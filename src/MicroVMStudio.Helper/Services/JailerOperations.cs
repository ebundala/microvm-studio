using System.Diagnostics;
using System.Runtime.InteropServices;
using Mvm.Helper.V1;

namespace MicroVMStudio.Helper.Services;

/// <summary>
/// Jailer process control. Jailer sets up the chroot, drops to the per-VM
/// UID/GID, applies cgroup v2 limits, enters the network namespace, then execs
/// firecracker.
///
/// We track the *jailer* PID: it is the parent of the supervised process tree.
/// The app polls ReadJailerStatus to detect crashes rather than waiting here,
/// because jailer fork+execs into a different uid and cgroup.
/// </summary>
public sealed partial class JailerOperations(Shell shell, ILogger<JailerOperations> logger)
{
    public Task<LaunchJailerResponse> LaunchJailerAsync(LaunchJailerRequest req, CancellationToken ct)
    {
        // Unlike every other RPC here, launching is not naturally idempotent: a
        // retried or concurrent boot for the same VM would leave two live
        // Firecracker processes both trying to attach to the same netns tap0,
        // and the second always fails with EBUSY. Killing on process-table
        // ground truth (not a caller-supplied PID, which can lag or race) closes
        // this regardless of how the double-invocation arose.
        KillExistingFirecrackerForVm(req.VmId);

        var argv = BuildJailerArgs(req);

        var diagDir = Path.Combine(Path.GetDirectoryName(req.ChrootBaseDir.TrimEnd('/'))!, "diagnostics");
        Directory.CreateDirectory(diagDir);
        var diagPath = Path.Combine(diagDir, $"{req.VmId}-jailer.log");

        // Jailer creates the chroot from scratch and will abort before exec'ing
        // firecracker if it already exists (mknod /dev/net/tun returns EEXIST),
        // surfacing upstream as the misleading "fc_unreachable / connection
        // refused". Removing any stale tree from a prior failed boot first.
        TryDeleteChrootDirectory(req.ChrootBaseDir, req.VmId, req.TraceId, staleBootRelaunch: true);

        // Jailer is exec'd directly in the host namespace, NOT via `ip netns exec`:
        //   1. Jailer's own --netns flag already setns(2)s into the VM's namespace
        //      at the right moment — after cgroup setup, before exec'ing firecracker.
        //   2. Wrapping it in `ip netns exec` re-mounts /sys for the child, and the
        //      fresh sysfs has no populated /sys/fs/cgroup, so jailer dies with
        //      CgroupHierarchyMissing even though cgroup v2 is fine on the host.
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/local/bin/jailer",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,   // jailer redirects internally post-chroot
            RedirectStandardError = true,     // capture pre-chroot stderr for diagnostics
            Environment = { ["PATH"] = "/usr/local/sbin:/usr/local/bin:/sbin:/bin" },
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try { File.AppendAllText(diagPath, e.Data + "\n"); } catch { /* best-effort */ }
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException("Failed to start jailer process");
        }
        proc.BeginErrorReadLine();

        var pid = proc.Id;
        var chrootRoot = Path.Combine(req.ChrootBaseDir, "firecracker", req.VmId, "root");

        logger.LogInformation("Jailer launched: vm={Vm} pid={Pid} chroot={Chroot} trace={Trace}",
            req.VmId, pid, chrootRoot, req.TraceId);

