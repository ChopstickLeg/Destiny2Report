using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

/// <summary>Deletes only old immutable generations that are no longer referenced by a job.</summary>
public sealed class CrawlGenerationCleanupService(
    ILogger<CrawlGenerationCleanupService> logger,
    IMongoDatabase mongoDatabase) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupBatchAsync(stoppingToken).ConfigureAwait(false);
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Compact crawler generation cleanup failed.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task CleanupBatchAsync(CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<CrawlReportDocument>("reports");
        var candidates = await reports.Find(item => item.CreatedAtUtc < DateTime.UtcNow - MinimumAge)
            .SortBy(item => item.CreatedAtUtc)
            .Limit(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            // Recheck pointers immediately before each delete. Documents are immutable, so a
            // generation can only become visible through one of these authoritative pointers.
            var referenced = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs")
                .Find(item => item.PlayerKey == candidate.PlayerKey
                    && (item.ActiveGeneration == candidate.Generation || item.CandidateGeneration == candidate.Generation))
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (referenced) continue;

            var key = candidate.PlayerKey;
            var generation = candidate.Generation;
            await reports.DeleteOneAsync(item => item.PlayerKey == key && item.Generation == generation, cancellationToken).ConfigureAwait(false);
            await mongoDatabase.GetCollection<CrawlStateDocument>("crawl_state")
                .DeleteManyAsync(item => item.PlayerKey == key && item.Generation == generation, cancellationToken).ConfigureAwait(false);
            await mongoDatabase.GetCollection<CrawlArtifactDocument>("crawl_artifacts")
                .DeleteManyAsync(item => item.PlayerKey == key && item.Generation == generation, cancellationToken).ConfigureAwait(false);
        }
    }
}
