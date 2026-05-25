namespace D2Report.BungieClient.RateLimiting;

public static class BungieClientHandlers
{
    public static HttpMessageHandler CreateRateLimitedHandler(BungieClientRateLimitOptions? options = null)
    {
        return new BungieClientRateLimitingHandler(options)
        {
            InnerHandler = CreateRedirectHandler()
        };
    }

    public static SocketsHttpHandler CreateRedirectHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };
    }
}
