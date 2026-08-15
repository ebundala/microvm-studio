using System.Runtime.InteropServices;
using MicroVMStudio.Helper.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateSlimBuilder(args);

// Fixed by the polkit policy's exec target, but overridable for development
// runs outside the installed location.
var socketPath = Environment.GetEnvironmentVariable("MVM_HELPER_SOCKET")
                 ?? "/run/microvm-studio-helper.sock";

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Bind ONLY to the Unix socket. No TCP endpoint of any kind: this process runs
// as root and must not be reachable over the network, even on loopback.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc(o => o.EnableDetailedErrors = true);
builder.Services.AddSingleton<Shell>();
builder.Services.AddSingleton<NetnsOperations>();
builder.Services.AddSingleton<IptablesOperations>();
builder.Services.AddSingleton<DiskOperations>();
builder.Services.AddSingleton<JailerOperations>();
builder.Services.AddSingleton<FirecrackerApiProxy>();
builder.Services.AddSingleton<ImageOperations>();
builder.Services.AddHostedService<HostNetworkingInitializer>();

var app = builder.Build();
app.MapGrpcService<VmHelperService>();

// A stale socket from an unclean shutdown makes bind() fail with EADDRINUSE.
if (File.Exists(socketPath)) File.Delete(socketPath);

var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helper");

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Kestrel binds the socket as root with a restrictive mode. Hand group
    // ownership to the invoking desktop user so the unprivileged app can
    // connect while everyone else still cannot. PKEXEC_UID is set by pkexec;
    // SUDO_UID covers the documented sudo fallback.
    var callerUid = Environment.GetEnvironmentVariable("PKEXEC_UID")
                    ?? Environment.GetEnvironmentVariable("SUDO_UID");

    if (int.TryParse(callerUid, out var uid))
    {
        var gid = GetPrimaryGid(uid);
        if (gid >= 0 && Chown(socketPath, 0, gid) == 0)
        {
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            log.LogInformation("Socket {Path} handed to uid={Uid} gid={Gid} mode=0660",
                socketPath, uid, gid);
        }
        else
        {
            log.LogError("Failed to chown {Path} to the primary group of uid {Uid}; "
                + "the app will not be able to connect.", socketPath, uid);
        }
    }
    else
    {
        // Running as root with no caller identity (e.g. a bare `sudo -i` shell).
        // Leave it root-only rather than guessing, and say so.
        log.LogWarning(
            "Neither PKEXEC_UID nor SUDO_UID is set; leaving {Path} root-owned. "
            + "Launch via pkexec or sudo so the desktop user can connect.", socketPath);
    }

    log.LogInformation("MicroVM Studio helper listening on {Path}", socketPath);
});

app.Lifetime.ApplicationStopped.Register(() =>
{
    try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { /* shutting down */ }
});

app.Run();
return;

static int GetPrimaryGid(int uid)
{
    // No managed API exposes this, and pulling in Mono.Posix for one lookup
    // isn't worth it.
    foreach (var line in File.ReadLines("/etc/passwd"))
    {
        var f = line.Split(':');
        if (f.Length > 3 && int.TryParse(f[2], out var u) && u == uid
            && int.TryParse(f[3], out var g))
        {
            return g;
        }
    }
    return -1;
}

[DllImport("libc", SetLastError = true, EntryPoint = "chown")]
static extern int Chown(string path, int owner, int group);
