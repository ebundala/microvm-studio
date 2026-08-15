using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroVMStudio.Core;
using MicroVMStudio.Core.Helper;
using MicroVMStudio.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MicroVMStudio.App.ViewModels;

/// <summary>
/// Host preflight and cleanup. The preflight fields come straight off the
/// helper's Ping, so this page answers "why won't anything boot?" — a missing
/// firecracker binary or an inaccessible /dev/kvm otherwise surfaces as an
/// opaque boot failure minutes later.
/// </summary>
public partial class HostViewModel : ViewModelBase
{
    private readonly HelperClient _helper;
    private readonly HelperSupervisor _supervisor;
    private readonly HostAdoptionService _adoption;
    private readonly StudioOptions _options;
    private readonly Func<Task> _reconnect;

    private OrphanReport? _orphans;

    [ObservableProperty] public partial bool HelperConnected { get; set; }
    [ObservableProperty] public partial string HelperVersion { get; set; } = "—";
    [ObservableProperty] public partial bool FirecrackerPresent { get; set; }
    [ObservableProperty] public partial bool JailerPresent { get; set; }
    [ObservableProperty] public partial bool KvmAccessible { get; set; }
    [ObservableProperty] public partial bool CgroupV2 { get; set; }
    [ObservableProperty] public partial string EgressInterface { get; set; } = "—";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? AdoptionSummary { get; set; }
    [ObservableProperty] public partial bool Reclaiming { get; set; }

    public ObservableCollection<string> HelperOutput { get; } = [];
    public ObservableCollection<string> OrphanVeths { get; } = [];
    public ObservableCollection<OrphanChroot> OrphanChroots { get; } = [];

    public string BridgeName => _options.BridgeName;
    public string BridgeCidr => _options.BridgeCidr;
    public string ProxyUrl => $"http://127.0.0.1:{_options.ProxyPort}";
    public string HelperSocketPath => _options.HelperSocketPath;
    public string BaseDir => _options.BaseDir;
    public string DatabasePath => _options.DatabasePath;

    public bool HasOrphans => OrphanVeths.Count > 0 || OrphanChroots.Count > 0;

    /// <summary>The exact command to paste when pkexec has no polkit agent to talk to.</summary>
    public string ManualStartCommand => $"sudo {_options.HelperBinaryPath}";

    public HostViewModel(IServiceProvider services, Func<Task> reconnect)
    {
        _helper = services.GetRequiredService<HelperClient>();
        _supervisor = services.GetRequiredService<HelperSupervisor>();
        _adoption = services.GetRequiredService<HostAdoptionService>();
        _options = services.GetRequiredService<IOptions<StudioOptions>>().Value;
        _reconnect = reconnect;

        _supervisor.OutputReceived += line => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HelperOutput.Add(line);
            while (HelperOutput.Count > 500) HelperOutput.RemoveAt(0);
        });
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var pong = await _helper.ConnectAsync();
            HelperConnected = true;
            HelperVersion = pong.Version;
            FirecrackerPresent = pong.FirecrackerPresent;
            JailerPresent = pong.JailerPresent;
            KvmAccessible = pong.KvmAccessible;
            CgroupV2 = pong.CgroupV2;
            EgressInterface = string.IsNullOrEmpty(pong.EgressInterface) ? "—" : pong.EgressInterface;
            Error = null;
        }
        catch (Exception ex)
        {
            HelperConnected = false;
            Error = ex.Message;
        }
    }

    /// <summary>
    /// Reconciles the database against the host. Run once on connect: VMs keep
    /// running when the app is closed, so a relaunch has to find out which of
    /// them are genuinely still alive.
    /// </summary>
    public async Task RunAdoptionAsync()
    {
        await RefreshAsync();
        if (!HelperConnected) return;

        try
        {
            var report = await _adoption.RunAsync();
            SetOrphans(new OrphanReport(report.OrphanVeths, report.OrphanChroots));

            AdoptionSummary = report switch
            {
                { Adopted.Count: 0, Lost.Count: 0 } => null,
                { Lost.Count: 0 } => $"Adopted {report.Adopted.Count} running VM(s) from a previous session.",
                { Adopted.Count: 0 } => $"{report.Lost.Count} VM(s) died while the app was closed.",
                _ => $"Adopted {report.Adopted.Count} VM(s); {report.Lost.Count} had died while closed.",
            };
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ScanOrphansAsync()
    {
        try
        {
            SetOrphans(await _adoption.FindOrphansAsync());
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ReclaimOrphansAsync()
    {
        if (_orphans is null || _orphans.IsEmpty) return;

        Reclaiming = true;
        try
        {
            await _adoption.ReclaimAsync(_orphans);
            SetOrphans(await _adoption.FindOrphansAsync());
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Reclaiming = false;
        }
    }

    [RelayCommand]
    private Task ReconnectAsync() => _reconnect();

    private void SetOrphans(OrphanReport report)
    {
        _orphans = report;

        OrphanVeths.Clear();
        foreach (var veth in report.Veths) OrphanVeths.Add(veth);

        OrphanChroots.Clear();
        foreach (var chroot in report.Chroots) OrphanChroots.Add(chroot);

        OnPropertyChanged(nameof(HasOrphans));
    }
}
