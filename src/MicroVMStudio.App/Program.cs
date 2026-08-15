using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using MicroVMStudio.Core;
using MicroVMStudio.Core.Helper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MicroVMStudio.App;

sealed class Program
{
    /// <summary>
    /// The background host: Kestrel + ingress proxy, log collector, reconciler,
    /// SQLite writer. Exposed statically because Avalonia constructs views
    /// itself and has no DI container of its own to resolve them through.
    /// </summary>
    public static WebApplication Host { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Host = StudioHost.Build();

            // Started synchronously *before* Avalonia so a bind failure on the
            // proxy port is a startup error the user sees, rather than an
            // exception swallowed by a fire-and-forget background task.
            StudioHost.InitializeDatabaseAsync(Host).GetAwaiter().GetResult();
            Host.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MicroVM Studio failed to start: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Stopping the host does not stop the VMs: jailer is reparented to
            // init and keeps running, and the next launch adopts it.
            try
            {
                // The supervisor's helper thread is deliberately IsBackground=false
                // (PR_SET_PDEATHSIG binds to the creating thread), so it blocks
                // process exit until the helper is asked to stop. Without this the
                // window closes but the process lives on, still holding the proxy
                // port — which then fails the next launch with "address in use".
                Host.Services.GetRequiredService<HelperSupervisor>().Stop();
                Host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Shutdown warning: {ex.Message}");
            }
        }

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
