using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroVMStudio.Core;
using MicroVMStudio.Core.Helper;
using MicroVMStudio.Core.Models;
using MicroVMStudio.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mvm.Helper.V1;

namespace MicroVMStudio.App.ViewModels;

/// <summary>
/// Data disks, including the orphans left behind when a VM is deleted with
/// "keep disk". Also diffs the host's disks directory against the database, so
/// a file left over from a delete that failed halfway is visible rather than
/// quietly consuming space forever.
/// </summary>
public partial class DisksViewModel(IServiceProvider services) : ViewModelBase
{
    private readonly VmManager _manager = services.GetRequiredService<VmManager>();
    private readonly HelperClient _helper = services.GetRequiredService<HelperClient>();
    private readonly StudioOptions _options =
        services.GetRequiredService<IOptions<StudioOptions>>().Value;

    [ObservableProperty] public partial string? Error { get; set; }

    public ObservableCollection<DiskRowViewModel> Disks { get; } = [];
    public ObservableCollection<UntrackedFileViewModel> UntrackedFiles { get; } = [];

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var disks = await _manager.ListDisksAsync();

            Disks.Clear();
            foreach (var disk in disks) Disks.Add(new DiskRowViewModel(disk));

            UntrackedFiles.Clear();
            if (!_helper.IsConnected) return;

            var known = disks.Select(d => d.PathOnHost).ToHashSet(StringComparer.Ordinal);
            var onHost = await _helper.Client.ListDiskFilesAsync(new ListDiskFilesRequest
            {
                DisksDir = _options.DisksDir,
            });

            foreach (var file in onHost.Files.Where(f => !known.Contains(f.PathOnHost)))
            {
                UntrackedFiles.Add(new UntrackedFileViewModel(file.PathOnHost, file.SizeBytes));
            }

            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

public sealed class DiskRowViewModel(VmDisk disk)
{
    public string Id { get; } = disk.Id;
    public string Path { get; } = disk.PathOnHost;
    public string Size { get; } = $"{disk.SizeGib} GiB";
    public DiskState State { get; } = disk.State;
    public string? VmId { get; } = disk.VmId;
    public string CreatedAt { get; } = disk.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public bool IsOrphaned => State == DiskState.Orphaned;
}

/// <summary>
/// A file in the disks directory with no database row. Size is real on-disk
/// usage, not the provisioned size — these images are sparse, so the logical
/// size grossly overstates what deleting one actually frees.
/// </summary>
public sealed class UntrackedFileViewModel(string path, long sizeBytes)
{
    public string Path { get; } = path;
    public string Size { get; } = sizeBytes >= 1L << 30
        ? $"{sizeBytes / (double)(1L << 30):0.#} GiB"
        : $"{sizeBytes / (double)(1L << 20):0.#} MiB";
}
