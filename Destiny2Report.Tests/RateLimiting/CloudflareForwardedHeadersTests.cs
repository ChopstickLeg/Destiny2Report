using Destiny2Report.API.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace Destiny2Report.Tests.RateLimiting;

public sealed class CloudflareForwardedHeadersTests
{
    [Fact]
    public async Task Trusted_proxy_replaces_remote_ip_with_cloudflare_client_ip()
    {
        var middleware = CreateMiddleware("10.0.0.0/8");
        var context = CreateContext("10.0.1.16", "66.97.54.162");

        await middleware.Invoke(context);

        Assert.Equal(IPAddress.Parse("66.97.54.162"), context.Connection.RemoteIpAddress);
        Assert.Equal("10.0.1.16:0", context.Request.Headers["X-Original-For"]);
    }

    [Fact]
    public async Task Trusted_ipv4_network_also_matches_ipv4_mapped_proxy()
    {
        var middleware = CreateMiddleware("10.0.0.0/8");
        var context = CreateContext("::ffff:10.0.1.16", "66.97.54.162");

        await middleware.Invoke(context);

        Assert.Equal(IPAddress.Parse("66.97.54.162"), context.Connection.RemoteIpAddress);
        Assert.Equal("[::ffff:10.0.1.16]:0", context.Request.Headers["X-Original-For"]);
    }

    [Fact]
    public async Task Untrusted_peer_cannot_spoof_cloudflare_client_ip()
    {
        var middleware = CreateMiddleware("10.0.0.0/8");
        var context = CreateContext("203.0.113.10", "66.97.54.162");

        await middleware.Invoke(context);

        Assert.Equal(IPAddress.Parse("203.0.113.10"), context.Connection.RemoteIpAddress);
        Assert.False(context.Request.Headers.ContainsKey("X-Original-For"));
    }

    [Fact]
    public void Invalid_trusted_proxy_network_fails_configuration()
    {
        var options = new ForwardedHeadersOptions();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CloudflareForwardedHeaders.Configure(options, ["not-a-network"]));

        Assert.Contains("not-a-network", exception.Message);
    }

    private static ForwardedHeadersMiddleware CreateMiddleware(params string[] trustedProxyNetworks)
    {
        var options = new ForwardedHeadersOptions();
        CloudflareForwardedHeaders.Configure(options, trustedProxyNetworks);
        return new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));
    }

    private static DefaultHttpContext CreateContext(string remoteIp, string cloudflareClientIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Headers[CloudflareForwardedHeaders.ClientIpHeaderName] = cloudflareClientIp;
        return context;
    }
}
