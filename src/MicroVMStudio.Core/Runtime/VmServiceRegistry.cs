using System.Collections.Concurrent;

namespace MicroVMStudio.Core.Runtime;

/// <summary>
/// What is actually listening inside each VM.
///
/// Discovery is guest-declared, not port-scanned: the guest init reads
/// /etc/mvm/services.json (baked into the image) and sends it as a SERVICES
/// handshake line when it dials the log collector. An image therefore documents
/// its own surface — adding a service to an image needs no host-side change.
/// </summary>
public sealed class VmServiceRegistry
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<VmService>> _services = new();

    /// <summary>Ports every stock image exposes, shown before the handshake lands.</summary>
    public static readonly VmService Shell = new("shell", "Terminal", 7681);
    public static readonly VmService Files = new("files", "File Browser", 7682);

    public event Action<string>? ServicesChanged;

    public void Set(string vmId, IReadOnlyList<VmService> services)
    {
        _services[vmId] = services;
        ServicesChanged?.Invoke(vmId);
    }

    public void Remove(string vmId)
    {
        if (_services.TryRemove(vmId, out _)) ServicesChanged?.Invoke(vmId);
    }

    /// <summary>Guest-declared services only — what the SERVICES handshake reported.</summary>
    public IReadOnlyList<VmService> GetDeclared(string vmId) =>
        _services.TryGetValue(vmId, out var services) ? services : [];

    /// <summary>
    /// Everything the UI should offer for a VM: the stock shell and file browser,
    /// the VM's own primary port, and whatever the guest declared. Deduplicated
    /// by port, with guest-declared entries taking precedence so an image can
    /// relabel a stock port.
    /// </summary>
    public IReadOnlyList<VmService> GetAll(string vmId, int primaryPort)
    {
        var byPort = new Dictionary<int, VmService>
        {
            [Shell.Port] = Shell,
            [Files.Port] = Files,
        };
        byPort.TryAdd(primaryPort, new VmService("primary", "Workload", primaryPort));

        foreach (var service in GetDeclared(vmId)) byPort[service.Port] = service;

        return [.. byPort.Values.OrderBy(s => s.Port)];
    }
}

public sealed record VmService(string Name, string Label, int Port);
