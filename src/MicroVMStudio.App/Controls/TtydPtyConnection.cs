using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Porta.Pty;

namespace MicroVMStudio.App.Controls;

/// <summary>
/// ttyd's WebSocket protocol dressed up as an <see cref="IPtyConnection"/>, so the
/// Avalonia terminal control's existing key/mouse/VT plumbing can drive a remote
/// shell instead of a local pty.
///
/// Framing verified against ttyd 1.7.7 (html/src/components/terminal/xterm/index.ts):
///   client → server: bare JSON handshake once, then '0'+input | '1'+{columns,rows}
///   server → client: '0'+output | '1'+title | '2'+preferences
/// AuthToken is empty because ttyd only validates it under --credential, which the
/// guest image does not use; the proxy in front of it is loopback-only.
/// </summary>
public sealed class TtydPtyConnection : IPtyConnection
{
    private const byte ClientInput = (byte)'0';
    private const byte ClientResize = (byte)'1';
    private const byte ServerOutput = (byte)'0';

    private readonly ClientWebSocket _socket = new();
    private readonly Pipe _incoming = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _exitHandled;

    private TtydPtyConnection()
    {
        ReaderStream = _incoming.Reader.AsStream();
        WriterStream = new InputStream(this);
    }

    public Stream ReaderStream { get; }
    public Stream WriterStream { get; }

    /// <summary>Not a real process; the control only uses this for display.</summary>
    public int Pid => 0;

    public int ExitCode { get; private set; }

    /// <summary>
    /// Never raised: <c>PtyExitedEventArgs</c>' constructor is internal to Porta.Pty,
    /// so there is no way to construct the payload. Termination is signalled instead
    /// by completing the reader stream, which the control already reports on.
    /// </summary>
    public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }

    public static async Task<TtydPtyConnection> ConnectAsync(
        Uri url, int cols, int rows, CancellationToken ct)
    {
        var connection = new TtydPtyConnection();
        try
        {
            connection._socket.Options.AddSubProtocol("tty");
            await connection._socket.ConnectAsync(url, ct).ConfigureAwait(false);

            var handshake = JsonSerializer.SerializeToUtf8Bytes(
                new { AuthToken = "", columns = cols, rows = rows });
            await connection._socket
                .SendAsync(handshake, WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _ = connection.ReceiveLoopAsync();
        return connection;
    }

    public void Resize(int cols, int rows)
    {
        var frame = Encoding.UTF8.GetBytes(
            $"1{{\"columns\":{cols},\"rows\":{rows}}}");
        _ = SendFrameAsync(frame, CancellationToken.None);
    }

    public bool WaitForExit(int milliseconds) => _cts.IsCancellationRequested;

    public void Kill() => Complete(0);

    public void Dispose()
    {
        // Send the close frame before cancelling: cancelling the pending receive
        // aborts the socket, and an aborted close makes ttyd log an EOF error and
        // leave the guest-side shell for the OS to reap.
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .Wait(TimeSpan.FromMilliseconds(250));
            }
            catch (Exception ex) when (ex is AggregateException or WebSocketException
                                       or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        Complete(0);
        _socket.Dispose();
        _cts.Dispose();
        // _sendLock is intentionally not disposed: an in-flight send would then fault
        // on release. Nothing waits on its handle, so there is nothing to reclaim.
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket
                        .ReceiveAsync(buffer.AsMemory(), _cts.Token)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                await DispatchAsync(message).ConfigureAwait(false);
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            Complete(1);
        }
    }

    private async Task DispatchAsync(MemoryStream message)
    {
        if (message.Length == 0) return;
        var frame = message.GetBuffer().AsMemory(0, (int)message.Length);

        // Title ('1') and preferences ('2') are dropped: this terminal is embedded in
        // a tab, not a dedicated window, and we do not honour server-pushed prefs.
        if (frame.Span[0] != ServerOutput) return;

        await _incoming.Writer.WriteAsync(frame[1..], _cts.Token).ConfigureAwait(false);
    }

    private async Task SendInputAsync(ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        var frame = new byte[input.Length + 1];
        frame[0] = ClientInput;
        input.CopyTo(frame.AsMemory(1));
        await SendFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(byte[] frame, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open) return;
            await _socket
                .SendAsync(frame, WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException
                                   or OperationCanceledException)
        {
            Complete(1);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void Complete(int exitCode)
    {
        if (Interlocked.Exchange(ref _exitHandled, 1) != 0) return;
        ExitCode = exitCode;
        _cts.Cancel();
        _incoming.Writer.Complete();
    }

    /// <summary>Every write becomes one framed ttyd input message.</summary>
    private sealed class InputStream(TtydPtyConnection owner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override void Write(byte[] buffer, int offset, int count) =>
            owner.SendInputAsync(new ReadOnlyMemory<byte>(buffer, offset, count),
                CancellationToken.None).GetAwaiter().GetResult();

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            owner.SendInputAsync(new ReadOnlyMemory<byte>(buffer, offset, count),
                cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(owner.SendInputAsync(buffer, cancellationToken));

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
