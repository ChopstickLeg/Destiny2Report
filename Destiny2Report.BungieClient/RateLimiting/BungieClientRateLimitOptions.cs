namespace D2Report.BungieClient.RateLimiting;

public sealed class BungieClientRateLimitOptions
{
    public const int DefaultRequestsPerSecond = 20;

    public int DefaultPermitLimit { get; set; } = DefaultRequestsPerSecond;

    public int QueueLimit { get; set; } = 1_000;

    public int SegmentsPerWindow { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    public Func<HttpRequestMessage, string>? EndpointKeySelector { get; set; }

    public IDictionary<string, int> EndpointPermitLimits { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public void SetEndpointLimit(string endpointKey, int requestsPerSecond)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerSecond);

        EndpointPermitLimits[endpointKey] = requestsPerSecond;
    }
}
