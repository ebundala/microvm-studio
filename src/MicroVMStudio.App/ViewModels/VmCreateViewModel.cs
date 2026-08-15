using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroVMStudio.Core.Models;
using MicroVMStudio.Core.Orchestration;

namespace MicroVMStudio.App.ViewModels;

/// <summary>
/// Use case 2: define a VM from a kernel + rootfs image in the catalog.
///
/// Also the only place an orphaned disk can be adopted — that is how a deleted
/// VM's data comes back, so it is surfaced here rather than buried in Disks.
/// </summary>
public partial class VmCreateViewModel : ViewModelBase
{
    private readonly Func<CreateVmRequest, Task> _submit;
    private readonly Action _cancel;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial VmImage? Kernel { get; set; }
    [ObservableProperty] public partial VmImage? Rootfs { get; set; }
    [ObservableProperty] public partial int Vcpu { get; set; } = 1;
    [ObservableProperty] public partial int MemoryMib { get; set; } = 512;
    [ObservableProperty] public partial int PrimaryPort { get; set; } = 8080;
    [ObservableProperty] public partial int DiskSizeGib { get; set; } = 5;
    [ObservableProperty] public partial VmDisk? AdoptDisk { get; set; }
    [ObservableProperty] public partial RestartPolicy RestartPolicy { get; set; } = RestartPolicy.OnFailure;
    [ObservableProperty] public partial bool StartImmediately { get; set; } = true;
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool Submitting { get; set; }

    public ObservableCollection<VmImage> Kernels { get; }
    public ObservableCollection<VmImage> RootfsImages { get; }
    public ObservableCollection<VmDisk> AdoptableDisks { get; }

    public IReadOnlyList<RestartPolicy> RestartPolicies { get; } =
        [RestartPolicy.Never, RestartPolicy.OnFailure, RestartPolicy.Always];

    public bool HasImages => Kernels.Count > 0 && RootfsImages.Count > 0;

    /// A disk carries its own size, so the size field is meaningless when adopting.
    public bool CanSetDiskSize => AdoptDisk is null;

    public VmCreateViewModel(
        IReadOnlyList<VmImage> images,
        IReadOnlyList<VmDisk> orphanedDisks,
        Func<CreateVmRequest, Task> submit,
        Action cancel)
    {
        _submit = submit;
        _cancel = cancel;

        Kernels = [.. images.Where(i => i.Kind == ImageKind.Kernel)];
        RootfsImages = [.. images.Where(i => i.Kind == ImageKind.Rootfs)];
        AdoptableDisks = [.. orphanedDisks];

        Kernel = Kernels.FirstOrDefault();
        Rootfs = RootfsImages.FirstOrDefault();
    }

    partial void OnAdoptDiskChanged(VmDisk? value) => OnPropertyChanged(nameof(CanSetDiskSize));

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (Kernel is null || Rootfs is null)
        {
            Error = "Pick a kernel and a rootfs image. Build one on the Images page if the list is empty.";
            return;
        }

        Submitting = true;
        try
        {
            await _submit(new CreateVmRequest
            {
                Name = Name.Trim(),
                KernelImageId = Kernel.Id,
                RootfsImageId = Rootfs.Id,
                Vcpu = (short)Vcpu,
                MemoryMib = MemoryMib,
                PrimaryPort = PrimaryPort,
                DiskSizeGib = DiskSizeGib,
                AdoptDiskId = AdoptDisk?.Id,
                RestartPolicy = RestartPolicy,
                StartImmediately = StartImmediately,
            });
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Submitting = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancel();
}
