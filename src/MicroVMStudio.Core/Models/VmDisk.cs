namespace MicroVMStudio.Core.Models;

/// <summary>
/// A VM's persistent data disk, mounted as /dev/vdb and used by the guest init
/// as the overlayfs upper layer (the rootfs itself is a shared read-only inode).
///
/// VmId is a denormalized pointer, deliberately NOT an EF relationship: a disk
/// outlives the VM that created it when the user deletes with "keep disk", at
/// which point it becomes Orphaned and can be adopted by a new VM.
/// </summary>
public class VmDisk
{
    public string Id { get; set; } = null!;
    public string? VmId { get; set; }
    public string PathOnHost { get; set; } = null!;
    public DiskFormat Format { get; set; } = DiskFormat.Raw;
    public int SizeGib { get; set; }
    public DiskState State { get; set; } = DiskState.InUse;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAttachedAt { get; set; }
}

public enum DiskFormat
{
    Raw,
    Qcow2,
}

public enum DiskState
{
    InUse,
    Orphaned,
    Deleting,
}
