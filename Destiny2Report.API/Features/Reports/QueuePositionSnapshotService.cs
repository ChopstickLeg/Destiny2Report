using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Reports;

public interface IQueuePositionSnapshotService
{
    Task<QueuePositionSnapshot?> GetPositionAsync(
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Shares one short-lived queue snapshot between every SSE client. Queue position is
/// approximate by nature, so it must not require MongoDB count queries per client.
/// </summary>
public sealed class QueuePositionSnapshotService(IMongoDatabase mongoDatabase) : IQueuePositionSnapshotService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private QueueJobPosition[] _jobs = [];
    private DateTimeOffset _expiresAtUtc = DateTimeOffset.MinValue;

    public async Task<QueuePositionSnapshot?> GetPositionAsync(
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var jobs = await GetJobsAsync(cancellationToken).ConfigureAwait(false);
        var playerKey = CrawlJob.CreatePlayerKey(membershipTypeId, membershipId);
        var job = jobs.FirstOrDefault(candidate => candidate.PlayerKey.AsSpan().SequenceEqual(playerKey));
        return job is null ? null : CalculatePosition(jobs, job);
    }

    internal static QueuePositionSnapshot CalculatePosition(
        IReadOnlyCollection<QueueJobPosition> allJobs,
        QueueJobPosition job)
    {
        var cohort = allJobs.Where(candidate => candidate.DispatchedToRedis == job.DispatchedToRedis).ToArray();
        var jobsAhead = cohort.Count(candidate => IsAheadOf(candidate, job));
        return new QueuePositionSnapshot(Math.Min(cohort.Length, 1 + jobsAhead), cohort.Length);
    }

    private async Task<QueueJobPosition[]> GetJobsAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _expiresAtUtc)
        {
            return _jobs;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _expiresAtUtc)
            {
                return _jobs;
            }

            var freshJobs = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs")
                .Find(job => job.State == CrawlJob.StateQueued)
                .Project(job => new QueueJobPosition(
                    job.PlayerKey,
                    job.QueuedAtUtc,
                    job.DispatchedToRedis,
                    job.IsPriority))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _jobs = [.. freshJobs];
            _expiresAtUtc = DateTimeOffset.UtcNow.Add(RefreshInterval);
            return _jobs;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static bool IsAheadOf(QueueJobPosition candidate, QueueJobPosition job)
    {
        if (job.IsPriority)
        {
            return candidate.IsPriority && IsEarlier(candidate, job);
        }

        return candidate.IsPriority || (!candidate.IsPriority && IsEarlier(candidate, job));
    }

    private static bool IsEarlier(QueueJobPosition left, QueueJobPosition right) =>
        left.QueuedAtUtc < right.QueuedAtUtc
        || (left.QueuedAtUtc == right.QueuedAtUtc
            && left.PlayerKey.AsSpan().SequenceCompareTo(right.PlayerKey) < 0);
}

public sealed record QueuePositionSnapshot(long Position, long QueueLength);

internal sealed record QueueJobPosition(
    byte[] PlayerKey,
    DateTime QueuedAtUtc,
    bool DispatchedToRedis,
    bool IsPriority);
