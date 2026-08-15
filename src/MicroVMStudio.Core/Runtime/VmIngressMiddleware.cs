using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace MicroVMStudio.Core.Runtime;

/// <summary>
/// Reverse-proxies loopback requests into a VM's private network.
///
///     http://127.0.0.1:7000/vms/dev1/p/7682/files  →  http://10.100.0.42:7682/files
///
/// The path scheme is ShopCare's with the scope word changed, and that is not
/// cosmetic: the guest's filebrowser is launched with a matching
/// <c>--baseURL /vms/&lt;name&gt;/p/&lt;port&gt;</c>, so if the prefix the proxy
/// strips and the base URL the guest advertises ever disagree, the page shell
/// renders but every static asset 404s.
/// </summary>
public static partial class VmIngressMiddleware
{
    [GeneratedRegex(@"^/vms/(?<name>[a-z0-9-]{1,32})/p/(?<port>\d{1,5})(?<rest>/.*)?$",
        RegexOptions.IgnoreCase)]
    public static partial Regex PathRegex { get; }

    public static void MapVmIngress(this WebApplication app)
    {
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var routes = app.Services.GetRequiredService<VmRoutingTable>();
        var signer = app.Services.GetRequiredService<PlatformIdentitySigner>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VmIngress");

        // One long-lived client for all VMs. Redirects are left to the caller so
        // a guest 302 to an absolute URL is visible rather than silently followed
        // out of the proxy's path space.
        var client = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            ActivityHeadersPropagator = null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        });

        var requestOptions = new ForwarderRequestConfig
        {
            // Long, because this path also carries ttyd's WebSocket and log
            // streams — anything shorter cuts idle terminals off mid-session.
            ActivityTimeout = TimeSpan.FromMinutes(30),
        };

        app.Use(async (context, next) =>
        {
            var match = PathRegex.Match(context.Request.Path.Value ?? string.Empty);
            if (!match.Success)
            {
                await next(context);
                return;
            }

            var vmName = match.Groups["name"].Value;
            var port = int.Parse(match.Groups["port"].Value);
            var rest = match.Groups["rest"].Success ? match.Groups["rest"].Value : "/";

            if (port is < 1 or > 65535)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Port out of range.");
                return;
            }

            if (!routes.TryResolve(vmName, out var route))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync(
                    $"VM '{vmName}' is not running. Start it, then retry.");
                return;
            }

            var traceId = Guid.NewGuid().ToString("N")[..12];
            var transformer = new PrefixStrippingTransformer(
                strippedPrefix: $"/vms/{vmName}/p/{port}",
                remainingPath: rest,
                headers: signer.BuildHeaders(route.VmId, traceId));

            var error = await forwarder.SendAsync(
                context, $"http://{route.PrivateIp}:{port}", client, requestOptions, transformer);

            if (error != ForwarderError.None)
            {
                var feature = context.GetForwarderErrorFeature();
                logger.LogWarning(feature?.Exception,
                    "Proxy to {Vm} ({Ip}:{Port}) failed: {Error} trace={Trace}",
                    vmName, route.PrivateIp, port, error, traceId);
            }
        });
    }
}

/// <summary>
/// Rewrites the outbound request so the guest app sees a bare path, and stamps
/// the signed identity headers on it.
/// </summary>
internal sealed class PrefixStrippingTransformer(
    string strippedPrefix,
    string remainingPath,
    IReadOnlyList<(string Name, string Value)> headers) : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(
        HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken ct)
    {
        await base.TransformRequestAsync(context, request, destinationPrefix, ct);

        var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
        request.RequestUri = new Uri($"{destinationPrefix.TrimEnd('/')}{remainingPath}{query}");

        // X-Mvm-* is reserved for the platform. Anything inbound under that
        // prefix is a spoofing attempt or a stale proxy artefact; drop the lot
        // before injecting the real, signed values.
        foreach (var header in request.Headers
                     .Where(h => h.Key.StartsWith(PlatformIdentitySigner.TenantHeaderPrefix,
                         StringComparison.OrdinalIgnoreCase))
                     .Select(h => h.Key)
                     .ToList())
        {
            request.Headers.Remove(header);
        }

        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        // The guest generates absolute URLs from this, and it must carry the
        // proxy's authority — not the VM's private IP, which is unreachable from
        // the user's browser.
        request.Headers.Host = context.Request.Host.Value;
        request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", strippedPrefix);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
    }
}
