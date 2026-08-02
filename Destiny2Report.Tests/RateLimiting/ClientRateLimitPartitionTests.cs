using System.Net;
using Destiny2Report.API.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Destiny2Report.Tests.RateLimiting;

public sealed class ClientRateLimitPartitionTests
{
    [Fact]
    public void GetKey_NormalizesIpv4MappedAddress()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.42");

        Assert.Equal("203.0.113.42", ClientRateLimitPartition.GetKey(context));
    }

    [Fact]
    public void GetDiagnostics_ReportsForwardedAddressAndProxyPeer()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        context.Request.Headers["X-Original-For"] = "172.18.0.1:54321";
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.42";

        var diagnostics = ClientRateLimitPartition.GetDiagnostics(context);

        Assert.Equal("203.0.113.42", diagnostics.PartitionKey);
        Assert.Equal("cf-connecting-ip", diagnostics.Source);
        Assert.Equal("172.18.0.1:54321", diagnostics.ProxyPeerAddress);
        Assert.Equal("203.0.113.42", diagnostics.CloudflareClientAddress);
    }

    [Fact]
    public void GetDiagnostics_FallsBackToConnectionAddress()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        var diagnostics = ClientRateLimitPartition.GetDiagnostics(context);

        Assert.Equal("127.0.0.1", diagnostics.PartitionKey);
        Assert.Equal("connection.remote_ip", diagnostics.Source);
        Assert.Null(diagnostics.ProxyPeerAddress);
    }

    [Fact]
    public async Task ForwardedHeaders_TrustsIpv4MappedDockerNetwork()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:172.18.0.1");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.42";
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = 1
        };
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("::ffff:172.16.0.0/108"));
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));

        await middleware.Invoke(context);

        Assert.Equal("203.0.113.42", context.Connection.RemoteIpAddress?.ToString());
        Assert.Equal("[::ffff:172.18.0.1]:0", context.Request.Headers["X-Original-For"]);
    }
}
