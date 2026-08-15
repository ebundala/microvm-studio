using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroVMStudio.Core.Helper;
using Microsoft.Extensions.DependencyInjection;

namespace MicroVMStudio.App.ViewModels;

/// <summary>
/// The window shell: left nav, plus the helper-connection banner every other
/// view depends on. Nothing works until the helper is connected, so establishing
/// that is the first thing this does.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly HelperSupervisor _supervisor;
    private readonly HelperClient _helper;

    [ObservableProperty] public partial ViewModelBase Current { get; set; }
    [ObservableProperty] public partial string SelectedSection { get; set; } = "VMs";
    [ObservableProperty] public partial bool HelperConnected { get; set; }
    [ObservableProperty] public partial string? HelperMessage { get; set; }
    [ObservableProperty] public partial bool Connecting { get; set; }

    public VmListViewModel Vms { get; }
    public ImagesViewModel Images { get; }
    public DisksViewModel Disks { get; }
    public HostViewModel HostPage { get; }

    public MainViewModel()
    {
        var services = Program.Host.Services;
        _supervisor = services.GetRequiredService<HelperSupervisor>();
        _helper = services.GetRequiredService<HelperClient>();

        Vms = new VmListViewModel(services);
        Images = new ImagesViewModel(services);
        Disks = new DisksViewModel(services);
        HostPage = new HostViewModel(services, () => ConnectHelperCommand.ExecuteAsync(null));

        Current = Vms;

        _helper.ConnectionChanged += connected => Dispatcher.UIThread.Post(() =>
        {
            HelperConnected = connected;
            if (!connected)
            {
                HelperMessage = "Helper disconnected. VM operations are paused until you reconnect.";
            }
        });

        // Deliberately not auto-relaunched when it drops: pkexec would silently
        // re-prompt for a password, which is hostile. The banner's Reconnect
        // button is the only way back.
        _ = ConnectHelperCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void Navigate(string section)
    {
        SelectedSection = section;
        Current = section switch
        {
            "Images" => Images,
            "Disks" => Disks,
            "Host" => HostPage,
            _ => Vms,
        };
    }

    [RelayCommand]
    private async Task ConnectHelperAsync()
    {
        if (Connecting) return;

        Connecting = true;
        HelperMessage = "Starting the privileged helper — check for a password prompt…";

        try
        {
            await _supervisor.EnsureRunningAsync();
            HelperConnected = _helper.IsConnected;
            HelperMessage = HelperConnected ? null : "The helper started but did not answer.";

            if (HelperConnected)
            {
                await HostPage.RunAdoptionAsync();
                await Vms.RefreshAsync();
                await Images.RefreshAsync();
                await Disks.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            HelperConnected = false;
            HelperMessage = ex is HelperUnavailableException
                ? ex.Message
                : $"Could not start the helper: {ex.Message}";
        }
        finally
        {
            Connecting = false;
        }
    }
}
