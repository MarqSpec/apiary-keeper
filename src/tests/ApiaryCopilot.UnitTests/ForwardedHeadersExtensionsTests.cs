using System.Net;
using ApiaryCopilot.Api.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace ApiaryCopilot.UnitTests;

// The app sits behind the nginx proxy on a private network (D-16): it takes the client's address, scheme and host from
// the proxy's X-Forwarded-* headers, and from nobody else.
public sealed class ForwardedHeadersExtensionsTests
{
    private const string ClientAddress = "203.0.113.7";
    private const string PublicHost = "apiary-keeper.example.test";

    [Theory]
    [InlineData("10.4.0.2")]
    [InlineData("172.18.0.5")]       // compose's bridge network
    [InlineData("172.31.255.254")]   // the top of 172.16.0.0/12
    [InlineData("192.168.1.20")]
    [InlineData("fd12:3456::8")]     // Railway's IPv6 private network
    [InlineData("::ffff:172.18.0.5")] // an IPv4 peer on a dual-mode socket
    [InlineData("127.0.0.1")]
    public async Task UseProxyForwardedHeaders_ShouldTakeClientAddressSchemeAndHost_WhenPeerIsOnPrivateNetwork(string peer)
    {
        await using var app = await StartAsync();

        var context = await SendThroughAsync(app, peer, ClientAddress);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(ClientAddress));
        context.Request.Scheme.Should().Be("https");
        context.Request.Host.Value.Should().Be(PublicHost);
    }

    // A peer outside the private ranges could only be a caller on the internet, which must not choose its own address.
    [Theory]
    [InlineData("198.51.100.9")]
    [InlineData("172.32.0.1")]       // just above 172.16.0.0/12
    [InlineData("11.0.0.1")]         // just above 10.0.0.0/8
    [InlineData("2001:db8::1")]
    public async Task UseProxyForwardedHeaders_ShouldIgnoreForwardedHeaders_WhenPeerIsPublic(string peer)
    {
        await using var app = await StartAsync();

        var context = await SendThroughAsync(app, peer, ClientAddress);

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(peer));
        context.Request.Scheme.Should().Be("http");
        context.Request.Host.Value.Should().Be("app.internal");
    }

    // Only the proxy's own hop is read, even when that hop is itself a private address: an address further back in
    // X-Forwarded-For was written by someone other than the proxy, and is not taken as the client.
    [Fact]
    public async Task UseProxyForwardedHeaders_ShouldTakeOnlyTheLastHop_WhenForwardedForHasSeveralAddresses()
    {
        await using var app = await StartAsync();

        var context = await SendThroughAsync(app, "172.18.0.5", "198.51.100.66, 10.0.0.9");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("10.0.0.9"));
    }

    private static Task<HttpContext> SendThroughAsync(WebApplication app, string peer, string forwardedFor) =>
        app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("app.internal");
            context.Request.Path = "/";
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-Host"] = PublicHost;
        });

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddProxyForwardedHeaders();
        var app = builder.Build();

        app.UseProxyForwardedHeaders();
        app.Run(_ => Task.CompletedTask);

        await app.StartAsync();
        return app;
    }
}
