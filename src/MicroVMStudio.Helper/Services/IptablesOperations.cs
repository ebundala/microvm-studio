using Mvm.Helper.V1;

namespace MicroVMStudio.Helper.Services;

/// <summary>
/// iptables rules: host-wide MASQUERADE for VM egress, plus an optional
/// per-VM allow/deny chain.
///
/// Rule layout:
///   nat/POSTROUTING:  -s &lt;bridge cidr&gt; -o &lt;egress iface&gt; -j MASQUERADE   (one global rule)
///   filter/MVM_&lt;id8&gt;: one chain per VM, rebuilt in full on every apply
///   filter/FORWARD:   -s &lt;vm ip&gt; -j MVM_&lt;id8&gt;                            (jump on egress)
///
/// Deliberately unlike ShopCare, there are no built-in platform denies. ShopCare
/// rejects all of RFC1918 to keep tenants off the platform's own network; on a
/// developer's machine reaching the host, the LAN and other VMs is the entire
/// point. Default is allow-all; restriction is opt-in per VM.
/// </summary>
public sealed class IptablesOperations(Shell shell, ILogger<IptablesOperations> logger)
{
    private const string IptablesBin = "/sbin/iptables";

    public async Task EnsureMasqueradeAsync(string cidr, string egressInterface, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(egressInterface))
        {
            logger.LogWarning("No egress interface detected; skipping MASQUERADE install");
            return;
        }

        string[] check = ["-t", "nat", "-C", "POSTROUTING", "-s", cidr, "-o", egressInterface, "-j", "MASQUERADE"];
        if ((await shell.RunAsync(IptablesBin, check, ct: ct)).Succeeded) return;

        string[] add = ["-t", "nat", "-A", "POSTROUTING", "-s", cidr, "-o", egressInterface, "-j", "MASQUERADE"];
        await shell.RunAsync(IptablesBin, add, throwOnNonZero: true, ct: ct);
        logger.LogInformation("iptables MASQUERADE installed for {Cidr} → {Iface}", cidr, egressInterface);
    }

    public async Task ApplyVmEgressRulesAsync(ApplyVmEgressRulesRequest req, CancellationToken ct)
    {
        var chain = ChainNameFor(req.VmId);
        var source = req.PrivateIp;

        var chainExists = (await shell.RunAsync(IptablesBin, ["-L", chain, "-n"], ct: ct)).Succeeded;
        if (!chainExists)
        {
            await shell.RunAsync(IptablesBin, ["-N", chain], throwOnNonZero: true, ct: ct);
        }
        else
        {
            // Declarative: the caller sends the complete rule set, so replace
            // rather than append.
            await shell.RunAsync(IptablesBin, ["-F", chain], throwOnNonZero: true, ct: ct);
        }

        // Lower priority number wins, so it must be emitted first — iptables
        // evaluates a chain top to bottom and stops at the first verdict.
        var sorted = req.Rules.OrderBy(r => r.Priority).ToList();
        foreach (var rule in sorted)
        {
            var verdict = rule.Effect == CidrRule.Types.Effect.Allow ? "ACCEPT" : "REJECT";
            await shell.RunAsync(IptablesBin, ["-A", chain, "-d", rule.Cidr, "-j", verdict],
                throwOnNonZero: true, ct: ct);
        }

        // RETURN at the end so an unmatched packet falls back to the default
        // FORWARD policy (allow) rather than being implicitly dropped.
        await shell.RunAsync(IptablesBin, ["-A", chain, "-j", "RETURN"], throwOnNonZero: true, ct: ct);

        string[] jumpCheck = ["-C", "FORWARD", "-s", source, "-j", chain];
        if (!(await shell.RunAsync(IptablesBin, jumpCheck, ct: ct)).Succeeded)
        {
            await shell.RunAsync(IptablesBin, ["-A", "FORWARD", "-s", source, "-j", chain],
                throwOnNonZero: true, ct: ct);
        }

        logger.LogInformation(
            "iptables egress rules applied vm={Vm} chain={Chain} count={Count} trace={Trace}",
            req.VmId, chain, sorted.Count, req.TraceId);
    }

    /// <summary>Removes a VM's chain and the FORWARD jump into it. Best-effort.</summary>
    public async Task RemoveVmEgressChainAsync(string vmId, string privateIp, CancellationToken ct)
    {
        var chain = ChainNameFor(vmId);
        if (!string.IsNullOrEmpty(privateIp))
        {
            await shell.RunAsync(IptablesBin, ["-D", "FORWARD", "-s", privateIp, "-j", chain], ct: ct);
        }
        await shell.RunAsync(IptablesBin, ["-F", chain], ct: ct);
        await shell.RunAsync(IptablesBin, ["-X", chain], ct: ct);
    }

    /// iptables caps chain names at 28 characters; "MVM_" + 8 hex = 12.
    private static string ChainNameFor(string vmId)
    {
        var stripped = vmId.Replace("-", "");
        return $"MVM_{(stripped.Length >= 8 ? stripped[..8] : stripped)}";
    }
}
