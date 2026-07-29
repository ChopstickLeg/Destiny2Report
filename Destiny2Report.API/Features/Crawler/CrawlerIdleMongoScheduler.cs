using Destiny2Report.API.Features.Crawler.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

/// <summary>
/// Bridges the legacy Mongo-only background queue into the durable Rust crawler
/// job protocol. It fills the configured crawler capacity with background players
/// while counting foreground jobs first, preserving foreground crawl priority.
/// </summary>
public sealed class CrawlerIdleMongoScheduler(
    ILogger<CrawlerIdleMongoScheduler> logger,
    IMongoDatabase mongoDatabase,
    ICrawlerJobQueue crawlerJobQueue,
    IOptions<CrawlerOptions> options) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClaimLeaseDuration = TimeSpan.FromMinutes(5);
    private readonly string _owner = $"{Environment.MachineName}-{Environment.ProcessId}-idle-scheduler-{Guid.NewGuid():N}";
    private readonly int _backgroundConcurrency = options.Value.BackgroundConcurrency;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await HasAvailableCapacityAsync(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var report = await TryClaimReportAsync(stoppingToken).ConfigureAwait(false);
                if (report is null)
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await BridgeToCrawlerQueueAsync(report, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Idle Mongo crawler scheduler failed; retrying.");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> HasAvailableCapacityAsync(CancellationToken cancellationToken)
    {
        var activeJobs = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs")
            .CountDocumentsAsync(BuildActiveCrawlerFilter(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return HasAvailableCapacity(activeJobs, _backgroundConcurrency);
    }

    internal static bool HasAvailableCapacity(long activeJobs, int backgroundConcurrency) =>
        activeJobs < backgroundConcurrency;

    private async Task<DestinyReport?> TryClaimReportAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var update = Builders<DestinyReport>.Update
            .Set(report => report.CrawlState, DestinyReport.CrawlStateRunning)
            .Set(report => report.StartedAtUtc, now)
            .Set(report => report.LeaseExpiresAtUtc, now.Add(ClaimLeaseDuration))
            .Set(report => report.LeaseOwner, _owner)
            .Set(report => report.CrawlError, "");

        return await mongoDatabase.GetCollection<DestinyReport>("destiny_reports")
            .FindOneAndUpdateAsync(
                BuildReportClaimFilter(now),
                update,
                new FindOneAndUpdateOptions<DestinyReport>
                {
                    Sort = Builders<DestinyReport>.Sort.Ascending(report => report.QueuedAtUtc),
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task BridgeToCrawlerQueueAsync(
        DestinyReport report,
        CancellationToken cancellationToken)
    {
        try
        {
            var queued = await crawlerJobQueue.EnqueueAsync(
                    report.PlatformId,
                    report.PlayerMembershipId,
                    report.NeedsFullRecrawl,
                    cancellationToken)
                .ConfigureAwait(false);
            var playerKey = CrawlJob.CreatePlayerKey(report.PlatformId, report.PlayerMembershipId);
            var job = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs")
                .Find(item => item.PlayerKey == playerKey && item.RunId == queued.JobId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var filter = ReportOwnershipFilter(report);
            var update = Builders<DestinyReport>.Update
                .Set(item => item.CrawlState, DestinyReport.CrawlStateQueued)
                .Set(item => item.QueuedInRedis, job?.DispatchedToRedis == true)
                .Set(item => item.LeaseExpiresAtUtc, null)
                .Set(item => item.LeaseOwner, "")
                .Set(item => item.CrawlError, "");
            await mongoDatabase.GetCollection<DestinyReport>("destiny_reports")
                .UpdateOneAsync(filter, update, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Bridged idle Mongo crawl for membership {MembershipType}/{MembershipId} to Rust run {RunId}.",
                report.PlatformId,
                report.PlayerMembershipId,
                queued.JobId);
        }
        catch
        {
            await ReleaseClaimAsync(report, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private Task ReleaseClaimAsync(DestinyReport report, CancellationToken cancellationToken)
    {
        var update = Builders<DestinyReport>.Update
            .Set(item => item.CrawlState, DestinyReport.CrawlStateQueued)
            .Set(item => item.QueuedInRedis, false)
            .Set(item => item.LeaseExpiresAtUtc, null)
            .Set(item => item.LeaseOwner, "");
        return mongoDatabase.GetCollection<DestinyReport>("destiny_reports")
            .UpdateOneAsync(
                ReportOwnershipFilter(report),
                update,
                cancellationToken: cancellationToken);
    }

    private FilterDefinition<DestinyReport> ReportOwnershipFilter(DestinyReport report) =>
        Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, report.PlatformId)
        & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, report.PlayerMembershipId)
        & Builders<DestinyReport>.Filter.Eq(item => item.LeaseOwner, _owner);

    internal static FilterDefinition<CrawlJob> BuildActiveCrawlerFilter() =>
        Builders<CrawlJob>.Filter.In(
            item => item.State,
            [CrawlJob.StateQueued, CrawlJob.StateRunning]);

    internal static FilterDefinition<DestinyReport> BuildReportClaimFilter(DateTime now)
    {
        var filters = Builders<DestinyReport>.Filter;
        var mongoOnly = filters.Eq(report => report.QueuedInRedis, false);
        var queued = filters.Eq(report => report.CrawlState, DestinyReport.CrawlStateQueued);
        var expired = filters.Eq(report => report.CrawlState, DestinyReport.CrawlStateRunning)
            & filters.Lt(report => report.LeaseExpiresAtUtc, now);
        return mongoOnly & (queued | expired);
    }
}
