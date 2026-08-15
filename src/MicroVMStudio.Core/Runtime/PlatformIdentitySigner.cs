using System.Security.Cryptography;
using System.Text;
using MicroVMStudio.Core.Orchestration;

namespace MicroVMStudio.Core.Runtime;

/// <summary>
/// Signs the identity headers the ingress proxy injects on the way into a VM.
///
/// ShopCare does this so a guest workload can trust the caller's tenant/user
/// without ever holding a Keycloak token. There are no tenants here and the
/// proxy is loopback-only, but the mechanism is kept because the guest side
/// depends on it: the filebrowser auth proxy baked into the images provisions
/// its user from X-Mvm-User-Email and refuses the request without it.
///
/// The key is derived per VM from the host master secret, so the guest can
/// verify signatures using only what MMDS handed it at boot.
/// </summary>
public sealed class PlatformIdentitySigner(VmOrchestrator orchestrator)
{
    public const string TenantHeaderPrefix = "X-Mvm-";
    private const int ValiditySeconds = 300;

    public static readonly string[] SignedHeaders =
    [
        "X-Mvm-User-Id",
        "X-Mvm-User-Email",
        "X-Mvm-Trace-Id",
        "X-Mvm-Auth-Expires",
    ];

    /// <summary>
    /// The local desktop user. There is no authentication to derive this from —
    /// a single-user dev tool has exactly one identity — but the guest needs a
    /// stable, well-formed value, so it is synthesized deterministically.
    /// </summary>
    public string UserId { get; } = Environment.UserName;

    public string UserEmail { get; } = $"{Environment.UserName}@localhost";

    public IReadOnlyList<(string Name, string Value)> BuildHeaders(string vmId, string traceId)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(ValiditySeconds).ToUnixTimeSeconds().ToString();

        var values = new[] { UserId, UserEmail, traceId, expires };
        var canonical = string.Join('\n', SignedHeaders.Zip(values, (h, v) => $"{h}:{v}"));

        using var hmac = new HMACSHA256(orchestrator.DeriveVmSecret(vmId));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));

        return
        [
            .. SignedHeaders.Zip(values, (h, v) => (h, v)),
            ("X-Mvm-Auth-Sig", signature),
            ("X-Mvm-Auth-Version", "1"),
        ];
    }
}
