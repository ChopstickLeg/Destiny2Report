using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;

namespace D2Report.BungieClient.RateLimiting;

public sealed class BungieClientRateLimitingHandler : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly BungieClientRateLimitOptions _options;

    public BungieClientRateLimitingHandler(BungieClientRateLimitOptions? options = null)
    {
        _options = options ?? new BungieClientRateLimitOptions();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpointKey = GetEndpointKey(request);
        var limiter = _limiters.GetOrAdd(endpointKey, CreateLimiter);

        using var lease = await limiter.AcquireAsync(permitCount: 1, cancellationToken).ConfigureAwait(false);
        if (lease.IsAcquired)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return CreateTooManyRequestsResponse(request, lease);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var limiter in _limiters.Values)
            {
                limiter.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private string GetEndpointKey(HttpRequestMessage request)
    {
        if (_options.EndpointKeySelector is not null)
        {
            return _options.EndpointKeySelector(request);
        }

        return BungieEndpointKey.FromRequest(request);
    }

    private RateLimiter CreateLimiter(string endpointKey)
    {
        var permitLimit = _options.EndpointPermitLimits.TryGetValue(endpointKey, out var endpointLimit)
            ? endpointLimit
            : _options.DefaultPermitLimit;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.SegmentsPerWindow);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Window, TimeSpan.FromMilliseconds(1));

        return new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = _options.Window,
            SegmentsPerWindow = _options.SegmentsPerWindow,
            QueueLimit = _options.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }

    private static HttpResponseMessage CreateTooManyRequestsResponse(HttpRequestMessage request, RateLimitLease lease)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            RequestMessage = request,
            ReasonPhrase = "Bungie client rate limit queue rejected the request"
        };

        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        }

        return response;
    }
}
