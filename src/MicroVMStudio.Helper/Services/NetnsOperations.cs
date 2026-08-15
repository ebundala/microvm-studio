using System.Text.Json;
using Mvm.Helper.V1;

namespace MicroVMStudio.Helper.Services;

/// <summary>
/// Bridge, network namespace, veth and TAP management via `ip` shell-out.
/// All operations are idempotent. Requires CAP_NET_ADMIN.
///
/// Ported from ShopCare's NetnsOperations with per-tenant bridging collapsed to
/// a single host bridge.
/// </summary>
public sealed class NetnsOperations(Shell shell, ILogger<NetnsOperations> logger)
{
    private const string IpBin = "/sbin/ip";

    public async Task<EnsureHostBridgeResponse> EnsureHostBridgeAsync(
        EnsureHostBridgeRequest req, CancellationToken ct)
    {
        if (await LinkExistsAsync(req.BridgeName, ct))
        {
            return new EnsureHostBridgeResponse { Created = false };
        }

        await shell.RunAsync(IpBin, ["link", "add", req.BridgeName, "type", "bridge"],
            throwOnNonZero: true, ct: ct);

        // The bridge holds the gateway address (.1 of the range); it is the only
        // host-side interface in this topology with an IP.
        var gatewayCidr = ComputeGatewayCidr(req.Cidr);
        await shell.RunAsync(IpBin, ["addr", "add", gatewayCidr, "dev", req.BridgeName],
            throwOnNonZero: true, ct: ct);

        await shell.RunAsync(IpBin, ["link", "set", req.BridgeName, "up"],
            throwOnNonZero: true, ct: ct);

        logger.LogInformation("Bridge {Bridge} created cidr={Cidr} trace={Trace}",
            req.BridgeName, req.Cidr, req.TraceId);

        return new EnsureHostBridgeResponse { Created = true };
    }

    public async Task DestroyHostBridgeAsync(DestroyHostBridgeRequest req, CancellationToken ct)
    {
        if (!await LinkExistsAsync(req.BridgeName, ct)) return;
        await shell.RunAsync(IpBin, ["link", "delete", req.BridgeName, "type", "bridge"], ct: ct);
        logger.LogInformation("Bridge {Bridge} deleted trace={Trace}", req.BridgeName, req.TraceId);
    }

    public async Task<CreateVmNetnsResponse> CreateVmNetnsAsync(
        CreateVmNetnsRequest req, CancellationToken ct)
    {
        // Topology inside the VM's netns:
        //
        //   host  mvm0 <─[veth]─> vh-XXXX (host side)
        //                         │ (peer)
        //   netns                 ▼
        //         lo (up)         ns-XXXX
        //                         │ (master)
        //                         ▼
        //                         br-int (bridge inside netns)
        //                         ▲ (master)
        //                         │
        //                         tap0 ←── Firecracker opens this as host_dev_name
        //
        // No IP is assigned to any host-side interface here. The guest configures
        // eth0 itself from agent_ip/agent_gw on the kernel cmdline (see init.go).
        //
        // Why TAP + bridge + veth rather than just a veth: Firecracker's
        // host_dev_name requires a real TAP device (it calls
        // ioctl(TUNSETIFF, IFF_TAP)); a veth's ifindex is not a TAP and the
        // ioctl fails with EINVAL. The in-netns bridge L2-forwards frames
        // between FC's TAP and the veth pair up to the host bridge.

        var shortId = ShortenId(req.VmId);
        var hostVeth = $"vh-{shortId}";
        var nsVeth = $"ns-{shortId}";
        const string brInt = "br-int";
        const string tapName = "tap0";

        if (!await NetnsExistsAsync(req.NetnsName, ct))
        {
            await shell.RunAsync(IpBin, ["netns", "add", req.NetnsName], throwOnNonZero: true, ct: ct);
        }

        if (!await LinkExistsAsync(hostVeth, ct))
        {
            await shell.RunAsync(IpBin,
                ["link", "add", hostVeth, "type", "veth", "peer", "name", nsVeth, "netns", req.NetnsName],
                throwOnNonZero: true, ct: ct);
        }

        // Not throwOnNonZero: re-attaching an already-attached veth is fine.
        await shell.RunAsync(IpBin, ["link", "set", hostVeth, "master", req.BridgeName], ct: ct);
        await shell.RunAsync(IpBin, ["link", "set", hostVeth, "up"], throwOnNonZero: true, ct: ct);

        await RunInNetnsAsync(req.NetnsName, ["link", "set", "lo", "up"], ct);

        if (!await NetnsLinkExistsAsync(req.NetnsName, brInt, ct))
        {
            await RunInNetnsAsync(req.NetnsName, ["link", "add", "name", brInt, "type", "bridge"], ct);
        }

        await RunInNetnsAsync(req.NetnsName, ["link", "set", nsVeth, "master", brInt], ct);
        await RunInNetnsAsync(req.NetnsName, ["link", "set", nsVeth, "up"], ct);

        // The user/group flags chown the TAP to the per-VM UID/GID so Firecracker,
        // which has already dropped privileges via jailer, can still open it
        // through /dev/net/tun.
        if (!await NetnsLinkExistsAsync(req.NetnsName, tapName, ct))
        {
            await RunInNetnsAsync(req.NetnsName,
                ["tuntap", "add", "dev", tapName, "mode", "tap",
                 "user", req.RuntimeUid.ToString(), "group", req.RuntimeGid.ToString()], ct);
        }

        await RunInNetnsAsync(req.NetnsName, ["link", "set", tapName, "master", brInt], ct);
        await RunInNetnsAsync(req.NetnsName, ["link", "set", tapName, "up"], ct);
        await RunInNetnsAsync(req.NetnsName, ["link", "set", brInt, "up"], ct);

        logger.LogInformation(
            "Netns {Netns} provisioned: host-veth={HostVeth} tap={Tap} uid={Uid} trace={Trace}",
            req.NetnsName, hostVeth, tapName, req.RuntimeUid, req.TraceId);

        return new CreateVmNetnsResponse
        {
            HostVethName = hostVeth,
            NetnsVethName = nsVeth,
            TapName = tapName,
        };
    }

