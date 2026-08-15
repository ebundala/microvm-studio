namespace MicroVMStudio.Core.Models;

/// <summary>
/// A key/value secret delivered to the guest through Firecracker MMDS v2, where
/// the guest init writes it to /run/secrets/&lt;KEY&gt; on tmpfs. The guest polls
/// MMDS every 10s, so writes to a running VM take effect without a restart.
/// Values are encrypted at rest with the host key file.
/// </summary>
public class VmSecret
{
    public string Id { get; set; } = null!;
    public string VmId { get; set; } = null!;
    public string Key { get; set; } = null!;             // ^[A-Z_][A-Z0-9_]{0,63}$ — env-var safe
    public byte[] ValueEncrypted { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public virtual Vm Vm { get; set; } = null!;
}

/// <summary>
/// A per-VM egress firewall rule. The full rule set is rebuilt on the host on
/// every apply — the helper RPC is declarative, not incremental.
/// </summary>
public class VmEgressRule
{
    public string Id { get; set; } = null!;
    public string VmId { get; set; } = null!;
    public string Cidr { get; set; } = null!;
    public EgressEffect Effect { get; set; }
    public int Priority { get; set; } = 100;
    public DateTime CreatedAt { get; set; }

    public virtual Vm Vm { get; set; } = null!;
}

public enum EgressEffect
{
    Allow,
    Deny,
}

/// <summary>
/// Append-only audit trail of state transitions, shown as the timeline on the
/// VM detail view. PayloadJson carries {code, detail} on failures.
/// </summary>
public class VmEvent
{
    public string Id { get; set; } = null!;
    public string VmId { get; set; } = null!;
    public string EventType { get; set; } = null!;
    public VmStatus? PrevStatus { get; set; }
    public VmStatus? NewStatus { get; set; }
    public string? PayloadJson { get; set; }
    public DateTime OccurredAt { get; set; }

    public virtual Vm Vm { get; set; } = null!;
}

/// <summary>
/// Reserves a private IP on the host bridge. Rows survive VM deletion with a
/// QuarantineUntil set, so a freshly created VM doesn't immediately inherit an
/// address that stale ARP entries or lingering connections still point at.
/// </summary>
public class IpAllocation
{
    public string Id { get; set; } = null!;
    public string? VmId { get; set; }
    public string PrivateIp { get; set; } = null!;
    public DateTime? QuarantineUntil { get; set; }
    public DateTime CreatedAt { get; set; }

    public virtual Vm? Vm { get; set; }
}

/// <summary>
/// Host-level configuration, editable from the Host view. Kept in the database
/// rather than a config file so the UI is the single source of truth.
/// </summary>
public class HostSetting
{
    public string Key { get; set; } = null!;
    public string Value { get; set; } = null!;
}
