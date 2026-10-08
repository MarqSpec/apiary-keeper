using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace ApiaryCopilot.Api.Proxy;

/// <summary>
/// Trusts the nginx proxy's <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> (D-16), so redirects, links and rate
/// limits see the real client, scheme and host. Only a peer on a private network is trusted, and only its own hop is read.
/// </summary>
public static class ForwardedHeadersExtensions
{
    // The proxy reaches the app over Railway's private network (IPv6, fd12::/16) or compose's bridge network; a peer on
    // the internet is never one of these. Loopback is trusted by the middleware's defaults.
    private static readonly IPNetwork[] PrivateNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("fc00::/7"),
    ];

    /// <summary>Configures forwarded headers to be read from a proxy on a private network, one hop deep.</summary>
    public static IServiceCollection AddProxyForwardedHeaders(this IServiceCollection services) =>
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            foreach (var network in PrivateNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });

    /// <summary>Applies the forwarded headers; call it before any middleware that reads the scheme, host or client address.</summary>
    public static WebApplication UseProxyForwardedHeaders(this WebApplication app)
    {
        app.UseForwardedHeaders();
        return app;
    }
}