    public async Task DestroyVmNetnsAsync(DestroyVmNetnsRequest req, CancellationToken ct)
    {
        // Deleting the netns destroys the inner veth half with it, and the kernel
        // auto-removes the host side once its peer is gone. Deleting the host
        // veth explicitly first makes the common case deterministic. Idempotent.
        if (!string.IsNullOrEmpty(req.HostVethName) && await LinkExistsAsync(req.HostVethName, ct))
        {
            await shell.RunAsync(IpBin, ["link", "delete", req.HostVethName], ct: ct);
        }
        if (await NetnsExistsAsync(req.NetnsName, ct))
        {
            await shell.RunAsync(IpBin, ["netns", "delete", req.NetnsName], ct: ct);
        }
        logger.LogInformation("Netns {Netns} destroyed trace={Trace}", req.NetnsName, req.TraceId);
    }

    // ── host diagnostics ────────────────────────────────────────────────────

    public async Task<ListHostVethsResponse> ListHostVethsAsync(CancellationToken ct)
    {
        var r = await shell.RunAsync(IpBin, ["-j", "link", "show", "type", "veth"], ct: ct);
        var response = new ListHostVethsResponse();
        if (!r.Succeeded || string.IsNullOrWhiteSpace(r.Stdout)) return response;

        using var doc = JsonDocument.Parse(r.Stdout);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.TryGetProperty("ifname", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            var master = el.TryGetProperty("master", out var m) ? m.GetString() ?? "" : "";
            response.Veths.Add(new HostVeth { Name = name, MasterBridge = master });
        }
        return response;
    }

    public async Task DeleteHostVethAsync(DeleteHostVethRequest req, CancellationToken ct)
    {
        if (!await LinkExistsAsync(req.Name, ct)) return;
        await shell.RunAsync(IpBin, ["link", "delete", req.Name], ct: ct);
        logger.LogInformation("Host veth {Name} deleted trace={Trace}", req.Name, req.TraceId);
    }

    /// <summary>Egress interface for MASQUERADE, resolved the way traffic actually routes.</summary>
    public async Task<string> DetectEgressInterfaceAsync(CancellationToken ct)
    {
        var r = await shell.RunAsync(IpBin, ["route", "get", "1.1.1.1"], ct: ct);
        if (!r.Succeeded) return "";
        // Output shape: "1.1.1.1 via 192.168.1.1 dev wlp2s0 src ..."
        var tokens = r.Stdout.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var devIdx = Array.IndexOf(tokens, "dev");
        return devIdx >= 0 && devIdx + 1 < tokens.Length ? tokens[devIdx + 1] : "";
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<bool> LinkExistsAsync(string name, CancellationToken ct) =>
        (await shell.RunAsync(IpBin, ["link", "show", "dev", name], ct: ct)).Succeeded;

    private async Task<bool> NetnsLinkExistsAsync(string netns, string name, CancellationToken ct) =>
        (await shell.RunAsync(IpBin,
            ["netns", "exec", netns, IpBin, "link", "show", "dev", name], ct: ct)).Succeeded;

    private async Task<bool> NetnsExistsAsync(string name, CancellationToken ct)
    {
        var r = await shell.RunAsync(IpBin, ["netns", "list"], ct: ct);
        if (!r.Succeeded) return false;
        // `ip netns list` prints "name (id: 0)" or bare "name" per line.
        foreach (var line in r.Stdout.Split('\n'))
        {
            if (line.Split(' ', '\t')[0].Trim() == name) return true;
        }
        return false;
    }

    private Task RunInNetnsAsync(string netns, string[] subArgs, CancellationToken ct)
    {
        string[] args = ["netns", "exec", netns, IpBin, .. subArgs];
        return shell.RunAsync(IpBin, args, throwOnNonZero: true, ct: ct);
    }

    private static string ShortenId(string id)
    {
        var stripped = id.Replace("-", "");
        return stripped.Length >= 8 ? stripped[..8] : stripped;
    }

    /// <summary>"10.100.0.0/24" → "10.100.0.1/24"</summary>
    private static string ComputeGatewayCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var addr = slash >= 0 ? cidr[..slash] : cidr;
        var bits = slash >= 0 ? cidr[(slash + 1)..] : "24";
        var parts = addr.Split('.');
        if (parts.Length != 4) throw new ArgumentException($"invalid CIDR: {cidr}");
        parts[3] = (int.Parse(parts[3]) + 1).ToString();
        return $"{string.Join('.', parts)}/{bits}";
    }
}
