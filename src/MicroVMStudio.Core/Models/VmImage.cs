namespace MicroVMStudio.Core.Models;

/// <summary>
/// A kernel or rootfs artifact registered in the catalog, content-addressed by
/// sha256. Kernel and rootfs files are hard-linked into every VM's jailer
/// chroot, so the same inode is shared across all VMs using the image — which
/// is why the rootfs drive is always attached read-only.
/// </summary>
public class VmImage
{
    public string Id { get; set; } = null!;
    public ImageKind Kind { get; set; }
    public string Name { get; set; } = null!;          // e.g. "alpine", "hermes", "vmlinux-5.10.225"
    public string Digest { get; set; } = null!;        // "sha256:<hex>", unique
    public string PathOnHost { get; set; } = null!;
    public string? Description { get; set; }

    /// The images/rootfs/distros/&lt;dir&gt; this was built from, when it was built
    /// rather than imported. Lets the UI offer "rebuild" and ties a catalog row
    /// back to its Dockerfile.
    public string? DistroDir { get; set; }

    public long SizeBytes { get; set; }
    public DateTime BuiltAt { get; set; }
}

public enum ImageKind
{
    Kernel,
    Rootfs,
}
