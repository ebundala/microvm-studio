using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroVMStudio.Core;
using MicroVMStudio.Core.Models;
using MicroVMStudio.Core.Orchestration;
using MicroVMStudio.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MicroVMStudio.App.ViewModels;

/// <summary>
/// Everything about one VM: state, what is listening inside it, its console
/// output, its secrets and its egress rules.
/// </summary>
public partial class VmDetailViewModel : ViewModelBase, IDisposable
{
    private readonly VmManager _manager;
    private readonly VmServiceRegistry _registry;
    private readonly VmLogHub _logs;
    private readonly StudioOptions _options;
    private readonly string _vmId;

    private readonly Action<string, string> _lineHandler;
    private readonly Action<string> _servicesHandler;

    [ObservableProperty] public partial Vm? Vm { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool LogsPaused { get; set; }

    [ObservableProperty] public partial string NewSecretKey { get; set; } = "";
    [ObservableProperty] public partial string NewSecretValue { get; set; } = "";

    [ObservableProperty] public partial string NewRuleCidr { get; set; } = "";
    [ObservableProperty] public partial EgressEffect NewRuleEffect { get; set; } = EgressEffect.Allow;

    public ObservableCollection<ServiceLinkViewModel> Services { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<SecretSummary> Secrets { get; } = [];
    public ObservableCollection<VmEgressRule> EgressRules { get; } = [];
    public ObservableCollection<VmEvent> Events { get; } = [];

    public string ShellUrl => Vm is null
        ? ""
        : $"http://127.0.0.1:{_options.ProxyPort}/vms/{Vm.Name}/p/{VmServiceRegistry.Shell.Port}/ws";

    public VmDetailViewModel(IServiceProvider services, string vmId)
    {
        _vmId = vmId;
        _manager = services.GetRequiredService<VmManager>();
        _registry = services.GetRequiredService<VmServiceRegistry>();
        _logs = services.GetRequiredService<VmLogHub>();
        _options = services.GetRequiredService<IOptions<StudioOptions>>().Value;

        _lineHandler = (id, line) =>
        {
            if (id != _vmId || LogsPaused) return;
            Dispatcher.UIThread.Post(() =>
            {
                LogLines.Add(line);
                // Match the hub's own retention so a long-running VM cannot grow
                // the UI's copy without bound.
                while (LogLines.Count > _options.LogBufferLines) LogLines.RemoveAt(0);
            });
        };
        _logs.LineAppended += _lineHandler;

        _servicesHandler = id =>
        {
            if (id == _vmId) Dispatcher.UIThread.Post(RefreshServices);
        };
        _registry.ServicesChanged += _servicesHandler;

        foreach (var line in _logs.Tail(vmId, _options.LogBufferLines)) LogLines.Add(line);

        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            Vm = await _manager.GetAsync(_vmId);
            if (Vm is null) return;

            Replace(Secrets, await _manager.ListSecretsAsync(_vmId));
            Replace(EgressRules, await _manager.ListEgressRulesAsync(_vmId));
            Replace(Events, await _manager.GetEventsAsync(_vmId, 50));
            RefreshServices();

            OnPropertyChanged(nameof(ShellUrl));
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>
    /// Use case 4: what is running inside the VM, reachable from localhost.
    /// Stock services show immediately; guest-declared ones appear when the
    /// SERVICES handshake lands.
    /// </summary>
    private void RefreshServices()
    {
        if (Vm is null) return;

        var running = Vm.Status == VmStatus.Running;
        var declared = _registry.GetAll(_vmId, Vm.PrimaryPort);

        Services.Clear();
        foreach (var service in declared)
        {
            Services.Add(new ServiceLinkViewModel(
                service.Label,
                service.Port,
                $"http://127.0.0.1:{_options.ProxyPort}/vms/{Vm.Name}/p/{service.Port}/",
                running));
        }
    }

    [RelayCommand]
    private void OpenService(ServiceLinkViewModel service)
    {
        try
        {
            Process.Start(new ProcessStartInfo(service.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error = $"Could not open a browser: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddSecretAsync()
    {
        try
        {
            await _manager.SetSecretAsync(_vmId, NewSecretKey.Trim(), NewSecretValue);
            NewSecretKey = "";
            NewSecretValue = "";
            Replace(Secrets, await _manager.ListSecretsAsync(_vmId));
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteSecretAsync(SecretSummary secret)
    {
        try
        {
            await _manager.DeleteSecretAsync(_vmId, secret.Id);
            Replace(Secrets, await _manager.ListSecretsAsync(_vmId));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void AddEgressRule()
    {
        if (string.IsNullOrWhiteSpace(NewRuleCidr)) return;

        EgressRules.Add(new VmEgressRule
        {
            Id = Guid.NewGuid().ToString(),
            VmId = _vmId,
            Cidr = NewRuleCidr.Trim(),
            Effect = NewRuleEffect,
            Priority = (EgressRules.Count + 1) * 10,
        });
        NewRuleCidr = "";
    }

    [RelayCommand]
    private void RemoveEgressRule(VmEgressRule rule) => EgressRules.Remove(rule);

    /// <summary>
    /// Pushes the whole rule set to the host firewall — the helper rebuilds the
    /// chain from scratch, so local edits are not live until this runs.
    /// </summary>
    [RelayCommand]
    private async Task ApplyEgressRulesAsync()
    {
        try
        {
            await _manager.ReplaceEgressRulesAsync(_vmId,
                [.. EgressRules.Select(r => new EgressRuleInput(r.Cidr, r.Effect, r.Priority))]);
            Replace(EgressRules, await _manager.ListEgressRulesAsync(_vmId));
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        LogLines.Clear();
        _logs.Clear(_vmId);
    }

    [RelayCommand]
    private void TogglePauseLogs() => LogsPaused = !LogsPaused;

    private static void Replace<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    public void Dispose()
    {
        _logs.LineAppended -= _lineHandler;
        _registry.ServicesChanged -= _servicesHandler;
    }
}

public sealed record ServiceLinkViewModel(string Label, int Port, string Url, bool Available);