        // Deliberately no WaitForExitAsync: the app polls ReadJailerStatus.
        return Task.FromResult(new LaunchJailerResponse { Pid = pid, ChrootRootPath = chrootRoot });
    }

    public Task KillJailerAsync(KillJailerRequest req, CancellationToken ct)
    {
        var pid = (int)req.Pid;
        try
        {
            var proc = Process.GetProcessById(pid);
            if (req.Graceful)
            {
                NativeMethods.Kill(pid, NativeMethods.SIGTERM);
                if (!proc.WaitForExit(10_000))
                {
                    NativeMethods.Kill(pid, NativeMethods.SIGKILL);
                }
            }
            else
            {
                NativeMethods.Kill(pid, NativeMethods.SIGKILL);
            }
            logger.LogInformation("Jailer killed pid={Pid} graceful={Graceful} trace={Trace}",
                pid, req.Graceful, req.TraceId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone — idempotent success.
            logger.LogDebug("Jailer pid={Pid} already exited; trace={Trace}", pid, req.TraceId);
        }
        return Task.CompletedTask;
    }

    public Task<ReadJailerStatusResponse> ReadJailerStatusAsync(
        ReadJailerStatusRequest req, CancellationToken ct)
    {
        try
        {
            var proc = Process.GetProcessById((int)req.Pid);
            return Task.FromResult(new ReadJailerStatusResponse
            {
                Alive = !proc.HasExited,
                ExitCode = proc.HasExited ? proc.ExitCode : 0,
            });
        }
        catch (ArgumentException)
        {
            // Process gone and already reaped; the exit code is unrecoverable.
            return Task.FromResult(new ReadJailerStatusResponse { Alive = false, ExitCode = -1 });
        }
    }

    public Task DeleteJailerChrootAsync(DeleteJailerChrootRequest req, CancellationToken ct)
    {
        TryDeleteChrootDirectory(req.ChrootBaseDir, req.VmId, req.TraceId, staleBootRelaunch: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Every per-VM chroot actually on disk, so the app can diff against its own
    /// Vm table and surface orphans left by a teardown that failed partway.
    /// </summary>
    public async Task<ListJailerChrootsResponse> ListJailerChrootsAsync(
        ListJailerChrootsRequest req, CancellationToken ct)
    {
        var response = new ListJailerChrootsResponse();
        var firecrackerDir = Path.Combine(req.ChrootBaseDir, "firecracker");
        if (!Directory.Exists(firecrackerDir)) return response;

        foreach (var dir in Directory.EnumerateDirectories(firecrackerDir))
        {
            ct.ThrowIfCancellationRequested();
            response.Chroots.Add(new JailerChroot
            {
                VmId = Path.GetFileName(dir),
                SizeBytes = await GetDirectorySizeBytesAsync(dir, ct),
            });
        }
        return response;
    }

    // `du -s --block-size=1` gives real on-disk usage with hard links counted
    // once. Not `-b`/`--apparent-size`: the chroot's data.img is a hard link to a
    // sparse file whose logical size is the full provisioned capacity, which
    // would wildly overstate what deleting the chroot actually frees.
    private async Task<long> GetDirectorySizeBytesAsync(string dir, CancellationToken ct)
    {
        var r = await shell.RunAsync("/usr/bin/du", ["-s", "--block-size=1", dir], ct: ct);
        if (!r.Succeeded) return 0;
        var tab = r.Stdout.IndexOf('\t');
        return tab >= 0 && long.TryParse(r.Stdout[..tab], out var bytes) ? bytes : 0;
    }

    private void TryDeleteChrootDirectory(
        string chrootBaseDir, string vmId, string traceId, bool staleBootRelaunch)
    {
        var chroot = Path.Combine(chrootBaseDir, "firecracker", vmId);
        if (!Directory.Exists(chroot)) return;

        try
        {
            Directory.Delete(chroot, recursive: true);
            logger.LogInformation("Removed jailer chroot: vm={Vm} path={Path} reason={Reason} trace={Trace}",
                vmId, chroot, staleBootRelaunch ? "stale-before-relaunch" : "vm-deleted", traceId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, staleBootRelaunch
                ? "Failed to remove stale jailer chroot {Path}; jailer will likely fail with EEXIST."
                : "Failed to remove jailer chroot {Path}; orphaned on host — sweep via ListJailerChroots.",
                chroot);
        }
    }

    private static List<string> BuildJailerArgs(LaunchJailerRequest req)
    {
        var args = new List<string>
        {
            "--id", req.VmId,
            "--exec-file", req.FirecrackerBinaryPath,
            "--uid", req.Uid.ToString(),
            "--gid", req.Gid.ToString(),
            "--chroot-base-dir", req.ChrootBaseDir,
            "--netns", $"/var/run/netns/{req.NetnsName}",
            // Modern distros mount /sys/fs/cgroup as v2-only. Without this flag
            // jailer probes v1 first and dies with CgroupHierarchyMissing. Pinned
            // explicitly so behaviour doesn't depend on the binary's default.
            "--cgroup-version", "2",
        };

        if (req.Cgroup is { } c)
        {
            if (c.Vcpu > 0)
            {
                // cpu.max is "<quota> <period>" in microseconds.
                args.AddRange(["--cgroup", $"cpu.max={c.Vcpu * 100000} 100000"]);
            }
            if (c.MemoryBytes > 0) args.AddRange(["--cgroup", $"memory.max={c.MemoryBytes}"]);
            if (c.PidsMax > 0) args.AddRange(["--cgroup", $"pids.max={c.PidsMax}"]);
            if ((c.IoReadBps > 0 || c.IoWriteBps > 0) && !string.IsNullOrEmpty(c.IoDeviceMajorMinor))
            {
                var parts = new List<string> { c.IoDeviceMajorMinor };
                if (c.IoReadBps > 0) parts.Add($"rbps={c.IoReadBps}");
                if (c.IoWriteBps > 0) parts.Add($"wbps={c.IoWriteBps}");
                args.AddRange(["--cgroup", $"io.max={string.Join(' ', parts)}"]);
            }
        }

        // Everything after `--` is forwarded to firecracker itself.
        args.Add("--");
        args.AddRange(req.FirecrackerArgs.Count > 0
            ? req.FirecrackerArgs
            : ["--api-sock", "/run/firecracker.socket"]);
        return args;
    }

    /// <summary>
    /// Kills any process whose cmdline identifies it as this VM's Firecracker.
    /// Jailer execs into firecracker, so the PID's cmdline is firecracker's argv.
    /// </summary>
    private void KillExistingFirecrackerForVm(string vmId)
    {
        var marker = $"--id {vmId}";
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out var pid)) continue;

            string cmdline;
            try
            {
                cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' ');
            }
            catch
            {
                continue;   // exited mid-scan, or a kernel thread — skip
            }

            if (!cmdline.Contains(marker)) continue;

            try
            {
                NativeMethods.Kill(pid, NativeMethods.SIGKILL);
                logger.LogWarning(
                    "Killed pre-existing firecracker pid={Pid} for vm={Vm} before relaunch "
                    + "(stale process from a prior boot attempt still held netns resources).",
                    pid, vmId);
            }
            catch { /* raced with its own exit — fine */ }
        }
    }

    private static partial class NativeMethods
    {
        public const int SIGTERM = 15;
        public const int SIGKILL = 9;

        [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
        public static partial int Kill(int pid, int sig);
    }
}
