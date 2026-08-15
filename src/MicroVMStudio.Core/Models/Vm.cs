using System.ComponentModel.DataAnnotations;

namespace MicroVMStudio.Core.Models;

/// <summary>
/// A Firecracker microVM. Host-scoped: unlike ShopCare's Agent, there is no
/// tenant column and no row-level security — one host, one user.
/// </summary>
public class Vm
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;              // [a-z0-9-]{1,32}, unique per host
    public VmStatus Status { get; set; }
    public VmDesiredStatus DesiredStatus { get; set; } = VmDesiredStatus.Running;

    public long? FirecrackerPid { get; set; }              // jailer PID — the supervised parent
    public string? NetnsName { get; set; }
    public string? PrivateIp { get; set; }
    public int VsockCid { get; set; }

    public string DiskId { get; set; } = null!;
    public short Vcpu { get; set; }
    public int MemoryMib { get; set; }
    public string KernelImageId { get; set; } = null!;
    public string RootfsImageId { get; set; } = null!;
    public int PrimaryPort { get; set; }
    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.OnFailure;

    public int RuntimeUid { get; set; }
    public int RuntimeGid { get; set; }
    public int RestartCount { get; set; }
    public DateTime? LastRestartAt { get; set; }

    /// Carries the caller's purge choice across to the reconciler tick that
    /// actually performs teardown — a separate scope from the one that chose.
    public bool? PurgeDiskOnDelete { get; set; }

    public DateTime CreatedAt { get; set; }

    [ConcurrencyCheck]
    public DateTime UpdatedAt { get; set; }

    public DateTime LastStateChangeAt { get; set; }

    public virtual VmDisk Disk { get; set; } = null!;
    public virtual ICollection<VmEvent> Events { get; set; } = new List<VmEvent>();
    public virtual ICollection<VmSecret> Secrets { get; set; } = new List<VmSecret>();
    public virtual ICollection<VmEgressRule> EgressRules { get; set; } = new List<VmEgressRule>();
    public virtual IpAllocation? IpAllocation { get; set; }
}

public enum VmStatus
{
    Pending,
    Provisioning,
    Booting,
    Running,
    Stopping,
    Stopped,
    Crashed,
    CrashLoopBackoff,
    BootFailed,
}

public enum VmDesiredStatus
{
    Running,
    Stopped,
    Deleted,
}

public enum RestartPolicy
{
    Never,
    OnFailure,
    Always,
}
