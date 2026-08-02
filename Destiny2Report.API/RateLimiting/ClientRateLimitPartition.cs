using System.Net;

namespace Destiny2Report.API.RateLimiting;

public static class ClientRateLimitPartition
{
    public const string UnknownPartitionKey = "unknown";

    public static string GetKey(HttpContext httpContext) =>
        Normalize(httpContext.Connection.RemoteIpAddress) ?? UnknownPartitionKey;

    public static ClientRateLimitDiagnostics GetDiagnostics(HttpContext httpContext)
    {
        var partitionKey = GetKey(httpContext);
        var proxyPeerAddress = httpContext.Request.Headers["X-Original-For"].FirstOrDefault();
        var cloudflareClientAddress = httpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        var source = string.IsNullOrWhiteSpace(proxyPeerAddress)
            ? "connection.remote_ip"
            : string.IsNullOrWhiteSpace(cloudflareClientAddress)
                ? "forwarded-client-ip"
                : "cf-connecting-ip";

        return new ClientRateLimitDiagnostics(
            partitionKey,
            source,
            proxyPeerAddress,
            cloudflareClientAddress);
    }

    private static string? Normalize(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();
    }
}

public sealed record ClientRateLimitDiagnostics(
    string PartitionKey,
    string Source,
    string? ProxyPeerAddress,
    string? CloudflareClientAddress);
