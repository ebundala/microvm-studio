using System.Net.Sockets;
using Google.Protobuf;
using Mvm.Helper.V1;

namespace MicroVMStudio.Helper.Services;

/// <summary>
/// Proxies a single Firecracker API call into a per-VM chroot. The app never
/// touches the chroot filesystem itself; it sends a FirecrackerApiCallRequest
/// and the helper opens the per-VM socket at
/// &lt;chroot_base&gt;/firecracker/&lt;vm_id&gt;/root/run/firecracker.socket.
///
/// This consolidates all Firecracker API access through one audited privileged
/// path.
/// </summary>
public sealed class FirecrackerApiProxy : IDisposable
{
    private readonly ILogger<FirecrackerApiProxy> _logger;
    private readonly string _chrootBaseDir;
    private readonly string _diagnosticsDir;

    // One HttpClient per socket path. Firecracker keeps connections alive after
    // replying — even when sent `Connection: close` — so reading to EOF would
    // stall until timeout on every call. Letting HttpClient handle HTTP/1.1
    // framing (Content-Length, 204/304 no-body rules) avoids that entirely.
    private readonly Dictionary<string, HttpClient> _clients = [];
    private readonly Lock _clientsLock = new();

    public FirecrackerApiProxy(ILogger<FirecrackerApiProxy> logger, IConfiguration config)
    {
        _logger = logger;
        var baseDir = config.GetValue<string>("Studio:BaseDir") ?? "/var/lib/microvm-studio";
        _chrootBaseDir = config.GetValue<string>("Studio:ChrootBaseDir")
            ?? Path.Combine(baseDir, "jails");
        _diagnosticsDir = Path.Combine(baseDir, "diagnostics");
    }

    public async Task<FirecrackerApiCallResponse> CallAsync(
        FirecrackerApiCallRequest req, CancellationToken ct)
    {
        var socketPath = Path.Combine(
            _chrootBaseDir, "firecracker", req.VmId, "root", "run", "firecracker.socket");

        var timeout = req.TimeoutMs > 0
            ? TimeSpan.FromMilliseconds(req.TimeoutMs)
            : TimeSpan.FromSeconds(5);

        // Wait for the socket to appear. After LaunchJailer returns, jailer still
        // has to set up the chroot, drop privileges and exec firecracker before
        // FC binds its socket — usually 200-800ms, slower under load. Waiting
        // here rather than in the caller keeps the wait next to the readiness
        // signal instead of guessing with a fixed sleep.
        var deadline = DateTime.UtcNow.Add(timeout);
        while (!File.Exists(socketPath) && DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(100), ct); }
            catch (OperationCanceledException) { break; }
        }

        if (!File.Exists(socketPath))
        {
            return Unreachable(
                $"Firecracker API socket did not appear at {socketPath} within {timeout.TotalSeconds:F1}s. "
                + "Likely causes: jailer crashed before exec'ing firecracker (check "
                + $"{Path.Combine(_diagnosticsDir, req.VmId + "-jailer.log")}), the kernel/rootfs files "
                + "were not linked into the chroot, or KVM/cgroup limits blocked the launch.");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var client = GetClient(socketPath);
            using var message = new HttpRequestMessage(
                new HttpMethod(req.Method.ToUpperInvariant()),
                new Uri("http://localhost" + req.Path));

            var bodyBytes = req.Body.ToByteArray();
            if (bodyBytes.Length > 0)
            {
                message.Content = new ByteArrayContent(bodyBytes);
                message.Content.Headers.ContentType = new("application/json");
            }

            using var response = await client.SendAsync(message, cts.Token);
            var responseBody = await response.Content.ReadAsByteArrayAsync(cts.Token);

            _logger.LogDebug("FC API: vm={Vm} {Method} {Path} → {Status} ({Len}B) trace={Trace}",
                req.VmId, req.Method, req.Path, (int)response.StatusCode, responseBody.Length, req.TraceId);

            return new FirecrackerApiCallResponse
            {
                Status = (int)response.StatusCode,
                Body = ByteString.CopyFrom(responseBody),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FirecrackerApiCallResponse
            {
                Status = 0,
                ErrorCode = "timeout",
                Body = ByteString.CopyFromUtf8($"Firecracker API call exceeded {timeout.TotalMilliseconds}ms"),
            };
        }
        catch (HttpRequestException ex)
        {
            return Unreachable($"socket: {ex.Message}");
        }
        catch (SocketException ex)
        {
            return Unreachable($"socket: {ex.Message}");
        }
    }

    /// <summary>Drops the cached client for a VM, so a relaunch gets a fresh connection pool.</summary>
    public void Forget(string vmId)
    {
        var socketPath = Path.Combine(
            _chrootBaseDir, "firecracker", vmId, "root", "run", "firecracker.socket");
        lock (_clientsLock)
        {
            if (_clients.Remove(socketPath, out var client)) client.Dispose();
        }
    }

    private HttpClient GetClient(string socketPath)
    {
        lock (_clientsLock)
        {
            if (_clients.TryGetValue(socketPath, out var existing)) return existing;

            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        // Even once the socket file exists, FC's listen() may still
                        // be racing its bind(); ECONNREFUSED is what that window
                        // looks like, so retry briefly rather than failing the boot.
                        var connectDeadline = DateTime.UtcNow.AddSeconds(2);
                        while (true)
                        {
                            try
                            {
                                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token);
                                break;
                            }
                            catch (SocketException) when (DateTime.UtcNow < connectDeadline)
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
                            }
                        }
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };

            // Per-request timeouts are enforced by the caller's CTS.
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _clients[socketPath] = client;
            return client;
        }
    }

    private static FirecrackerApiCallResponse Unreachable(string detail) => new()
    {
        Status = 0,
        ErrorCode = "fc_unreachable",
        Body = ByteString.CopyFromUtf8(detail),
    };

    public void Dispose()
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }
}
