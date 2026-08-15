using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MicroVMStudio.Core.Helper;

/// <summary>
/// Owns the lifetime of the privileged helper process.
///
/// Three hard constraints from polkit's implementation shape this class, and
/// violating any of them breaks the helper in ways that look like random
/// failures much later:
///
///  1. pkexec authorizes <c>getppid()</c> and refuses with "Refusing to render
///     service to dead parents" if it has been reparented to init. So we never
///     double-fork, never setsid, and the app must outlive the helper.
///
///  2. pkexec sets <c>PR_SET_PDEATHSIG</c>, and the kernel's notion of "parent"
///     there is the *creating thread*, not the process. .NET retires idle
///     threadpool threads, so launching from a threadpool thread would have the
///     helper SIGTERM'd minutes later for no visible reason. Hence the
///     dedicated non-background thread below.
///
///  3. polkit requires the exec target to be an absolute, root-owned path, so
///     the binary must be installed by packaging/install.sh — it cannot be run
///     out of the build directory.
/// </summary>
public sealed class HelperSupervisor(
    HelperClient client,
    IOptions<StudioOptions> options,
    ILogger<HelperSupervisor> logger) : IDisposable
{
    private readonly StudioOptions _options = options.Value;
    private readonly Lock _gate = new();

    private Thread? _thread;
    private Process? _process;
    private volatile bool _disposed;

    /// <summary>Raised for each line the helper writes to stdout/stderr.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised when the helper exits, with its exit code.</summary>
    public event Action<int>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    /// <summary>
    /// Launches the helper via pkexec (prompting the user for a password) and
    /// waits for it to answer on its socket. If a helper is already listening —
    /// started by a previous session or manually with sudo — this connects to it
    /// instead of starting a second one.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (await TryConnectExistingAsync(ct)) return true;

        if (!File.Exists(_options.HelperBinaryPath))
        {
            throw new HelperUnavailableException(
                $"Helper binary not found at {_options.HelperBinaryPath}. "
                + "Run: sudo ./packaging/install.sh");
        }

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool alreadySupervising;
        lock (_gate)
        {
            alreadySupervising = _thread is { IsAlive: true };
            if (!alreadySupervising)
            {
                // IsBackground=false: this thread must outlive any threadpool
                // churn, because PR_SET_PDEATHSIG is bound to it (see remarks).
                _thread = new Thread(() => RunHelperThread(started))
                {
                    IsBackground = false,
                    Name = "helper-supervisor",
                };
                _thread.Start();
            }
        }

        if (alreadySupervising) return await TryConnectExistingAsync(ct);

        // pkexec blocks on the password dialog, so allow a generous window.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            await started.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HelperUnavailableException("Timed out waiting for the helper to start.");
        }

        return await WaitForSocketAsync(ct);
    }

    private void RunHelperThread(TaskCompletionSource<bool> started)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/pkexec",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(_options.HelperBinaryPath);
            psi.Environment["MVM_HELPER_SOCKET"] = _options.HelperSocketPath;

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Emit(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Emit(e.Data); };
            proc.Exited += (_, _) =>
            {
                var code = proc.ExitCode;
                logger.LogWarning("Helper exited with code {Code}", code);
                client.Reset();
                Exited?.Invoke(code);
            };

            if (!proc.Start())
            {
                started.TrySetException(new HelperUnavailableException("Failed to start pkexec."));
                return;
            }

            lock (_gate) _process = proc;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            started.TrySetResult(true);

            // Block this thread for the helper's entire lifetime. This is the
            // whole point of the dedicated thread: the creating thread must stay
            // alive or PR_SET_PDEATHSIG fires.
            proc.WaitForExit();

            if (proc.ExitCode == 127)
            {
                // pkexec's "no polkit agent / not authorized" exit code — the
                // common case on headless SSH sessions and bare tiling WMs.
                logger.LogError(
                    "pkexec could not authenticate (exit 127). No polkit agent appears to be running. "
                    + "Start the helper manually with: sudo {Path}", _options.HelperBinaryPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Helper supervisor thread failed");
            started.TrySetException(ex);
        }
        finally
        {
            lock (_gate) _process = null;
        }
    }

    private void Emit(string line)
    {
        logger.LogDebug("[helper] {Line}", line);
        OutputReceived?.Invoke(line);
    }

    private async Task<bool> TryConnectExistingAsync(CancellationToken ct)
    {
        if (!File.Exists(_options.HelperSocketPath)) return false;
        try
        {
            await client.ConnectAsync(ct);
            logger.LogInformation("Connected to an already-running helper at {Path}",
                _options.HelperSocketPath);
            return true;
        }
        catch (HelperUnavailableException)
        {
            // Stale socket file with nothing behind it — fall through to launch.
            return false;
        }
    }

    private async Task<bool> WaitForSocketAsync(CancellationToken ct)
    {
        // The helper has to bind and then chown the socket before we can use it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning && _process is not null) return false;

            if (File.Exists(_options.HelperSocketPath))
            {
                try
                {
                    await client.ConnectAsync(ct);
                    return true;
                }
                catch (HelperUnavailableException)
                {
                    // Bound but not yet chowned, or still starting.
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }

        throw new HelperUnavailableException(
            $"Helper did not begin listening on {_options.HelperSocketPath} within 10s.");
    }

    /// <summary>
    /// Stops the helper. Running VMs are unaffected: jailer is reparented to
    /// init and keeps running, and the app adopts them on next launch.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false }) return;
            try
            {
                _process.Kill(entireProcessTree: false);
                _process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop the helper cleanly");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
