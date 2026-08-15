using System.Diagnostics;
using System.Text;

namespace MicroVMStudio.Helper.Services;

/// <summary>
/// Invokes external commands. Every privileged operation in this daemon shells
/// out to the host's `ip`, `iptables`, `truncate` and friends — that is what
/// makes the helper the privileged subprocess.
///
/// Security property: arguments are passed as string[], never as one shell
/// string, so the OS never invokes /bin/sh. Shell injection is impossible by
/// construction regardless of what reaches these arguments.
/// </summary>
public sealed class Shell(ILogger<Shell> logger)
{
    public async Task<ShellResult> RunAsync(
        string fileName,
        string[] args,
        TimeSpan? timeout = null,
        bool throwOnNonZero = false,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var deadline = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"{fileName} {string.Join(' ', args)} exceeded {deadline.TotalSeconds}s");
        }

        var result = new ShellResult(proc.ExitCode, stdout.ToString(), stderr.ToString());

        logger.LogDebug("Shell {File} {Args} exit={Exit}", fileName, string.Join(' ', args), proc.ExitCode);

        if (throwOnNonZero && proc.ExitCode != 0)
        {
            throw new ShellNonZeroException(fileName, args, result);
        }
        return result;
    }

    /// <summary>True if the binary exists and is executable.</summary>
    public static bool BinaryExists(string path) => File.Exists(path);
}

public sealed record ShellResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class ShellNonZeroException(string fileName, string[] args, ShellResult result)
    : Exception($"{fileName} {string.Join(' ', args)} exited with code {result.ExitCode}; stderr: {result.Stderr.Trim()}")
{
    public string FileName { get; } = fileName;
    public string[] Args { get; } = args;
    public ShellResult Result { get; } = result;
}
