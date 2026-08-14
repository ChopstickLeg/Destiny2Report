namespace D2Report.BungieClient.RateLimiting;

public static class BungieClientHandlers
{
    public static HttpMessageHandler CreateRateLimitedHandler(BungieClientRateLimitOptions? options = null)
    {
        return new BungieClientRetryHandler
        {
            InnerHandler = new BungieClientRateLimitingHandler(options)
            {
                InnerHandler = CreateRedirectHandler()
            }
        };
    }

    public static SocketsHttpHandler CreateRedirectHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            // Keep an outbound request burst from consuming an unbounded number
            // of sockets when several search/report requests arrive together.
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
    }
}
