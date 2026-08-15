using MicroVMStudio.Core.Data;
using MicroVMStudio.Core.Helper;
using MicroVMStudio.Core.Orchestration;
using MicroVMStudio.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MicroVMStudio.Core;

/// <summary>
/// Builds the background half of the app: Kestrel + the ingress proxy, the log
/// collector, the reconciler and the serialized SQLite writer.
///
/// Deliberately built and started explicitly rather than with the fire-and-forget
/// RunAsync pattern, so a failure to bind the proxy port surfaces as an exception
/// the UI can show instead of vanishing into a background task.
/// </summary>
public static class StudioHost
{
    public static WebApplication Build(Action<StudioOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddOptions<StudioOptions>().Configure(o => configure?.Invoke(o));
        var options = new StudioOptions();
        configure?.Invoke(options);

        builder.WebHost.ConfigureKestrel(k =>
        {
            // Loopback only. There is no authentication on this proxy — it is a
            // single-user dev tool — so it must never be reachable off-host.
            k.ListenLocalhost(options.ProxyPort);
        });

        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);

        builder.Services.AddDbContext<StudioDbContext>(o => o.UseSqlite(
            // Busy timeout belongs in the connection string: EF's CommandTimeout
            // does not map to sqlite3_busy_timeout (efcore#28135). Never
            // Cache=Shared — it is incompatible with WAL.
            $"Data Source={options.DatabasePath};"
            + "Cache=Default;Pooling=True;Foreign Keys=True;Default Timeout=5"));

        builder.Services.AddSingleton<SqliteWriter>();
        builder.Services.AddSingleton<ISqliteWriter>(sp => sp.GetRequiredService<SqliteWriter>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SqliteWriter>());

        builder.Services.AddSingleton<HelperClient>();
        builder.Services.AddSingleton<HelperSupervisor>();

        builder.Services.AddSingleton<ResourceAllocator>();
        builder.Services.AddSingleton<SecretsProtector>();
        builder.Services.AddSingleton<VmOrchestrator>();
        builder.Services.AddSingleton<HostAdoptionService>();
        builder.Services.AddSingleton<VmManager>();
        builder.Services.AddSingleton<ImageCatalog>();

        builder.Services.AddSingleton<VmRoutingTable>();
        builder.Services.AddSingleton<VmServiceRegistry>();
        builder.Services.AddSingleton(sp =>
            new VmLogHub(sp.GetRequiredService<IOptions<StudioOptions>>().Value.LogBufferLines));
        builder.Services.AddSingleton<PlatformIdentitySigner>();

        builder.Services.AddSingleton<VmReconciler>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<VmReconciler>());
        builder.Services.AddHostedService<VmLogCollector>();

        builder.Services.AddHttpForwarder();

        var app = builder.Build();
        app.MapVmIngress();
        return app;
    }

    /// <summary>
    /// Applies the schema. EF migrations are overkill for a single-user local
    /// database that ships with the app, and EnsureCreated keeps first-run
    /// working with no migration bundle to deploy.
    /// </summary>
    public static async Task InitializeDatabaseAsync(WebApplication app, CancellationToken ct = default)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        await db.Database.EnsureCreatedAsync(ct);

        // WAL is a persistent property of the file, so this only does work once —
        // but it must happen before any concurrent reader appears, or readers
        // block writers for the lifetime of the process.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", ct);
    }
}
