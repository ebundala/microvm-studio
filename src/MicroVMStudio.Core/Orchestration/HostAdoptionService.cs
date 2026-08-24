using Grpc.Core;
using MicroVMStudio.Core.Data;
using MicroVMStudio.Core.Helper;
using MicroVMStudio.Core.Models;
using MicroVMStudio.Core.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mvm.Helper.V1;

namespace MicroVMStudio.Core.Orchestration;

/// <summary>
/// Reconciles the database against the host once, at startup.
///
/// This matters more here than in ShopCare: closing a desktop app is routine,
/// and jailer keeps running when it happens (reparented to init). So on every
/// launch, VMs marked Running may be genuinely alive — in which case we adopt
/// them and repopulate the routing table without rebooting anything — or dead,
/// in which case the reconciler needs to know.
///
/// The same pass diffs host veths and jailer chroots against the database to
/// surface resources that leaked from a crash mid-teardown.
/// </summary>
public sealed class HostAdoptionService(
    ISqliteWriter writer,
    HelperClient helper,
    VmRoutingTable routes,
    IServiceScopeFactory scopes,
    IOptions<StudioOptions> options,
    ILogger<HostAdoptionService> logger)
{
    private readonly StudioOptions _options = options.Value;

    public async Task<AdoptionReport> RunAsync(CancellationToken ct = default)
    {
        var adopted = new List<string>();
        var lost = new List<string>();

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();

            var running = await db.Vms.AsNoTracking()
                .Where(v => v.Status == VmStatus.Running)
                .Select(v => new { v.Id, v.Name, v.PrivateIp, v.FirecrackerPid })
                .ToListAsync(ct);

            foreach (var vm in running)
            {
                var alive = vm.FirecrackerPid is { } pid && await IsAliveAsync(pid, ct);

                if (alive && !string.IsNullOrEmpty(vm.PrivateIp))
                {
                    routes.Set(vm.Name, vm.Id, vm.PrivateIp);
                    adopted.Add(vm.Name);
                }
                else
                {
                    lost.Add(vm.Name);
                    await MarkCrashedAsync(vm.Id, ct);
                }
            }

            // A VM caught mid-boot by an app crash would otherwise sit in
            // Provisioning/Booting forever with a chroot that jailer refuses to
            // reuse. Reset to Pending so the reconciler retries from scratch.
            var stuck = await db.Vms.AsNoTracking()
                .Where(v => v.Status == VmStatus.Provisioning || v.Status == VmStatus.Booting)
                .Select(v => v.Id)
                .ToListAsync(ct);

            foreach (var id in stuck) await ResetToPendingAsync(id, ct);
        }

        var orphans = await FindOrphansAsync(ct);

        logger.LogInformation(
            "Host adoption complete: {Adopted} adopted, {Lost} found dead, {Veths} orphan veth(s), {Chroots} orphan chroot(s)",
            adopted.Count, lost.Count, orphans.Veths.Count, orphans.Chroots.Count);

        return new AdoptionReport(adopted, lost, orphans.Veths, orphans.Chroots);
    }

    /// <summary>
    /// Finds host resources with no corresponding database row. Reported rather
    /// than swept automatically: an unexplained netns might belong to something
    /// else on the machine, and deleting it silently would be worse than leaking.
    /// </summary>
    public async Task<OrphanReport> FindOrphansAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();

        var knownIds = await db.Vms.AsNoTracking().Select(v => v.Id).ToListAsync(ct);
        var knownPrefixes = knownIds
            .Select(id => id.Replace("-", "")[..8])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownIdSet = knownIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var veths = await helper.Client.ListHostVethsAsync(new Empty(), cancellationToken: ct);
        var orphanVeths = veths.Veths
            .Where(v => v.Name.StartsWith("vh-", StringComparison.Ordinal))
            .Where(v => !knownPrefixes.Contains(v.Name[3..]))
            .Select(v => v.Name)
            .ToList();

        var chroots = await helper.Client.ListJailerChrootsAsync(new ListJailerChrootsRequest
        {
            ChrootBaseDir = _options.ChrootBaseDir,
        }, cancellationToken: ct);
        var orphanChroots = chroots.Chroots
            .Where(c => !knownIdSet.Contains(c.VmId))
            .Select(c => new OrphanChroot(c.VmId, c.SizeBytes))
            .ToList();

        return new OrphanReport(orphanVeths, orphanChroots);
    }

    /// <summary>Removes the host resources that <see cref="FindOrphansAsync"/> reported.</summary>
    public async Task ReclaimAsync(OrphanReport orphans, CancellationToken ct = default)
    {
        var traceId = Guid.NewGuid().ToString("N")[..12];

        foreach (var veth in orphans.Veths)
        {
            try
            {
                await helper.Client.DeleteHostVethAsync(new DeleteHostVethRequest
                {
                    TraceId = traceId,
                    Name = veth,
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove orphan veth {Name}", veth);
            }
        }

        foreach (var chroot in orphans.Chroots)
        {
            try
            {
                await helper.Client.DeleteJailerChrootAsync(new DeleteJailerChrootRequest
                {
                    TraceId = traceId,
                    ChrootBaseDir = _options.ChrootBaseDir,
                    VmId = chroot.VmId,
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove orphan chroot {VmId}", chroot.VmId);
            }
        }

        logger.LogInformation("Reclaimed {Veths} veth(s) and {Chroots} chroot(s)",
            orphans.Veths.Count, orphans.Chroots.Count);
    }

    private async Task<bool> IsAliveAsync(long pid, CancellationToken ct)
    {
        try
        {
            var status = await helper.Client.ReadJailerStatusAsync(new ReadJailerStatusRequest
            {
                TraceId = "adopt",
                Pid = pid,
            }, cancellationToken: ct);
            return status.Alive;
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unavailable
                                                 || ex.StatusCode == Grpc.Core.StatusCode.DeadlineExceeded
                                                 || ex.StatusCode == Grpc.Core.StatusCode.ResourceExhausted)
        {
            // Transient gRPC errors (connection issues, timeouts) — do NOT mark the VM as dead.
            // Leave it in Running state; the reconciler will re-check on its next tick.
            logger.LogWarning(ex, "Transient gRPC error reading jailer status for pid {Pid}; skipping adoption for this VM.", pid);
            return true; // Assume alive to avoid falsely marking running VMs as crashed
        }
        catch (HelperUnavailableException ex)
        {
            // Helper not connected — same as above, don't mark as dead
            logger.LogWarning(ex, "Helper unavailable when reading jailer status for pid {Pid}; skipping adoption.", pid);
            return true;
        }
        catch (Exception ex)
        {
            // Any other error (e.g., process genuinely not found) — log and assume dead
            logger.LogWarning(ex, "Could not read jailer status for pid {Pid}; assuming dead.", pid);
            return false;
        }
    }

    private Task MarkCrashedAsync(string vmId, CancellationToken ct) =>
        writer.WriteAsync(async (db, c) =>
        {
            var vm = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (vm is null) return;
            vm.FirecrackerPid = null;
            await VmOrchestrator.TransitionAsync(db, vm, VmStatus.Crashed, "died_while_app_closed", c);
        }, ct);

    private Task ResetToPendingAsync(string vmId, CancellationToken ct) =>
        writer.WriteAsync(async (db, c) =>
        {
            var vm = await db.Vms.FirstOrDefaultAsync(v => v.Id == vmId, c);
            if (vm is null) return;
            vm.FirecrackerPid = null;
            await VmOrchestrator.TransitionAsync(db, vm, VmStatus.Pending, "boot_interrupted", c);
        }, ct);
}

public sealed record AdoptionReport(
    IReadOnlyList<string> Adopted,
    IReadOnlyList<string> Lost,
    IReadOnlyList<string> OrphanVeths,
    IReadOnlyList<OrphanChroot> OrphanChroots);

public sealed record OrphanReport(
    IReadOnlyList<string> Veths,
    IReadOnlyList<OrphanChroot> Chroots)
{
    public bool IsEmpty => Veths.Count == 0 && Chroots.Count == 0;
}

public sealed record OrphanChroot(string VmId, long SizeBytes);
