using System.Net;
using MicroVMStudio.Core.Data;
using MicroVMStudio.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MicroVMStudio.Core.Orchestration;

/// <summary>
/// Allocates the host-unique resources a VM needs: an IP on the bridge, a vsock
/// context id, and a UID/GID for jailer to drop privileges to.
///
/// All three pools are host-wide. ShopCare partitions them per tenant; with one
/// host and one user there is a single pool of each.
/// </summary>
public sealed class ResourceAllocator(IOptions<StudioOptions> options)
{
    private readonly StudioOptions _options = options.Value;

    /// <summary>
    /// Reserves the lowest free address in the bridge range. Must be called
    /// inside the serialized writer so two VMs can't race onto the same IP.
    /// </summary>
    public async Task<IpAllocation> AllocateIpAsync(
        StudioDbContext db, string vmId, CancellationToken ct)
    {
        var existing = await db.IpAllocations.FirstOrDefaultAsync(a => a.VmId == vmId, ct);
        if (existing is not null) return existing;

        var (network, prefix) = ParseCidr(_options.BridgeCidr);
        var networkInt = ToUInt32(network);
        var hostCount = 1u << (32 - prefix);

        var now = DateTime.UtcNow;
        var taken = await db.IpAllocations
            .Where(a => a.VmId != null || a.QuarantineUntil > now)
            .Select(a => a.PrivateIp)
            .ToListAsync(ct);
        var takenSet = taken.ToHashSet(StringComparer.Ordinal);

        // .0 is the network address and .1 is the bridge gateway; the last
        // address is broadcast. Everything between is assignable.
        for (var offset = 2u; offset < hostCount - 1; offset++)
        {
            var candidate = FromUInt32(networkInt + offset).ToString();
            if (takenSet.Contains(candidate)) continue;

            // Reuse a released row still sitting out its quarantine only if the
            // quarantine has expired; otherwise take a fresh row.
            var stale = await db.IpAllocations
                .FirstOrDefaultAsync(a => a.PrivateIp == candidate, ct);

            if (stale is not null)
            {
                stale.VmId = vmId;
                stale.QuarantineUntil = null;
                return stale;
            }

            var allocation = new IpAllocation
            {
                Id = Guid.NewGuid().ToString(),
                VmId = vmId,
                PrivateIp = candidate,
                CreatedAt = DateTime.UtcNow,
            };
            db.IpAllocations.Add(allocation);
            return allocation;
        }

        throw new InvalidOperationException(
            $"No free addresses left in {_options.BridgeCidr}. Delete some VMs or widen the range.");
    }

    /// <summary>
    /// Releases a VM's address into quarantine rather than straight back into
    /// the pool, so a new VM doesn't inherit an address that stale ARP entries
    /// or lingering connections still point at.
    /// </summary>
    public static void ReleaseIp(IpAllocation allocation, int quarantineMinutes)
    {
        allocation.VmId = null;
        allocation.QuarantineUntil = DateTime.UtcNow.AddMinutes(quarantineMinutes);
    }

    public async Task<int> AllocateVsockCidAsync(StudioDbContext db, CancellationToken ct)
    {
        var used = await db.Vms.Select(v => v.VsockCid).ToListAsync(ct);
        var usedSet = used.ToHashSet();

        for (var cid = _options.VsockCidPoolStart; cid <= _options.VsockCidPoolEnd; cid++)
        {
            if (!usedSet.Contains(cid)) return cid;
        }
        throw new InvalidOperationException("No free vsock context ids remain.");
    }

    /// <summary>
    /// Picks a UID/GID for jailer to drop to. One per VM so a compromised guest
    /// cannot touch another VM's chroot or disk.
    /// </summary>
    public async Task<int> AllocateRuntimeUidAsync(StudioDbContext db, CancellationToken ct)
    {
        var used = await db.Vms.Select(v => v.RuntimeUid).ToListAsync(ct);
        var usedSet = used.ToHashSet();

        for (var uid = _options.RuntimeUidPoolStart; uid <= _options.RuntimeUidPoolEnd; uid++)
        {
            if (!usedSet.Contains(uid)) return uid;
        }
        throw new InvalidOperationException("No free runtime UIDs remain.");
    }

    public string GatewayIp
    {
        get
        {
            var (network, _) = ParseCidr(_options.BridgeCidr);
            return FromUInt32(ToUInt32(network) + 1).ToString();
        }
    }

    public int PrefixLength => ParseCidr(_options.BridgeCidr).Prefix;

    private static (IPAddress Network, int Prefix) ParseCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0) throw new ArgumentException($"Invalid CIDR: {cidr}");
        return (IPAddress.Parse(cidr[..slash]), int.Parse(cidr[(slash + 1)..]));
    }

    private static uint ToUInt32(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt32(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    });
}
