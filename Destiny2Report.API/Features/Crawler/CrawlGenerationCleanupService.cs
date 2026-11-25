using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

/// <summary>Deletes only old immutable generations that are no longer referenced by a job.</summary>
public sealed class CrawlGenerationCleanupService(
    ILogger<CrawlGenerationCleanupService> logger,
    IMongoDatabase mongoDatabase) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan BacklogInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(6);
    internal const int PageSize = 500;
    internal const int MaxDeletionsPerPass = 25;

    private static readonly ProjectionDefinition<CrawlReportDocument, ReportCandidate> CandidateProjection =
        Builders<CrawlReportDocument>.Projection
            .Include(item => item.Id)
            .Include(item => item.PlayerKey)
            .Include(item => item.Generation)
            .Include(item => item.CreatedAtUtc);

    private (DateTime CreatedAtUtc, ObjectId Id)? _cursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await CleanupBatchAsync(stoppingToken).ConfigureAwait(false);
                if (result.DeletedCount > 0)
                {
                    logger.LogInformation(
                        "Deleted {DeletedCount} unreferenced crawler generation(s); backlog remaining: {HasMore}.",
                        result.DeletedCount,
                        result.HasMore);
                }

                await Task.Delay(result.HasMore ? BacklogInterval : Interval, stoppingToken).ConfigureAwait(false);
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

    internal static FilterDefinition<CrawlReportDocument> BuildCandidateFilter(
        DateTime cutoffUtc,
        (DateTime CreatedAtUtc, ObjectId Id)? cursor)
    {
        var filters = Builders<CrawlReportDocument>.Filter;
        var eligible = filters.Lt(item => item.CreatedAtUtc, cutoffUtc);
        if (cursor is not { } after)
        {
            return eligible;
        }

        var afterCursor = filters.Gt(item => item.CreatedAtUtc, after.CreatedAtUtc)
            | (filters.Eq(item => item.CreatedAtUtc, after.CreatedAtUtc) & filters.Gt(item => item.Id, after.Id));
        return filters.Gte(item => item.CreatedAtUtc, after.CreatedAtUtc)
            & eligible
            & afterCursor;
    }

    internal async Task<CleanupPassResult> CleanupBatchAsync(
        CancellationToken cancellationToken,
        DateTime? utcNow = null)
    {
        var cutoff = (utcNow ?? DateTime.UtcNow) - MinimumAge;
        var reports = mongoDatabase.GetCollection<CrawlReportDocument>("reports");
        var states = mongoDatabase.GetCollection<CrawlStateDocument>("crawl_state");
        var artifacts = mongoDatabase.GetCollection<CrawlArtifactDocument>("crawl_artifacts");
        var crawlJobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
        var deletedCount = 0;

        while (deletedCount < MaxDeletionsPerPass)
        {
            var page = await reports.Find(BuildCandidateFilter(cutoff, _cursor))
                .SortBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id)
                .Project(CandidateProjection)
                .Limit(PageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
            {
                _cursor = null;
                return new CleanupPassResult(deletedCount, HasMore: false);
            }

            var referencedByPlayer = await LoadReferencedGenerationsAsync(crawlJobs, page, cancellationToken)
                .ConfigureAwait(false);

            foreach (var candidate in page)
            {
                if (referencedByPlayer.TryGetValue(candidate.PlayerKey, out var generations)
                    && (string.Equals(generations.Active, candidate.Generation, StringComparison.Ordinal)
                        || string.Equals(generations.Candidate, candidate.Generation, StringComparison.Ordinal)))
                {
                    _cursor = (candidate.CreatedAtUtc, candidate.Id);
                    continue;
                }

                // Recheck pointers immediately before each delete. Documents are immutable, so a
                // generation can only become visible through one of these authoritative pointers.
                var referenced = await crawlJobs
                    .Find(item => item.PlayerKey == candidate.PlayerKey
                        && (item.ActiveGeneration == candidate.Generation || item.CandidateGeneration == candidate.Generation))
                    .AnyAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (referenced)
                {
                    _cursor = (candidate.CreatedAtUtc, candidate.Id);
                    continue;
                }

                var key = candidate.PlayerKey;
                var generation = candidate.Generation;
                // Delete child documents before the report anchor so an interrupted cleanup
                // remains discoverable on the next pass.
                await artifacts.DeleteManyAsync(item => item.Generation == generation, cancellationToken)
                    .ConfigureAwait(false);
                await states.DeleteManyAsync(item => item.PlayerKey == key && item.Generation == generation, cancellationToken)
                    .ConfigureAwait(false);
                await reports.DeleteOneAsync(item => item.PlayerKey == key && item.Generation == generation, cancellationToken)
                    .ConfigureAwait(false);

                _cursor = (candidate.CreatedAtUtc, candidate.Id);
                deletedCount++;
                if (deletedCount >= MaxDeletionsPerPass)
                {
                    return new CleanupPassResult(deletedCount, HasMore: true);
                }
            }

            if (page.Count < PageSize)
            {
                _cursor = null;
                return new CleanupPassResult(deletedCount, HasMore: false);
            }
        }

        return new CleanupPassResult(deletedCount, HasMore: true);
    }

    private static async Task<Dictionary<byte[], (string? Active, string? Candidate)>> LoadReferencedGenerationsAsync(
        IMongoCollection<CrawlJob> crawlJobs,
        IReadOnlyList<ReportCandidate> page,
        CancellationToken cancellationToken)
    {
        var distinctKeys = page
            .Select(item => item.PlayerKey)
            .Distinct(ByteArrayEqualityComparer.Instance)
            .ToArray();

        var jobs = await crawlJobs
            .Find(Builders<CrawlJob>.Filter.In(item => item.PlayerKey, distinctKeys))
            .Project(item => new JobGenerationPointer(item.PlayerKey, item.ActiveGeneration, item.CandidateGeneration))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var lookup = new Dictionary<byte[], (string? Active, string? Candidate)>(jobs.Count, ByteArrayEqualityComparer.Instance);
        foreach (var job in jobs)
        {
            lookup[job.PlayerKey] = (job.ActiveGeneration, job.CandidateGeneration);
        }

        return lookup;
    }

    internal readonly record struct CleanupPassResult(int DeletedCount, bool HasMore);

    [BsonIgnoreExtraElements]
    private sealed class ReportCandidate
    {
        [BsonId] public ObjectId Id { get; init; }
        [BsonElement("p")] public byte[] PlayerKey { get; init; } = [];
        [BsonElement("g")] public string Generation { get; init; } = "";
        [BsonElement("ca")] public DateTime CreatedAtUtc { get; init; }
    }

    private sealed record JobGenerationPointer(byte[] PlayerKey, string? ActiveGeneration, string? CandidateGeneration);

    private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayEqualityComparer Instance { get; } = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
