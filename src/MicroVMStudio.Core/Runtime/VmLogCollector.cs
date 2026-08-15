using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MicroVMStudio.Core.Data;
using MicroVMStudio.Core.Orchestration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MicroVMStudio.Core.Runtime;

/// <summary>
/// Receives console output and service declarations from running VMs.
///
/// The guest init dials this on the bridge gateway address and speaks a
/// deliberately trivial line protocol:
///
///     AGENT &lt;vsock_cid&gt;\n          identifies which VM is calling
///     SERVICES &lt;json array&gt;\n      what is listening inside it
///     &lt;timestamp&gt;\t&lt;line&gt;\n        console output, repeating
///
/// It is one-way: we never write back. The guest treats a read returning as its
/// signal to redial, so a host restart repopulates without rebooting the VM.
/// </summary>
public sealed class VmLogCollector(
    VmLogHub hub,
    VmServiceRegistry registry,
    ResourceAllocator allocator,
    IServiceScopeFactory scopes,
    IOptions<StudioOptions> options,
    ILogger<VmLogCollector> logger) : BackgroundService
{
    private readonly StudioOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Bound to the bridge gateway only: this port must be reachable from
        // guests but is not something to expose on the host's real interfaces.
        var bindAddress = IPAddress.Parse(allocator.GatewayIp);
        var listener = new TcpListener(bindAddress, _options.LogCollectorPort);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            // The bridge does not exist until the first VM boots, so this is
            // expected on a cold start; retry until it appears.
            logger.LogInformation(
                "Log collector cannot bind {Address}:{Port} yet ({Reason}); will retry once the bridge exists.",
                bindAddress, _options.LogCollectorPort, ex.SocketErrorCode);

            if (!await RetryBindAsync(listener, ct)) return;
        }

        logger.LogInformation("Log collector listening on {Address}:{Port}",
            bindAddress, _options.LogCollectorPort);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<bool> RetryBindAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            try
            {
                listener.Start();
                return true;
            }
            catch (SocketException) { /* bridge still absent */ }
        }
        return false;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        string? vmId = null;

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);

            var handshake = await reader.ReadLineAsync(ct);
            if (handshake is null || !handshake.StartsWith("AGENT ", StringComparison.Ordinal))
            {
                logger.LogWarning("Log client sent an unexpected handshake: {Line}", handshake);
                return;
            }

            if (!int.TryParse(handshake[6..].Trim(), out var vsockCid))
            {
                logger.LogWarning("Log client sent an unparseable vsock cid: {Line}", handshake);
                return;
            }

            vmId = await ResolveVmIdAsync(vsockCid, ct);
            if (vmId is null)
            {
                logger.LogWarning("Log client claimed vsock cid {Cid}, which matches no known VM.", vsockCid);
                return;
            }

            logger.LogInformation("VM {VmId} connected to the log collector (cid={Cid})", vmId, vsockCid);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                if (line.StartsWith("SERVICES ", StringComparison.Ordinal))
                {
                    TryParseServices(vmId, line[9..]);
                    continue;
                }

                // Console lines arrive as "<timestamp>\t<text>"; keep only the text,
                // since the UI stamps its own arrival time.
                var tab = line.IndexOf('\t');
                hub.Append(vmId, tab >= 0 ? line[(tab + 1)..] : line);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // VM stopped, or we are shutting down. Not an error.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Log collector client failed (vm={VmId})", vmId ?? "unknown");
        }
        finally
        {
            if (vmId is not null)
            {
                logger.LogDebug("VM {VmId} disconnected from the log collector", vmId);
            }
        }
    }

    private async Task<string?> ResolveVmIdAsync(int vsockCid, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        return await db.Vms
            .Where(v => v.VsockCid == vsockCid)
            .Select(v => v.Id)
            .FirstOrDefaultAsync(ct);
    }

    private void TryParseServices(string vmId, string json)
    {
        try
        {
            var declared = JsonSerializer.Deserialize<List<DeclaredService>>(json);
            if (declared is null) return;

            var services = declared
                .Where(s => !string.IsNullOrWhiteSpace(s.Name) && s.Port is > 0 and < 65536)
                .Select(s => new VmService(s.Name!, string.IsNullOrWhiteSpace(s.Label) ? s.Name! : s.Label!, s.Port))
                .ToList();

            registry.Set(vmId, services);
            logger.LogInformation("VM {VmId} declared {Count} service(s): {Ports}",
                vmId, services.Count, string.Join(", ", services.Select(s => s.Port)));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "VM {VmId} sent a malformed SERVICES manifest", vmId);
        }
    }

    private sealed class DeclaredService
    {
        public string? Name { get; set; }
        public string? Label { get; set; }
        public int Port { get; set; }
    }
}
