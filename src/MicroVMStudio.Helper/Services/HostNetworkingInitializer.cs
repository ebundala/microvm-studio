namespace MicroVMStudio.Helper.Services;

/// <summary>
/// One-time host networking setup: NAT masquerading so VMs can reach the
/// outside world through whichever interface the host actually routes over.
///
/// Runs once at helper startup rather than per VM — MASQUERADE is a host-wide
/// concern and installing it repeatedly would just add duplicate rules.
/// </summary>
public sealed class HostNetworkingInitializer(
    NetnsOperations netns,
    IptablesOperations iptables,
    IConfiguration config,
    ILogger<HostNetworkingInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cidr = config.GetValue<string>("Studio:BridgeCidr") ?? "10.100.0.0/24";

        try
        {
            var egressIface = await netns.DetectEgressInterfaceAsync(ct);
            if (string.IsNullOrEmpty(egressIface))
            {
                logger.LogWarning(
                    "Could not determine the host egress interface; VM outbound traffic will not be "
                    + "NATed until the host has a default route.");
                return;
            }

            await iptables.EnsureMasqueradeAsync(cidr, egressIface, ct);
            logger.LogInformation("Host networking ready: {Cidr} masqueraded via {Iface}", cidr, egressIface);
        }
        catch (Exception ex)
        {
            // Never fail helper startup over this: VM lifecycle still works
            // without egress, and the failure surfaces on the next iptables call.
            logger.LogError(ex,
                "Host networking initialization failed; MASQUERADE not installed. "
                + "VMs will boot but may have no outbound connectivity.");
        }
    }
}
