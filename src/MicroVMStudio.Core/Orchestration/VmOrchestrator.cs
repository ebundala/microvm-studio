using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using MicroVMStudio.Core.Data;
using MicroVMStudio.Core.Helper;
using MicroVMStudio.Core.Models;
using MicroVMStudio.Core.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mvm.Helper.V1;

namespace MicroVMStudio.Core.Orchestration;

/// <summary>
/// Drives a VM between states. Nothing else writes Vm.Status: callers express
/// intent by setting DesiredStatus, and the reconciler invokes the methods here
/// to make reality match.
///
///     Pending → Provisioning → Booting → Running        (happy path)
///     Running → Stopping → Stopped                      (user stop)
///     any     → BootFailed                              (provisioning error)
///     Running → Crashed → Pending                       (crash + restart policy)
/// </summary>
public sealed class VmOrchestrator(
    ISqliteWriter writer,
    HelperClient helper,
    ResourceAllocator allocator,
    SecretsProtector protector,
    VmRoutingTable routes,
    VmServiceRegistry services,
    IOptions<StudioOptions> options,
    ILogger<VmOrchestrator> logger)
{
    private readonly StudioOptions _options = options.Value;

    /// <summary>
    /// Takes a VM from Pending to Running. Idempotent: a VM already at Running
    /// or beyond is left alone, so a duplicated reconciler tick is harmless.
    /// </summary>
    public async Task ProvisionAndBootAsync(string vmId, CancellationToken ct)
    {
        var traceId = Guid.NewGuid().ToString("N")[..12];

        try
        {
            // ── 1. Allocate host-unique resources ───────────────────────────
            var plan = await writer.WriteAsync(async (db, c) =>
            {
                var vm = await db.Vms.Include(v => v.Disk).FirstOrDefaultAsync(v => v.Id == vmId, c)
                    ?? throw new InvalidOperationException($"VM {vmId} no longer exists.");

                if (vm.Status >= VmStatus.Running) return null;

                if (vm.VsockCid == 0) vm.VsockCid = await allocator.AllocateVsockCidAsync(db, c);
                if (vm.RuntimeUid == 0)
                {
                    vm.RuntimeUid = await allocator.AllocateRuntimeUidAsync(db, c);
                    vm.RuntimeGid = vm.RuntimeUid;
                }

                var ip = await allocator.AllocateIpAsync(db, vmId, c);
                vm.PrivateIp = ip.PrivateIp;
                vm.NetnsName = $"mvm-{vmId.Replace("-", "")[..8]}";

                await TransitionAsync(db, vm, VmStatus.Provisioning, "provisioning", c);

                var kernel = await db.Images.FirstAsync(i => i.Id == vm.KernelImageId, c);
                var rootfs = await db.Images.FirstAsync(i => i.Id == vm.RootfsImageId, c);
                var secrets = await db.Secrets.Where(s => s.VmId == vmId).ToListAsync(c);

                return new BootPlan(
                    vm.Id, vm.Name, vm.PrivateIp!, vm.NetnsName!, vm.VsockCid,
                    vm.RuntimeUid, vm.RuntimeGid, vm.Vcpu, vm.MemoryMib, vm.PrimaryPort,
                    kernel.PathOnHost, rootfs.PathOnHost,
                    vm.Disk.Id, vm.Disk.PathOnHost, vm.Disk.SizeGib,
                    secrets.ToDictionary(s => s.Key, s => protector.Unprotect(s.ValueEncrypted)));
            }, ct);

            if (plan is null) return;   // already running

            // ── 2. Host networking ──────────────────────────────────────────
            await helper.Client.EnsureHostBridgeAsync(new EnsureHostBridgeRequest
            {
                TraceId = traceId,
                BridgeName = _options.BridgeName,
                Cidr = _options.BridgeCidr,
            }, cancellationToken: ct);

            var netns = await helper.Client.CreateVmNetnsAsync(new CreateVmNetnsRequest
            {
                TraceId = traceId,
                VmId = plan.VmId,
                NetnsName = plan.NetnsName,
                BridgeName = _options.BridgeName,
                PrivateIpCidr = $"{plan.PrivateIp}/{allocator.PrefixLength}",
                GatewayIp = allocator.GatewayIp,
                RuntimeUid = (uint)plan.RuntimeUid,
                RuntimeGid = (uint)plan.RuntimeGid,
            }, cancellationToken: ct);

            // ── 3. Disk ─────────────────────────────────────────────────────
            await helper.Client.CreateDiskImageAsync(new CreateDiskImageRequest
            {
                TraceId = traceId,
                DiskId = plan.DiskId,
                PathOnHost = plan.DiskPath,
                Format = CreateDiskImageRequest.Types.Format.Raw,
                SizeGib = plan.DiskSizeGib,
                OwnerUid = (uint)plan.RuntimeUid,
                OwnerGid = (uint)plan.RuntimeGid,
            }, cancellationToken: ct);

            // ── 4. Launch jailer ────────────────────────────────────────────
            await UpdateStatusAsync(vmId, VmStatus.Booting, "booting", ct);

            var launch = await helper.Client.LaunchJailerAsync(new LaunchJailerRequest
            {
                TraceId = traceId,
                VmId = plan.VmId,
                FirecrackerBinaryPath = _options.FirecrackerBinaryPath,
                NetnsName = plan.NetnsName,
                Uid = (uint)plan.RuntimeUid,
                Gid = (uint)plan.RuntimeGid,
                ChrootBaseDir = _options.ChrootBaseDir,
                Cgroup = new CgroupLimits
                {
                    Vcpu = plan.Vcpu,
                    MemoryBytes = (long)plan.MemoryMib * 1024 * 1024,
                    PidsMax = 1024,
                },
            }, cancellationToken: ct);

            await writer.WriteAsync(async (db, c) =>
            {
                var vm = await db.Vms.FirstAsync(v => v.Id == vmId, c);
                vm.FirecrackerPid = launch.Pid;
                vm.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(c);
            }, ct);

            // ── 5. Link kernel, rootfs and data disk into the chroot ────────
            // Kernel and rootfs are hard-linked, so every VM using an image
            // shares one inode — which is exactly why the rootfs drive is
            // attached read-only below.
            await LinkAsync(traceId, plan.DiskId, plan.KernelPath, launch.ChrootRootPath, "vmlinux.bin", ct);
            await LinkAsync(traceId, plan.DiskId, plan.RootfsPath, launch.ChrootRootPath, "rootfs.img", ct);
            await LinkAsync(traceId, plan.DiskId, plan.DiskPath, launch.ChrootRootPath, "data.img", ct);

            // ── 6. Configure and start the VM ───────────────────────────────
            await ConfigureFirecrackerAsync(plan, netns.TapName, traceId, ct);

            // ── 7. Wait for the workload to answer ──────────────────────────
            if (!await WaitForListenerAsync(plan.PrivateIp, plan.PrimaryPort, ct))
            {
                throw new BootFailedException("boot_timeout",
                    $"VM did not accept connections on {plan.PrivateIp}:{plan.PrimaryPort} "
                    + $"within {_options.BootTimeoutSeconds}s. Check the Logs tab for guest console output.");
            }

            await UpdateStatusAsync(vmId, VmStatus.Running, "running", ct);
            routes.Set(plan.Name, plan.VmId, plan.PrivateIp);

            logger.LogInformation("VM {Name} running: ip={Ip}:{Port} netns={Netns} pid={Pid}",
                plan.Name, plan.PrivateIp, plan.PrimaryPort, plan.NetnsName, launch.Pid);
        }
        catch (BootFailedException ex)
        {
            await FailAsync(vmId, ex.Code, ex.Message, ct);
        }
        catch (Grpc.Core.RpcException ex)
        {
            await FailAsync(vmId, "helper_rpc_failed", ex.Status.Detail, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(vmId, "boot_error", ex.Message, ct);
        }
    }

    /// <summary>
    /// Tears the VM's host resources down and parks it at Stopped — or back at
    /// Pending if the user asked for it to start again mid-teardown.
    /// </summary>
    public async Task TearDownAndStopAsync(string vmId, CancellationToken ct)
    {
        var traceId = Guid.NewGuid().ToString("N")[..12];

        var vm = await LoadAsync(vmId, ct);
        if (vm is null) return;

        await TearDownRuntimeAsync(vm, traceId, ct);

        await writer.WriteAsync(async (db, c) =>
        {
            var fresh = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (fresh is null) return;

            // The user may have hit Start again while teardown was in flight;
            // honour the newer intent rather than parking at Stopped.
            var next = fresh.DesiredStatus == VmDesiredStatus.Running
                ? VmStatus.Pending
                : VmStatus.Stopped;

            fresh.FirecrackerPid = null;
            await TransitionAsync(db, fresh, next, next == VmStatus.Pending ? "restarting" : "stopped", c);
        }, ct);

        routes.Remove(vm.Name);
        services.Remove(vmId);
    }

    /// <summary>Tears down host resources and removes the VM permanently.</summary>
    public async Task TearDownAndDeleteAsync(string vmId, CancellationToken ct)
    {
        var traceId = Guid.NewGuid().ToString("N")[..12];

        var vm = await LoadAsync(vmId, ct);
        if (vm is null) return;

        await TearDownRuntimeAsync(vm, traceId, ct);

        // Remove the chroot too: unlike a stop, nothing is coming back for it.
        await TryHelperAsync(() => helper.Client.DeleteJailerChrootAsync(new DeleteJailerChrootRequest
        {
            TraceId = traceId,
            ChrootBaseDir = _options.ChrootBaseDir,
            VmId = vmId,
        }, cancellationToken: ct).ResponseAsync, "DeleteJailerChroot", vmId);

        var purgeDisk = vm.PurgeDiskOnDelete == true;
        if (purgeDisk)
        {
            await TryHelperAsync(() => helper.Client.DeleteDiskImageAsync(new DeleteDiskImageRequest
            {
                TraceId = traceId,
                DiskId = vm.DiskId,
                PathOnHost = vm.Disk.PathOnHost,
                SecureOverwrite = false,
            }, cancellationToken: ct).ResponseAsync, "DeleteDiskImage", vmId);
        }

        await writer.WriteAsync(async (db, c) =>
        {
            var fresh = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (fresh is null) return;

            var disk = await db.Disks.FirstOrDefaultAsync(d => d.Id == fresh.DiskId, c);
            if (disk is not null)
            {
                if (purgeDisk)
                {
                    db.Disks.Remove(disk);
                }
                else
                {
                    // Keep the data: the disk outlives its VM and can be adopted
                    // by a new one from the create dialog.
                    disk.VmId = null;
                    disk.State = DiskState.Orphaned;
                }
            }

            var ip = await db.IpAllocations.FirstOrDefaultAsync(a => a.VmId == vmId, c);
            if (ip is not null) ResourceAllocator.ReleaseIp(ip, _options.IpQuarantineMinutes);

            db.Vms.Remove(fresh);
            await db.SaveChangesAsync(c);
        }, ct);

        routes.Remove(vm.Name);
        services.Remove(vmId);
        logger.LogInformation("VM {Name} deleted (disk {Fate})", vm.Name, purgeDisk ? "purged" : "kept");
    }

    /// <summary>
    /// Re-pushes the MMDS document for a running VM so secret changes reach the
    /// guest without a restart — its init polls MMDS every 10 seconds.
    /// </summary>
    public async Task RefreshSecretsAsync(string vmId, CancellationToken ct)
    {
        var vm = await LoadAsync(vmId, ct);
        if (vm is null || vm.Status != VmStatus.Running) return;

        var secrets = await LoadSecretsAsync(vmId, ct);
        var traceId = Guid.NewGuid().ToString("N")[..12];

        await CallFcAsync(vmId, traceId, "PUT", "/mmds",
            JsonSerializer.Serialize(BuildMmdsDocument(vmId, vm.Name, secrets)), ct);

        logger.LogInformation("Refreshed MMDS secrets for VM {Name} ({Count} entries)",
            vm.Name, secrets.Count);
    }

    /// <summary>Applies a VM's egress rules to the host firewall.</summary>
    public async Task ApplyEgressRulesAsync(string vmId, CancellationToken ct)
    {
        var vm = await LoadAsync(vmId, ct);
        if (vm is null || string.IsNullOrEmpty(vm.PrivateIp)) return;

        var request = new ApplyVmEgressRulesRequest
        {
            TraceId = Guid.NewGuid().ToString("N")[..12],
            VmId = vmId,
            PrivateIp = vm.PrivateIp,
        };

        foreach (var rule in vm.EgressRules.OrderBy(r => r.Priority))
        {
            request.Rules.Add(new CidrRule
            {
                Cidr = rule.Cidr,
                Effect = rule.Effect == EgressEffect.Allow
                    ? CidrRule.Types.Effect.Allow
                    : CidrRule.Types.Effect.Deny,
                Priority = rule.Priority,
            });
        }

        await helper.Client.ApplyVmEgressRulesAsync(request, cancellationToken: ct);
    }

    // ── internals ───────────────────────────────────────────────────────────

    private async Task TearDownRuntimeAsync(Vm vm, string traceId, CancellationToken ct)
    {
        if (vm.FirecrackerPid is { } pid)
        {
            await TryHelperAsync(() => helper.Client.KillJailerAsync(new KillJailerRequest
            {
                TraceId = traceId,
                Pid = pid,
                Graceful = true,
            }, cancellationToken: ct).ResponseAsync, "KillJailer", vm.Id);
        }

        if (!string.IsNullOrEmpty(vm.NetnsName))
        {
            await TryHelperAsync(() => helper.Client.DestroyVmNetnsAsync(new DestroyVmNetnsRequest
            {
                TraceId = traceId,
                NetnsName = vm.NetnsName,
                HostVethName = $"vh-{vm.Id.Replace("-", "")[..8]}",
            }, cancellationToken: ct).ResponseAsync, "DestroyVmNetns", vm.Id);
        }
    }

    private async Task ConfigureFirecrackerAsync(
        BootPlan plan, string tapName, string traceId, CancellationToken ct)
    {
        await CallFcAsync(plan.VmId, traceId, "PUT", "/machine-config", JsonSerializer.Serialize(new
        {
            vcpu_count = (int)plan.Vcpu,
            mem_size_mib = plan.MemoryMib,
            track_dirty_pages = false,
        }), ct);

        // The guest init parses these from /proc/cmdline. Values must not contain
        // spaces; the VM name regex already guarantees that.
        var bootArgs = "console=ttyS0 reboot=k panic=1 pci=off nomodules"
                     + $" agent_cid={plan.VsockCid}"
                     + $" agent_ip={plan.PrivateIp}/{allocator.PrefixLength}"
                     + $" agent_gw={allocator.GatewayIp}"
                     + $" agent_id={plan.VmId}"
                     + $" agent_name={plan.Name}"
                     + $" agent_port={plan.PrimaryPort}";

        await CallFcAsync(plan.VmId, traceId, "PUT", "/boot-source", JsonSerializer.Serialize(new
        {
            kernel_image_path = "/vmlinux.bin",
            boot_args = bootArgs,
        }), ct);

        // Read-only because this inode is shared by every VM using the image.
        // The guest gets a writable root by overlaying the data disk on top.
        await CallFcAsync(plan.VmId, traceId, "PUT", "/drives/rootfs", JsonSerializer.Serialize(new
        {
            drive_id = "rootfs",
            path_on_host = "/rootfs.img",
            is_root_device = true,
            is_read_only = true,
        }), ct);

        // Attached second, so the guest sees it as /dev/vdb — drives appear in
        // the order they are PUT.
        await CallFcAsync(plan.VmId, traceId, "PUT", "/drives/data", JsonSerializer.Serialize(new
        {
            drive_id = "data",
            path_on_host = "/data.img",
            is_root_device = false,
            is_read_only = false,
        }), ct);

        await CallFcAsync(plan.VmId, traceId, "PUT", "/network-interfaces/eth0", JsonSerializer.Serialize(new
        {
            iface_id = "eth0",
            host_dev_name = tapName,
            guest_mac = GenerateMac(plan.VmId),
        }), ct);

        await CallFcAsync(plan.VmId, traceId, "PUT", "/mmds/config", JsonSerializer.Serialize(new
        {
            version = "V2",
            network_interfaces = new[] { "eth0" },
        }), ct);

        await CallFcAsync(plan.VmId, traceId, "PUT", "/mmds",
            JsonSerializer.Serialize(BuildMmdsDocument(plan.VmId, plan.Name, plan.Secrets)), ct);

        await CallFcAsync(plan.VmId, traceId, "PUT", "/actions", JsonSerializer.Serialize(new
        {
            action_type = "InstanceStart",
        }), ct);
    }

    private object BuildMmdsDocument(string vmId, string vmName, IDictionary<string, string> secrets) => new
    {
        vm_id = vmId,
        vm_name = vmName,
        secrets,
        vm_secret_b64 = Convert.ToBase64String(DeriveVmSecret(vmId)),
        auth_version = "1",
    };

    /// <summary>
    /// Per-VM key for the identity headers the ingress proxy signs. Derived
    /// rather than stored so it is stable across restarts and never needs a
    /// separate lifecycle.
    /// </summary>
    public byte[] DeriveVmSecret(string vmId)
    {
        var master = GetOrCreateMasterSecret();
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32,
            salt: SHA256.HashData(Encoding.UTF8.GetBytes(vmId)),
            info: "mvm-vm-identity"u8.ToArray());
    }

    private byte[]? _masterSecret;

    private byte[] GetOrCreateMasterSecret()
    {
        if (_masterSecret is not null) return _masterSecret;

        var path = Path.Combine(Path.GetDirectoryName(_options.DatabasePath)!, "identity.key");
        if (File.Exists(path))
        {
            _masterSecret = File.ReadAllBytes(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _masterSecret = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, _masterSecret);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return _masterSecret;
    }

    private async Task CallFcAsync(
        string vmId, string traceId, string method, string path, string body, CancellationToken ct)
    {
        var response = await helper.Client.FirecrackerApiCallAsync(new FirecrackerApiCallRequest
        {
            TraceId = traceId,
            VmId = vmId,
            Method = method,
            Path = path,
            Body = ByteString.CopyFromUtf8(body),
            TimeoutMs = 10_000,
        }, cancellationToken: ct);

        if (!string.IsNullOrEmpty(response.ErrorCode))
        {
            throw new BootFailedException(response.ErrorCode,
                $"Firecracker {method} {path} failed: {response.Body.ToStringUtf8()}");
        }

        if (response.Status is < 200 or >= 300)
        {
            throw new BootFailedException("fc_rejected",
                $"Firecracker {method} {path} returned {response.Status}: {response.Body.ToStringUtf8()}");
        }
    }

    private Task LinkAsync(
        string traceId, string diskId, string source, string chrootRoot, string target, CancellationToken ct) =>
        helper.Client.LinkDiskIntoChrootAsync(new LinkDiskIntoChrootRequest
        {
            TraceId = traceId,
            DiskId = diskId,
            DiskHostPath = source,
            ChrootRootPath = chrootRoot,
            UseBindMount = false,
            TargetName = target,
        }, cancellationToken: ct).ResponseAsync;

    private async Task<bool> WaitForListenerAsync(string ip, int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_options.BootTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.ConnectAsync(System.Net.IPAddress.Parse(ip), port, attempt.Token);
                return true;
            }
            catch (SocketException) { /* not listening yet */ }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (OperationCanceledException) { /* per-attempt timeout */ }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        return false;
    }

    private async Task<Vm?> LoadAsync(string vmId, CancellationToken ct) =>
        await writer.WriteAsync(async (db, c) => await db.Vms
            .Include(v => v.Disk)
            .Include(v => v.EgressRules)
            .FirstOrDefaultAsync(v => v.Id == vmId, c), ct);

    private async Task<Dictionary<string, string>> LoadSecretsAsync(string vmId, CancellationToken ct) =>
        await writer.WriteAsync(async (db, c) =>
        {
            var rows = await db.Secrets.Where(s => s.VmId == vmId).ToListAsync(c);
            return rows.ToDictionary(s => s.Key, s => protector.Unprotect(s.ValueEncrypted));
        }, ct);

    private Task UpdateStatusAsync(string vmId, VmStatus status, string eventType, CancellationToken ct) =>
        writer.WriteAsync(async (db, c) =>
        {
            var vm = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (vm is not null) await TransitionAsync(db, vm, status, eventType, c);
        }, ct);

    private async Task FailAsync(string vmId, string code, string detail, CancellationToken ct)
    {
        logger.LogError("VM {VmId} boot failed [{Code}]: {Detail}", vmId, code, detail);

        await writer.WriteAsync(async (db, c) =>
        {
            var vm = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (vm is null) return;

            var prev = vm.Status;
            vm.Status = VmStatus.BootFailed;
            vm.LastStateChangeAt = DateTime.UtcNow;
            vm.UpdatedAt = DateTime.UtcNow;

            db.Events.Add(new VmEvent
            {
                Id = Guid.NewGuid().ToString(),
                VmId = vmId,
                EventType = "boot_failed",
                PrevStatus = prev,
                NewStatus = VmStatus.BootFailed,
                PayloadJson = JsonSerializer.Serialize(new { code, detail }),
                OccurredAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(c);
        }, CancellationToken.None);
    }

    internal static async Task TransitionAsync(
        StudioDbContext db, Vm vm, VmStatus next, string eventType, CancellationToken ct)
    {
        var prev = vm.Status;
        vm.Status = next;
        vm.LastStateChangeAt = DateTime.UtcNow;
        vm.UpdatedAt = DateTime.UtcNow;

        db.Events.Add(new VmEvent
        {
            Id = Guid.NewGuid().ToString(),
            VmId = vm.Id,
            EventType = eventType,
            PrevStatus = prev,
            NewStatus = next,
            OccurredAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs a teardown helper call that must not abort the rest of teardown.
    /// A half-removed VM is worse than a leaked veth, and the Host view's
    /// cleanup panel exists to sweep whatever leaks.
    /// </summary>
    private async Task TryHelperAsync(Func<Task> call, string operation, string vmId)
    {
        try
        {
            await call();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "{Operation} failed for VM {VmId} during teardown; continuing. "
                + "Any leaked host resources will show up in Host → cleanup.", operation, vmId);
        }
    }

    /// <summary>
    /// Deterministic locally-administered MAC, stable across restarts so the
    /// guest's DHCP-free static configuration and any ARP caches stay valid.
    /// </summary>
    private static string GenerateMac(string vmId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(vmId));
        return $"02:{hash[0]:x2}:{hash[1]:x2}:{hash[2]:x2}:{hash[3]:x2}:{hash[4]:x2}";
    }

    private sealed record BootPlan(
        string VmId, string Name, string PrivateIp, string NetnsName, int VsockCid,
        int RuntimeUid, int RuntimeGid, short Vcpu, int MemoryMib, int PrimaryPort,
        string KernelPath, string RootfsPath,
        string DiskId, string DiskPath, int DiskSizeGib,
        Dictionary<string, string> Secrets);
}

public sealed class BootFailedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
