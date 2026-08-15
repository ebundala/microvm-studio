using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroVMStudio.Core.Models;
using MicroVMStudio.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace MicroVMStudio.App.ViewModels;

/// <summary>
/// Use case 1: define rootfs images from the build pipeline.
///
/// The distro list is a directory listing of images/rootfs/distros/, so adding a
/// Dockerfile there is all it takes to make a new image type buildable — no code
/// change, no registration step.
/// </summary>
public partial class ImagesViewModel : ViewModelBase
{
    private readonly ImageCatalog _catalog;
    private CancellationTokenSource? _build;

    [ObservableProperty] public partial string? SelectedDistro { get; set; }
    [ObservableProperty] public partial bool Building { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string ImportPath { get; set; } = "";
    [ObservableProperty] public partial string ImportName { get; set; } = "";
    [ObservableProperty] public partial ImageKind ImportKind { get; set; } = ImageKind.Rootfs;

    public ObservableCollection<string> Distros { get; } = [];
    public ObservableCollection<ImageRowViewModel> Images { get; } = [];

    /// Streamed `make` output. A rootfs build takes minutes; without this the UI
    /// would look hung and a failing Dockerfile would be undiagnosable.
    public ObservableCollection<string> BuildLog { get; } = [];

    public ImagesViewModel(IServiceProvider services)
    {
        _catalog = services.GetRequiredService<ImageCatalog>();

        foreach (var distro in _catalog.ListDistros()) Distros.Add(distro);
        SelectedDistro = Distros.Count > 0 ? Distros[0] : null;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var images = await _catalog.ListAsync();
            var usage = await _catalog.GetUsageAsync();

            Images.Clear();
            foreach (var image in images)
            {
                Images.Add(new ImageRowViewModel(
                    image, usage.TryGetValue(image.Id, out var count) ? count : 0));
            }
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task BuildRootfsAsync()
    {
        if (SelectedDistro is null || Building) return;
        await RunBuildAsync(ct => _catalog.BuildRootfsAsync(SelectedDistro, AppendLog, ct));
    }

    [RelayCommand]
    private async Task BuildKernelAsync()
    {
        if (Building) return;
        await RunBuildAsync(ct => _catalog.BuildKernelAsync(AppendLog, ct));
    }

    [RelayCommand]
    private void CancelBuild() => _build?.Cancel();

    [RelayCommand]
    private async Task ImportAsync()
    {
        try
        {
            await _catalog.ImportAsync(ImportPath.Trim(), ImportKind, ImportName.Trim());
            ImportPath = "";
            ImportName = "";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ImageRowViewModel row)
    {
        try
        {
            await _catalog.DeleteAsync(row.Id);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task RunBuildAsync(Func<CancellationToken, Task<VmImage>> build)
    {
        Building = true;
        Error = null;
        BuildLog.Clear();

        _build = new CancellationTokenSource();
        try
        {
            await build(_build.Token);
            AppendLog(">>> Build complete.");
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            AppendLog(">>> Build cancelled.");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            AppendLog($">>> FAILED: {ex.Message}");
        }
        finally
        {
            _build?.Dispose();
            _build = null;
            Building = false;
        }
    }

    private void AppendLog(string line) => Dispatcher.UIThread.Post(() =>
    {
        BuildLog.Add(line);
        while (BuildLog.Count > 2000) BuildLog.RemoveAt(0);
    });
}

public sealed class ImageRowViewModel(VmImage image, int usedByVms)
{
    public string Id { get; } = image.Id;
    public string Name { get; } = image.Name;
    public ImageKind Kind { get; } = image.Kind;
    public string ShortDigest { get; } = image.Digest.Length > 19 ? image.Digest[7..19] : image.Digest;
    public string Size { get; } = FormatBytes(image.SizeBytes);
    public string BuiltAt { get; } = image.BuiltAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string? DistroDir { get; } = image.DistroDir;
    public int UsedByVms { get; } = usedByVms;

    public bool CanDelete => UsedByVms == 0;
    public string UsageLabel => UsedByVms == 0 ? "unused" : $"used by {UsedByVms} VM(s)";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MiB",
        _ => $"{bytes / 1024.0:0.#} KiB",
    };
}
