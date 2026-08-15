using System.Net.Sockets;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mvm.Helper.V1;

namespace MicroVMStudio.Core.Helper;

/// <summary>
/// gRPC client for the privileged helper, over its Unix domain socket.
///
/// The channel is created lazily and rebuilt on reconnect, because the helper's
/// lifetime is tied to the app's: if the user cancels the polkit prompt or the
/// helper dies, every call must fail fast with a clear signal rather than
/// hanging on a dead socket.
/// </summary>
public sealed class HelperClient(
    IOptions<StudioOptions> options,
    ILogger<HelperClient> logger) : IDisposable
{
    private readonly StudioOptions _options = options.Value;
    private readonly Lock _gate = new();

    private GrpcChannel? _channel;
    private VmHelper.VmHelperClient? _client;

    public bool IsConnected { get; private set; }

    /// <summary>Raised whenever connectivity changes, so the UI can show a banner.</summary>
    public event Action<bool>? ConnectionChanged;

    public VmHelper.VmHelperClient Client
    {
        get
        {
            lock (_gate)
            {
                return _client ?? throw new HelperUnavailableException(
                    "The privileged helper is not connected. Start it from the Host view.");
            }
        }
    }

    /// <summary>
    /// Connects to the helper socket and verifies it answers. Safe to call
    /// repeatedly; an already-working connection is left alone.
    /// </summary>
    public async Task<PingResponse> ConnectAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_options.HelperSocketPath))
        {
            SetConnected(false);
            throw new HelperUnavailableException(
                $"Helper socket not found at {_options.HelperSocketPath}.");
        }

        lock (_gate)
        {
            if (_client is null)
            {
                _channel = BuildChannel(_options.HelperSocketPath);
                _client = new VmHelper.VmHelperClient(_channel);
            }
        }

        try
        {
            var pong = await Client.PingAsync(new Empty(),
                deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            SetConnected(true);
            logger.LogInformation(
                "Helper connected: version={Version} firecracker={Fc} jailer={Jailer} kvm={Kvm} cgroup2={Cg} egress={Egress}",
                pong.Version, pong.FirecrackerPresent, pong.JailerPresent,
                pong.KvmAccessible, pong.CgroupV2, pong.EgressInterface);
            return pong;
        }
        catch (Exception ex)
        {
            Reset();
            SetConnected(false);
            throw new HelperUnavailableException($"Helper did not answer: {ex.Message}", ex);
        }
    }

    /// <summary>Drops the channel so the next ConnectAsync builds a fresh one.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _channel?.Dispose();
            _channel = null;
            _client = null;
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    private static GrpcChannel BuildChannel(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        // The authority is required by the Uri parser but never resolved — every
        // connection goes through ConnectCallback to the socket above.
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    public void Dispose() => Reset();
}

public sealed class HelperUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
