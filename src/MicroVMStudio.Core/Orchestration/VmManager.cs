using System.Text.RegularExpressions;
using MicroVMStudio.Core.Data;
using MicroVMStudio.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MicroVMStudio.Core.Orchestration;

/// <summary>
/// What the UI calls. Every lifecycle method here writes DesiredStatus and
/// returns immediately — the reconciler is the only thing that touches the host.
/// That split is what keeps the UI responsive during a 30-second boot and makes
/// closing the app mid-operation safe.
/// </summary>
public sealed partial class VmManager(
    ISqliteWriter writer,
    VmOrchestrator orchestrator,
    SecretsProtector protector,
    IServiceScopeFactory scopes,
    IOptions<StudioOptions> options)
{
    private readonly StudioOptions _options = options.Value;

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$")]
    private static partial Regex NameRegex { get; }

    [GeneratedRegex(@"^[A-Z_][A-Z0-9_]{0,63}$")]
    private static partial Regex SecretKeyRegex { get; }

    public async Task<IReadOnlyList<Vm>> ListAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        return await db.Vms.AsNoTracking()
            .Include(v => v.Disk)
            .OrderBy(v => v.Name)
            .ToListAsync(ct);
    }

    public async Task<Vm?> GetAsync(string vmId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        return await db.Vms.AsNoTracking()
            .Include(v => v.Disk)
            .Include(v => v.EgressRules)
            .FirstOrDefaultAsync(v => v.Id == vmId, ct);
    }

    public async Task<IReadOnlyList<VmEvent>> GetEventsAsync(
        string vmId, int limit = 100, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        return await db.Events.AsNoTracking()
            .Where(e => e.VmId == vmId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<string> CreateAsync(CreateVmRequest request, CancellationToken ct = default)
    {
        if (!NameRegex.IsMatch(request.Name))
        {
            throw new ArgumentException(
                "Name must be 1-32 lowercase letters, digits or hyphens, and cannot start or end with a hyphen. "
                + "It appears in the proxy URL, so the character set is not negotiable.");
        }

        if (request.Vcpu is < 1 or > 32) throw new ArgumentException("vCPU must be between 1 and 32.");
        if (request.MemoryMib is < 128 or > 65536) throw new ArgumentException("Memory must be 128–65536 MiB.");
        if (request.PrimaryPort is < 1 or > 65535) throw new ArgumentException("Primary port is out of range.");

        var vmId = Guid.NewGuid().ToString();

        return await writer.WriteAsync(async (db, c) =>
        {
            if (await db.Vms.AnyAsync(v => v.Name == request.Name, c))
            {
                throw new InvalidOperationException($"A VM named '{request.Name}' already exists.");
            }

            var now = DateTime.UtcNow;
            VmDisk disk;

            if (request.AdoptDiskId is { } adoptId)
            {
                // Adopting an orphan is how a deleted VM's data comes back: the
                // file is untouched, only the ownership row changes.
                disk = await db.Disks.FirstOrDefaultAsync(d => d.Id == adoptId, c)
                    ?? throw new InvalidOperationException($"Disk {adoptId} not found.");

                if (disk.State != DiskState.Orphaned)
                {
                    throw new InvalidOperationException("That disk is still attached to another VM.");
                }

                disk.VmId = vmId;
                disk.State = DiskState.InUse;
                disk.LastAttachedAt = now;
            }
            else
            {
                if (request.DiskSizeGib is < 1 or > 1024)
                {
                    throw new ArgumentException("Disk size must be between 1 and 1024 GiB.");
                }

                disk = new VmDisk
                {
                    Id = Guid.NewGuid().ToString(),
                    VmId = vmId,
                    PathOnHost = Path.Combine(_options.DisksDir, $"{vmId}.img"),
                    Format = DiskFormat.Raw,
                    SizeGib = request.DiskSizeGib,
                    State = DiskState.InUse,
                    CreatedAt = now,
                    LastAttachedAt = now,
                };
                db.Disks.Add(disk);
            }

            db.Vms.Add(new Vm
            {
                Id = vmId,
                Name = request.Name,
                Status = VmStatus.Pending,
                DesiredStatus = request.StartImmediately ? VmDesiredStatus.Running : VmDesiredStatus.Stopped,
                DiskId = disk.Id,
                Vcpu = request.Vcpu,
                MemoryMib = request.MemoryMib,
                KernelImageId = request.KernelImageId,
                RootfsImageId = request.RootfsImageId,
                PrimaryPort = request.PrimaryPort,
                RestartPolicy = request.RestartPolicy,
                CreatedAt = now,
                UpdatedAt = now,
                LastStateChangeAt = now,
            });

            db.Events.Add(new VmEvent
            {
                Id = Guid.NewGuid().ToString(),
                VmId = vmId,
                EventType = "created",
                NewStatus = VmStatus.Pending,
                OccurredAt = now,
            });

            await db.SaveChangesAsync(c);
            return vmId;
        }, ct);
    }

    public Task StartAsync(string vmId, CancellationToken ct = default) =>
        SetDesiredAsync(vmId, VmDesiredStatus.Running, vm =>
        {
            // A VM parked in CrashLoopBackoff or BootFailed needs its counters
            // cleared, otherwise the very first crash re-parks it immediately.
            if (vm.Status is VmStatus.CrashLoopBackoff or VmStatus.BootFailed or VmStatus.Crashed)
            {
                vm.RestartCount = 0;
                vm.LastRestartAt = null;
                vm.Status = VmStatus.Pending;
            }
        }, ct);

    public Task StopAsync(string vmId, CancellationToken ct = default) =>
        SetDesiredAsync(vmId, VmDesiredStatus.Stopped, null, ct);

    /// <summary>
    /// Restart is a stop that leaves the intent at Running: teardown parks the VM
    /// at Pending instead of Stopped, and the next tick boots it again.
    /// </summary>
    public Task RestartAsync(string vmId, CancellationToken ct = default) =>
        writer.WriteAsync(async (db, c) =>
        {
            var vm = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (vm is null) return;

            vm.DesiredStatus = VmDesiredStatus.Running;
            vm.RestartCount = 0;
            vm.LastRestartAt = null;

            var next = vm.Status == VmStatus.Running ? VmStatus.Stopping : VmStatus.Pending;
            await VmOrchestrator.TransitionAsync(db, vm, next, "restart_requested", c);
        }, ct);

    public Task DeleteAsync(string vmId, bool purgeDisk, CancellationToken ct = default) =>
        writer.WriteAsync(async (db, c) =>
        {
            var vm = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (vm is null) return;

            // The purge choice is made here but acted on by a later reconciler
            // tick in a different scope, so it has to be persisted, not passed.
            vm.PurgeDiskOnDelete = purgeDisk;
            vm.DesiredStatus = VmDesiredStatus.Deleted;
            vm.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(c);
        }, ct);

    // ── Secrets ─────────────────────────────────────────────────────────────

    /// <summary>Keys only. Values are never read back into the UI once written.</summary>
    public async Task<IReadOnlyList<SecretSummary>> ListSecretsAsync(
        string vmId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        return await db.Secrets.AsNoTracking()
            .Where(s => s.VmId == vmId)
            .OrderBy(s => s.Key)
            .Select(s => new SecretSummary(s.Id, s.Key, s.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task SetSecretAsync(string vmId, string key, string value, CancellationToken ct = default)
    {
        if (!SecretKeyRegex.IsMatch(key))
        {
            throw new ArgumentException(
                "Secret keys must be uppercase letters, digits and underscores, starting with a letter or "
                + "underscore — the guest exports them as environment variables.");
        }

        await writer.WriteAsync(async (db, c) =>
        {
            var existing = await db.Secrets.FirstOrDefaultAsync(s => s.VmId == vmId && s.Key == key, c);
            var now = DateTime.UtcNow;

            if (existing is null)
            {
                db.Secrets.Add(new VmSecret
                {
                    Id = Guid.NewGuid().ToString(),
                    VmId = vmId,
                    Key = key,
                    ValueEncrypted = protector.Protect(value),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.ValueEncrypted = protector.Protect(value);
                existing.UpdatedAt = now;
            }

            await db.SaveChangesAsync(c);
        }, ct);

        // Push straight away: the guest polls MMDS every 10s, so a running VM
        // picks the change up without a restart.
        await orchestrator.RefreshSecretsAsync(vmId, ct);
    }

    public async Task DeleteSecretAsync(string vmId, string secretId, CancellationToken ct = default)
    {
        await writer.WriteAsync(async (db, c) =>
        {
            var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == secretId, c);
            if (secret is null) return;
            db.Secrets.Remove(secret);
            await db.SaveChangesAsync(c);
        }, ct);

        await orchestrator.RefreshSecretsAsync(vmId, ct);
    }

    // ── Egress rules ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<VmEgressRule>> ListEgressRulesAsync(
        string vmId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        return await db.EgressRules.AsNoTracking()
            .Where(r => r.VmId == vmId)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);
    }

    public async Task ReplaceEgressRulesAsync(
        string vmId, IReadOnlyList<EgressRuleInput> rules, CancellationToken ct = default)
    {
        foreach (var rule in rules)
        {
            if (!IsValidCidr(rule.Cidr)) throw new ArgumentException($"'{rule.Cidr}' is not a valid CIDR.");
        }

        await writer.WriteAsync(async (db, c) =>
        {
            var existing = await db.EgressRules.Where(r => r.VmId == vmId).ToListAsync(c);
            db.EgressRules.RemoveRange(existing);

            foreach (var rule in rules)
            {
                db.EgressRules.Add(new VmEgressRule
                {
                    Id = Guid.NewGuid().ToString(),
                    VmId = vmId,
                    Cidr = rule.Cidr,
                    Effect = rule.Effect,
                    Priority = rule.Priority,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync(c);
        }, ct);

        await orchestrator.ApplyEgressRulesAsync(vmId, ct);
    }

    // ── Disks ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<VmDisk>> ListDisksAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        return await db.Disks.AsNoTracking().OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
    }

    private Task SetDesiredAsync(
        string vmId, VmDesiredStatus desired, Action<Vm>? mutate, CancellationToken ct) =>
        writer.WriteAsync(async (db, c) =>
        {
            var vm = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (vm is null) return;

            vm.DesiredStatus = desired;
            vm.UpdatedAt = DateTime.UtcNow;
            mutate?.Invoke(vm);

            db.Events.Add(new VmEvent
            {
                Id = Guid.NewGuid().ToString(),
                VmId = vmId,
                EventType = desired == VmDesiredStatus.Running ? "start_requested" : "stop_requested",
                PrevStatus = vm.Status,
                NewStatus = vm.Status,
                OccurredAt = DateTime.UtcNow,
            });

            await db.SaveChangesAsync(c);
        }, ct);

    private static bool IsValidCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0) return false;
        return System.Net.IPAddress.TryParse(cidr[..slash], out _)
            && int.TryParse(cidr[(slash + 1)..], out var prefix)
            && prefix is >= 0 and <= 32;
    }
}

public sealed record CreateVmRequest
{
    public required string Name { get; init; }
    public required string KernelImageId { get; init; }
    public required string RootfsImageId { get; init; }
    public short Vcpu { get; init; } = 1;
    public int MemoryMib { get; init; } = 512;
    public int PrimaryPort { get; init; } = 8080;
    public int DiskSizeGib { get; init; } = 5;

    /// Adopt an orphaned disk instead of creating one, keeping its data.
    public string? AdoptDiskId { get; init; }

    public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.OnFailure;
    public bool StartImmediately { get; init; } = true;
}

public sealed record SecretSummary(string Id, string Key, DateTime UpdatedAt);

public sealed record EgressRuleInput(string Cidr, EgressEffect Effect, int Priority);
