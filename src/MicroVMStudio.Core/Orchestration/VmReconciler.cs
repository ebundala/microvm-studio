using System.Collections.Concurrent;
using MicroVMStudio.Core.Data;
using MicroVMStudio.Core.Helper;
using MicroVMStudio.Core.Models;
using MicroVMStudio.Core.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mvm.Helper.V1;

namespace MicroVMStudio.Core.Orchestration;

/// <summary>
/// The single writer of Vm.Status. Every UI action sets DesiredStatus and
/// returns; this loop is what actually makes the host match.
///
/// Keeping intent and reality in separate columns is what makes the whole thing
/// crash-safe: if the app dies mid-boot, the next tick sees a VM stuck in
/// Provisioning with desired=Running and simply drives it forward again.
/// </summary>
public sealed class VmReconciler(
    VmOrchestrator orchestrator,
    ISqliteWriter writer,
    HelperClient helper,
    IServiceScopeFactory scopes,
    IOptions<StudioOptions> options,
    ILogger<VmReconciler> logger) : BackgroundService
{
    private readonly StudioOptions _options = options.Value;

    /// Guards orchestration, not database writes — those are already serialized
    /// by SqliteWriter. This only stops a slow boot from being started twice.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>Raised after every tick that changed something, so the UI can refresh.</summary>
    public event Action? Changed;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give the helper connection and the host adoption pass time to settle
        // before making any lifecycle decisions.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var period = TimeSpan.FromSeconds(_options.ReconcilerIntervalSeconds);
        using var timer = new PeriodicTimer(period);

        do
        {
            try
            {
                if (helper.IsConnected) await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconciler tick failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();

        var candidates = await db.Vms
            .AsNoTracking()
            .Where(v => v.DesiredStatus == VmDesiredStatus.Deleted
                     || v.Status != VmStatus.Stopped)
            .Select(v => new Candidate(
                v.Id, v.Name, v.Status, v.DesiredStatus, v.FirecrackerPid,
                v.RestartPolicy, v.RestartCount, v.LastRestartAt))
            .ToListAsync(ct);

        var work = candidates
            .Select(c => ActOnAsync(c, ct))
            .ToList();

        var acted = await Task.WhenAll(work);
        if (acted.Any(a => a)) Changed?.Invoke();
    }

    private async Task<bool> ActOnAsync(Candidate vm, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(vm.Id, _ => new SemaphoreSlim(1, 1));

        // A boot in flight already owns this VM; skip rather than queue, since
        // the next tick will re-evaluate against fresher state anyway.
        if (!await gate.WaitAsync(0, ct)) return false;

        try
        {
            return vm switch
            {
                { DesiredStatus: VmDesiredStatus.Deleted } => await DeleteAsync(vm, ct),

                { DesiredStatus: VmDesiredStatus.Running,
                  Status: VmStatus.Pending or VmStatus.Provisioning or VmStatus.Booting }
                    => await BootAsync(vm, ct),

                { Status: VmStatus.Stopping } => await StopAsync(vm, ct),

                // A Running VM that the user asked to stop still has to pass
                // through Stopping so teardown happens exactly once.
                { DesiredStatus: VmDesiredStatus.Stopped, Status: VmStatus.Running }
                    => await MarkStoppingAsync(vm, ct),

                { Status: VmStatus.Running } => await CheckLivenessAsync(vm, ct),

                { Status: VmStatus.Crashed } => await ApplyRestartPolicyAsync(vm, ct),

                _ => false,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> BootAsync(Candidate vm, CancellationToken ct)
    {
        logger.LogInformation("Reconciling VM {Name}: {Status} → Running", vm.Name, vm.Status);
        await orchestrator.ProvisionAndBootAsync(vm.Id, ct);
        return true;
    }

    private async Task<bool> StopAsync(Candidate vm, CancellationToken ct)
    {
        await orchestrator.TearDownAndStopAsync(vm.Id, ct);
        return true;
    }

    private async Task<bool> DeleteAsync(Candidate vm, CancellationToken ct)
    {
        await orchestrator.TearDownAndDeleteAsync(vm.Id, ct);
        return true;
    }

    private async Task<bool> MarkStoppingAsync(Candidate vm, CancellationToken ct)
    {
        await writer.WriteAsync(async (db, c) =>
        {
            var fresh = await db.Vms.FirstOrDefaultAsync(v => v.Id == vm.Id, c);
            if (fresh is null || fresh.Status != VmStatus.Running) return;
            await VmOrchestrator.TransitionAsync(db, fresh, VmStatus.Stopping, "stopping", c);
        }, ct);
        return true;
    }

    /// <summary>
    /// Detects a VM whose jailer died without us asking. Firecracker exiting is
    /// the only signal we get that a guest panicked or was killed externally.
    /// </summary>
    private async Task<bool> CheckLivenessAsync(Candidate vm, CancellationToken ct)
    {
        if (vm.FirecrackerPid is not { } pid) return false;

        var status = await helper.Client.ReadJailerStatusAsync(new ReadJailerStatusRequest
        {
            TraceId = Guid.NewGuid().ToString("N")[..12],
            Pid = pid,
        }, cancellationToken: ct);

        if (status.Alive) return false;

        logger.LogWarning("VM {Name} died unexpectedly (jailer pid {Pid}, exit {Code})",
            vm.Name, pid, status.ExitCode);

        await writer.WriteAsync(async (db, c) =>
        {
            var fresh = await db.Vms.FirstOrDefaultAsync(v => v.Id == vm.Id, c);
            if (fresh is null || fresh.Status != VmStatus.Running) return;

            fresh.FirecrackerPid = null;
            await VmOrchestrator.TransitionAsync(db, fresh, VmStatus.Crashed, "crashed", c);
        }, ct);

        return true;
    }

    /// <summary>
    /// Decides whether a crashed VM comes back. A VM that keeps dying is parked
    /// in CrashLoopBackoff rather than restarted forever — an image with a
    /// broken init would otherwise spin the host at the tick rate.
    /// </summary>
    private async Task<bool> ApplyRestartPolicyAsync(Candidate vm, CancellationToken ct)
    {
        if (vm.RestartPolicy == RestartPolicy.Never) return false;
        if (vm.DesiredStatus != VmDesiredStatus.Running) return false;

        var window = TimeSpan.FromSeconds(_options.RestartWindowSeconds);
        var withinWindow = vm.LastRestartAt is { } last && DateTime.UtcNow - last < window;
        var attempts = withinWindow ? vm.RestartCount : 0;

        if (attempts >= _options.CrashLoopThreshold)
        {
            logger.LogWarning(
                "VM {Name} crashed {Count} times within {Window}s; parking in CrashLoopBackoff. "
                + "Start it manually once the image is fixed.",
                vm.Name, attempts, _options.RestartWindowSeconds);

            await writer.WriteAsync(async (db, c) =>
            {
                var fresh = await db.Vms.FirstOrDefaultAsync(v => v.Id == vm.Id, c);
                if (fresh is null) return;
                fresh.DesiredStatus = VmDesiredStatus.Stopped;
                await VmOrchestrator.TransitionAsync(db, fresh, VmStatus.CrashLoopBackoff, "crash_loop", c);
            }, ct);
            return true;
        }

        // Teardown first: the netns, veth and chroot from the dead instance are
        // still on the host, and jailer refuses to reuse an existing chroot.
        // This leaves the VM at Pending, since DesiredStatus is still Running.
        await orchestrator.TearDownAndStopAsync(vm.Id, ct);

        await writer.WriteAsync(async (db, c) =>
        {
            var fresh = await db.Vms.FirstOrDefaultAsync(v => v.Id == vm.Id, c);
            if (fresh is null) return;

            fresh.RestartCount = attempts + 1;
            fresh.LastRestartAt = DateTime.UtcNow;
            fresh.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(c);
        }, ct);

        logger.LogInformation("Restarting VM {Name} (attempt {Attempt})", vm.Name, attempts + 1);
        return true;
    }

    private sealed record Candidate(
        string Id, string Name, VmStatus Status, VmDesiredStatus DesiredStatus,
        long? FirecrackerPid, RestartPolicy RestartPolicy, int RestartCount, DateTime? LastRestartAt);
}
