using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Destiny2Report.API.Features.Crawler;

public sealed class CrawlerSherpaHistoryThrottler : IDisposable
{
    private readonly SlidingWindowRateLimiter limiter;

    public CrawlerSherpaHistoryThrottler(IOptions<CrawlerOptions> options)
    {
        var crawler = options.Value;
        RequestsPerSecond = Math.Max(1, crawler.SherpaHistoryRequestsPerSecond);
        var queueLimit = Math.Max(0, crawler.SherpaHistoryRateLimitQueueLimit);

        limiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = RequestsPerSecond,
            Window = TimeSpan.FromSeconds(1),
            SegmentsPerWindow = 10,
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }

    public int RequestsPerSecond { get; }

    public async ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
    {
        var lease = await limiter.AcquireAsync(permitCount: 1, cancellationToken).ConfigureAwait(false);
        if (lease.IsAcquired)
        {
            return lease;
        }

        lease.Dispose();
        throw new InvalidOperationException("The crawler sherpa history rate limit queue rejected the request.");
    }

    public void Dispose()
    {
        limiter.Dispose();
    }
}
