using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Porta.Pty;
using Base = Iciclecreek.Terminal.TerminalView;

namespace MicroVMStudio.App.Controls;

/// <summary>
/// A VT terminal wired to a guest's ttyd over the local ingress proxy.
///
/// Subclasses the Iciclecreek control rather than reimplementing it: keyboard
/// translation, mouse reporting, selection, scrollback and rendering all already
/// route through <see cref="IPtyConnection"/>, and <c>ArrangeOverride</c> already
/// forwards geometry changes to it. Substituting a <see cref="TtydPtyConnection"/>
/// for the local pty is therefore the whole integration.
///
/// The substitution needs three private members the library does not expose. They
/// are resolved once at type load and throw immediately if absent, so a library
/// upgrade that moves them fails loudly instead of silently producing a dead pane.
/// </summary>
public class TerminalView : Base
{
    private static readonly FieldInfo PtyField = Member<FieldInfo>("_ptyConnection");
    private static readonly FieldInfo CtsField = Member<FieldInfo>("_processCts");
    private static readonly FieldInfo ExitHandledField = Member<FieldInfo>("_processExitHandled");
    private static readonly MethodInfo ReadLoop = Member<MethodInfo>("ReadPtyOutputAsync");

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<TerminalView, string?>(nameof(Url));

    private TtydPtyConnection? _connection;
    private CancellationTokenSource? _cts;
    private string? _connectedUrl;

    /// <summary>ttyd WebSocket endpoint, e.g. <c>http://127.0.0.1:7000/vms/dev1/p/7681/ws</c>.</summary>
    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public TerminalView()
    {
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty && IsLoaded) _ = ReconnectAsync();
    }

    /// <summary>
    /// The base class's process-launch entry point, repurposed: this control has no
    /// local process to launch, so any launch request becomes a ttyd (re)connect.
    /// </summary>
    public override Task LaunchProcess(string? startingDirectory, string process, params string[] args) =>
        ReconnectAsync();

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        if (_connection is null || _connectedUrl != Url) _ = ReconnectAsync();
    }

    private void OnViewUnloaded(object? sender, RoutedEventArgs e) => Disconnect();

    public async Task ReconnectAsync()
    {
        Disconnect();

        var url = Url;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(ToWebSocketScheme(url), UriKind.Absolute, out var uri))
        {
            Terminal.WriteLine($"\r\nInvalid shell URL: {url}\r\n");
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        CtsField.SetValue(this, cts);
        ExitHandledField.SetValue(this, 0);

        try
        {
            var connection = await TtydPtyConnection
                .ConnectAsync(uri, Terminal.Cols, Terminal.Rows, cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested)
            {
                connection.Dispose();
                return;
            }

            _connection = connection;
            _connectedUrl = url;
            PtyField.SetValue(this, connection);

            // Reuse the base class's pump: it owns the terminal lock, the scrollback
            // bookkeeping and the exit notice, none of which are worth duplicating.
            _ = Task.Run(() => (Task)ReadLoop.Invoke(this, [cts.Token])!, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Terminal.WriteLine(
                $"\r\nCould not reach the VM shell at {uri}: {ex.Message}\r\n" +
                "The VM must be Running and its ttyd service listening on port 7681.\r\n"));
        }
    }

    private void Disconnect()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _connection?.Dispose();
        _connection = null;
        _connectedUrl = null;
        PtyField.SetValue(this, null);
        CtsField.SetValue(this, null);
    }

    private static string ToWebSocketScheme(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? string.Concat("ws://", url.AsSpan(7))
        : url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? string.Concat("wss://", url.AsSpan(8))
        : url;

    private static T Member<T>(string name) where T : MemberInfo
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        MemberInfo? member = typeof(T) == typeof(FieldInfo)
            ? typeof(Base).GetField(name, flags)
            : typeof(Base).GetMethod(name, flags);

        return member as T ?? throw new MissingMemberException(
            $"Iciclecreek.Terminal.TerminalView.{name} is gone — the ttyd transport " +
            "substitution in MicroVMStudio.App.Controls.TerminalView needs reworking " +
            "against the current version of Iciclecreek.Avalonia.Terminal.");
    }
}
