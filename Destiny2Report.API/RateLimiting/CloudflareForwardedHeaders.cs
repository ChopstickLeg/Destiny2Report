using Microsoft.AspNetCore.HttpOverrides;

namespace Destiny2Report.API.RateLimiting;

internal static class CloudflareForwardedHeaders
{
    internal const string ClientIpHeaderName = "CF-Connecting-IP";

    internal static void Configure(
        ForwardedHeadersOptions options,
        IEnumerable<string> trustedProxyNetworks)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardedForHeaderName = ClientIpHeaderName;
        options.ForwardLimit = 1;

        foreach (var value in trustedProxyNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
            {
                throw new InvalidOperationException(
                    $"RateLimiting:TrustedProxyNetworks contains invalid CIDR value '{value}'.");
            }

            options.KnownIPNetworks.Add(network);
        }
    }
}
