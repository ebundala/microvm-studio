using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroVMStudio.Core.Models;
using MicroVMStudio.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace MicroVMStudio.App.ViewModels;

/// <summary>
/// Use case 3: create / start / stop / restart / delete.
///
/// Every action here just writes intent and returns — the reconciler does the
/// work — so the list refreshes on the reconciler's tick rather than waiting on
/// a 30-second boot to return.
/// </summary>
public partial class VmListViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly VmManager _manager;
    private readonly ImageCatalog _catalog;

    [ObservableProperty] public partial VmRowViewModel? Selected { get; set; }
    [ObservableProperty] public partial VmDetailViewModel? Detail { get; set; }
    [ObservableProperty] public partial VmCreateViewModel? CreateForm { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    public ObservableCollection<VmRowViewModel> Items { get; } = [];

    public VmListViewModel(IServiceProvider services)
    {
        _services = services;
        _manager = services.GetRequiredService<VmManager>();
        _catalog = services.GetRequiredService<ImageCatalog>();

        var reconciler = services.GetRequiredService<VmReconciler>();
        reconciler.Changed += () => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    }

    partial void OnSelectedChanged(VmRowViewModel? value)
    {
        Detail?.Dispose();
        Detail = value is null ? null : new VmDetailViewModel(_services, value.Id);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var vms = await _manager.ListAsync();
            var selectedId = Selected?.Id;

            // Update in place rather than rebuilding: replacing the collection on
            // every 2-second tick would drop the user's selection and scroll
            // position mid-interaction.
            foreach (var vm in vms)
            {
                var existing = Items.FirstOrDefault(i => i.Id == vm.Id);
                if (existing is null) Items.Add(new VmRowViewModel(vm));
                else existing.Update(vm);
            }

            foreach (var stale in Items.Where(i => vms.All(v => v.Id != i.Id)).ToList())
            {
                Items.Remove(stale);
            }

            if (selectedId is not null && Selected is null)
            {
                Selected = Items.FirstOrDefault(i => i.Id == selectedId);
            }

            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task StartAsync(VmRowViewModel row) => await ActAsync(() => _manager.StartAsync(row.Id));

    [RelayCommand]
    private async Task StopAsync(VmRowViewModel row) => await ActAsync(() => _manager.StopAsync(row.Id));

    [RelayCommand]
    private async Task RestartAsync(VmRowViewModel row) => await ActAsync(() => _manager.RestartAsync(row.Id));

    [RelayCommand]
    private async Task DeleteAsync(VmRowViewModel row) =>
        await ActAsync(() => _manager.DeleteAsync(row.Id, row.PurgeDiskOnDelete));

    [RelayCommand]
    private async Task NewAsync()
    {
        var images = await _catalog.ListAsync();
        var disks = await _manager.ListDisksAsync();

        CreateForm = new VmCreateViewModel(
            images,
            [.. disks.Where(d => d.State == DiskState.Orphaned)],
            async request =>
            {
                await _manager.CreateAsync(request);
                CreateForm = null;
                await RefreshAsync();
            },
            () => CreateForm = null);
    }

    private async Task ActAsync(Func<Task> action)
    {
        try
        {
            await action();
            Error = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

/// <summary>One row in the VM table. Mutated in place so the grid stays stable.</summary>
public partial class VmRowViewModel(Vm vm) : ViewModelBase
{
    public string Id { get; } = vm.Id;

    [ObservableProperty] public partial string Name { get; set; } = vm.Name;
    [ObservableProperty] public partial VmStatus Status { get; set; } = vm.Status;
    [ObservableProperty] public partial string PrivateIp { get; set; } = vm.PrivateIp ?? "—";
    [ObservableProperty] public partial string Resources { get; set; } = $"{vm.Vcpu} vCPU · {vm.MemoryMib} MiB";
    [ObservableProperty] public partial string Uptime { get; set; } = FormatUptime(vm);
    [ObservableProperty] public partial int PrimaryPort { get; set; } = vm.PrimaryPort;

    /// <summary>Delete keeps the data disk by default — losing it is unrecoverable.</summary>
    [ObservableProperty] public partial bool PurgeDiskOnDelete { get; set; }

    public bool CanStart => Status is VmStatus.Stopped or VmStatus.Crashed
                                    or VmStatus.CrashLoopBackoff or VmStatus.BootFailed;
    public bool CanStop => Status is VmStatus.Running or VmStatus.Booting or VmStatus.Provisioning;
    public bool IsBusy => Status is VmStatus.Provisioning or VmStatus.Booting or VmStatus.Stopping;

    public void Update(Vm vm)
    {
        Name = vm.Name;
        Status = vm.Status;
        PrivateIp = vm.PrivateIp ?? "—";
        Resources = $"{vm.Vcpu} vCPU · {vm.MemoryMib} MiB";
        Uptime = FormatUptime(vm);
        PrimaryPort = vm.PrimaryPort;

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsBusy));
    }

    private static string FormatUptime(Vm vm)
    {
        if (vm.Status != VmStatus.Running) return "—";

        var elapsed = DateTime.UtcNow - vm.LastStateChangeAt;
        return elapsed switch
        {
            { TotalDays: >= 1 } => $"{(int)elapsed.TotalDays}d {elapsed.Hours}h",
            { TotalHours: >= 1 } => $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m",
            { TotalMinutes: >= 1 } => $"{(int)elapsed.TotalMinutes}m",
            _ => $"{(int)elapsed.TotalSeconds}s",
        };
    }
}
