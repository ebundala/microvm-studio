using System.Collections.Concurrent;

namespace MicroVMStudio.Core.Runtime;

/// <summary>
/// Maps a VM name to the address the ingress proxy should forward to. Held in
/// memory and rebuilt at startup from VMs found to be genuinely running, so it
/// never disagrees with the host for long.
/// </summary>
public sealed class VmRoutingTable
{
    private readonly ConcurrentDictionary<string, RouteEntry> _routes =
        new(StringComparer.OrdinalIgnoreCase);

    public void Set(string vmName, string vmId, string privateIp) =>
        _routes[vmName] = new RouteEntry(vmId, privateIp);

    public void Remove(string vmName) => _routes.TryRemove(vmName, out _);

    public bool TryResolve(string vmName, out RouteEntry entry) =>
        _routes.TryGetValue(vmName, out entry!);

    public IReadOnlyDictionary<string, RouteEntry> Snapshot() => _routes;
}

public readonly record struct RouteEntry(string VmId, string PrivateIp);
