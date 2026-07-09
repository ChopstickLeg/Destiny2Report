using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;

namespace Destiny2Report.API.Features.Crawler;

public class CrawlerBackgroundJob : BackgroundService
{
    private const int ReadBatchSize = 1;
    private static readonly TimeSpan PendingMessageIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BackgroundJobLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackgroundJobLeaseRenewalInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<CrawlerBackgroundJob> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly IMongoDatabase _mongoDatabase;
    private readonly string _consumerName = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    public CrawlerBackgroundJob(
        ILogger<CrawlerBackgroundJob> logger,
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        IMongoDatabase mongoDatabase)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _redis = redis;
        _mongoDatabase = mongoDatabase;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Crawler background job is starting.");

        var redisDatabase = _redis.GetDatabase();
        await EnsureConsumerGroupAsync(redisDatabase).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recoveredEntries = await ClaimStalePendingEntriesAsync(redisDatabase, stoppingToken).ConfigureAwait(false);
                if (recoveredEntries.Length > 0)
                {
                    foreach (var entry in recoveredEntries)
                    {
                        await ProcessEntryAsync(redisDatabase, entry, stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var entries = await redisDatabase.StreamReadGroupAsync(
                        CrawlerQueue.StreamName,
                        CrawlerQueue.ConsumerGroupName,
                        _consumerName,
                        ">",
                        count: ReadBatchSize)
                    .WaitAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (entries.Length == 0)
                {
                    var backgroundJob = await ClaimBackgroundJobAsync(stoppingToken).ConfigureAwait(false);
                    if (backgroundJob is not null)
                    {
                        await ProcessBackgroundJobAsync(backgroundJob, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var entry in entries)
                {
                    await ProcessEntryAsync(redisDatabase, entry, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while running the crawler.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Crawler background job is stopping.");
    }

    private async Task EnsureConsumerGroupAsync(IDatabase redisDatabase)
    {
        try
        {
            await redisDatabase.StreamCreateConsumerGroupAsync(
                    CrawlerQueue.StreamName,
                    CrawlerQueue.ConsumerGroupName,
                    "0-0",
                    createStream: true)
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Redis stream consumer group {ConsumerGroupName} already exists.", CrawlerQueue.ConsumerGroupName);
        }
    }

    private async Task<StreamEntry[]> ClaimStalePendingEntriesAsync(IDatabase redisDatabase, CancellationToken stoppingToken)
    {
        var result = await redisDatabase.StreamAutoClaimAsync(
                CrawlerQueue.StreamName,
                CrawlerQueue.ConsumerGroupName,
                _consumerName,
                (long)PendingMessageIdleTimeout.TotalMilliseconds,
                "0-0",
                ReadBatchSize)
            .WaitAsync(stoppingToken)
            .ConfigureAwait(false);

        if (result.ClaimedEntries.Length > 0)
        {
            _logger.LogInformation(
                "Claimed {EntryCount} stale pending crawler stream entries.",
                result.ClaimedEntries.Length);
        }

        return result.ClaimedEntries;
    }

    private async Task ProcessEntryAsync(IDatabase redisDatabase, StreamEntry entry, CancellationToken stoppingToken)
    {
        if (!TryReadCrawlerJob(entry, out var membershipTypeId, out var membershipId))
        {
            _logger.LogWarning("Acknowledging malformed crawler stream entry {EntryId}.", entry.Id);
            await redisDatabase.StreamAcknowledgeAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, entry.Id)
                .ConfigureAwait(false);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var crawlerService = scope.ServiceProvider.GetRequiredService<ICrawlerService>();
        using var activity = AppTelemetry.ActivitySource.StartActivity("crawler.player.process", ActivityKind.Consumer);

        activity?.SetTag("destiny.membership_type_id", membershipTypeId);
        activity?.SetTag("destiny.membership_id", membershipId);
        activity?.SetTag("messaging.system", "redis");
        activity?.SetTag("messaging.destination.name", CrawlerQueue.StreamName);
        activity?.SetTag("messaging.message.id", entry.Id.ToString());

        await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, DestinyReport.CrawlStateRunning, null)
            .ConfigureAwait(false);
        await UpdateReportCrawlStateAsync(membershipTypeId, membershipId, DestinyReport.CrawlStateRunning, queuedInRedis: true, null, stoppingToken)
            .ConfigureAwait(false);

        var progress = new RedisCrawlProgressReporter(redisDatabase, membershipTypeId, membershipId, entry.Id, TimeSpan.FromSeconds(1));

        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await crawlerService.CrawlAsync(membershipTypeId, membershipId, progress, stoppingToken).ConfigureAwait(false);
                var finalReportState = await GetReportCrawlStateAsync(membershipTypeId, membershipId, stoppingToken).ConfigureAwait(false);
                var finalStatus = finalReportState?.CrawlState ?? DestinyReport.CrawlStateCompleted;
                await redisDatabase.StreamAcknowledgeAndDeleteAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, StreamTrimMode.DeleteReferences, entry.Id)
                    .ConfigureAwait(false);
                await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, finalStatus, finalReportState?.CrawlError, progress.Snapshot)
                    .ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogInformation("Completed crawler stream entry {EntryId} with status {CrawlState}.", entry.Id, finalStatus);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Crawler stream entry {EntryId} failed on attempt {Attempt}; retrying once immediately.",
                    entry.Id,
                    attempt);
            }
            catch (Exception ex)
            {
                await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, DestinyReport.CrawlStateFailed, ex.Message, progress.Snapshot)
                    .ConfigureAwait(false);
                await redisDatabase.StreamAcknowledgeAndDeleteAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, StreamTrimMode.DeleteReferences, entry.Id)
                    .ConfigureAwait(false);
                await UpdateReportCrawlStateAsync(membershipTypeId, membershipId, DestinyReport.CrawlStateFailed, queuedInRedis: false, ex.Message, stoppingToken)
                    .ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                _logger.LogError(
                    ex,
                    "Crawler stream entry {EntryId} failed after {AttemptCount} attempts.",
                    entry.Id,
                    maxAttempts);
                return;
            }
        }

    }

    private async Task<DestinyReport?> ClaimBackgroundJobAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        var reports = _mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var queuedFilter = Builders<DestinyReport>.Filter.Eq(report => report.CrawlState, DestinyReport.CrawlStateQueued)
            & Builders<DestinyReport>.Filter.Eq(report => report.QueuedInRedis, false);
        var expiredLeaseFilter = Builders<DestinyReport>.Filter.Eq(report => report.CrawlState, DestinyReport.CrawlStateRunning)
            & Builders<DestinyReport>.Filter.Eq(report => report.QueuedInRedis, false)
            & Builders<DestinyReport>.Filter.Lt(report => report.LeaseExpiresAtUtc, now);
        var filter = queuedFilter | expiredLeaseFilter;
        var update = Builders<DestinyReport>.Update
            .Set(report => report.CrawlState, DestinyReport.CrawlStateRunning)
            .Set(report => report.StartedAtUtc, now)
            .Set(report => report.LeaseExpiresAtUtc, now.Add(BackgroundJobLeaseDuration))
            .Set(report => report.LeaseOwner, _consumerName)
            .Set(report => report.CrawlError, "");
        var options = new FindOneAndUpdateOptions<DestinyReport>
        {
            Sort = Builders<DestinyReport>.Sort.Ascending(report => report.QueuedAtUtc),
            ReturnDocument = ReturnDocument.After
        };

        return await reports.FindOneAndUpdateAsync(filter, update, options, stoppingToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessBackgroundJobAsync(DestinyReport job, CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var crawlerService = scope.ServiceProvider.GetRequiredService<ICrawlerService>();
        var redisDatabase = _redis.GetDatabase();
        var progress = new RedisCrawlProgressReporter(redisDatabase, job.PlatformId, job.PlayerMembershipId, RedisValue.Null, TimeSpan.FromSeconds(1));
        using var activity = AppTelemetry.ActivitySource.StartActivity("crawler.player.background_process", ActivityKind.Consumer);
        using var leaseRenewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseRenewalTask = RenewBackgroundJobLeaseAsync(job, leaseRenewalCancellation.Token);

        activity?.SetTag("destiny.membership_type_id", job.PlatformId);
        activity?.SetTag("destiny.membership_id", job.PlayerMembershipId);
        activity?.SetTag("messaging.system", "mongodb");
        activity?.SetTag("messaging.destination.name", "destiny_reports");

        try
        {
            await UpdateJobStatusAsync(redisDatabase, job.PlatformId, job.PlayerMembershipId, RedisValue.Null, DestinyReport.CrawlStateRunning, null)
                .ConfigureAwait(false);

            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await crawlerService.CrawlAsync(job.PlatformId, job.PlayerMembershipId, progress, stoppingToken).ConfigureAwait(false);
                    var finalReportState = await GetReportCrawlStateAsync(job.PlatformId, job.PlayerMembershipId, stoppingToken).ConfigureAwait(false);
                    var finalStatus = finalReportState?.CrawlState ?? DestinyReport.CrawlStateCompleted;
                    await UpdateJobStatusAsync(redisDatabase, job.PlatformId, job.PlayerMembershipId, RedisValue.Null, finalStatus, finalReportState?.CrawlError, progress.Snapshot)
                        .ConfigureAwait(false);
                    activity?.SetStatus(ActivityStatusCode.Ok);

                    _logger.LogInformation(
                        "Completed background crawler job for membership {MembershipType}/{MembershipId} with status {CrawlState}.",
                        job.PlatformId,
                        job.PlayerMembershipId,
                        finalStatus);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "Background crawler job for membership {MembershipType}/{MembershipId} failed on attempt {Attempt}; retrying once immediately.",
                        job.PlatformId,
                        job.PlayerMembershipId,
                        attempt);
                }
                catch (Exception ex)
                {
                    await UpdateJobStatusAsync(redisDatabase, job.PlatformId, job.PlayerMembershipId, RedisValue.Null, DestinyReport.CrawlStateFailed, ex.Message, progress.Snapshot)
                        .ConfigureAwait(false);
                    await UpdateReportCrawlStateAsync(job.PlatformId, job.PlayerMembershipId, DestinyReport.CrawlStateFailed, queuedInRedis: false, ex.Message, stoppingToken)
                        .ConfigureAwait(false);
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                    _logger.LogError(
                        ex,
                        "Background crawler job for membership {MembershipType}/{MembershipId} failed after {AttemptCount} attempts.",
                        job.PlatformId,
                        job.PlayerMembershipId,
                        maxAttempts);
                    return;
                }
            }
        }
        finally
        {
            leaseRenewalCancellation.Cancel();
            try
            {
                await leaseRenewalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (leaseRenewalCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RenewBackgroundJobLeaseAsync(DestinyReport job, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(BackgroundJobLeaseRenewalInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var renewed = await TryRenewBackgroundJobLeaseAsync(job.PlatformId, job.PlayerMembershipId, cancellationToken)
                    .ConfigureAwait(false);
                if (!renewed)
                {
                    _logger.LogWarning(
                        "Could not renew background crawler lease for membership {MembershipType}/{MembershipId}; another worker may reclaim it after expiry.",
                        job.PlatformId,
                        job.PlayerMembershipId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not renew background crawler lease for membership {MembershipType}/{MembershipId}; retrying on the next interval.",
                    job.PlatformId,
                    job.PlayerMembershipId);
            }
        }
    }

    private async Task<bool> TryRenewBackgroundJobLeaseAsync(
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var reports = _mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(report => report.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(report => report.PlayerMembershipId, membershipId)
            & Builders<DestinyReport>.Filter.Eq(report => report.CrawlState, DestinyReport.CrawlStateRunning)
            & Builders<DestinyReport>.Filter.Eq(report => report.QueuedInRedis, false)
            & Builders<DestinyReport>.Filter.Eq(report => report.LeaseOwner, _consumerName);
        var update = Builders<DestinyReport>.Update.Set(report => report.LeaseExpiresAtUtc, now.Add(BackgroundJobLeaseDuration));
        var result = await reports.UpdateOneAsync(filter, update, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ModifiedCount > 0;
    }

    private async Task UpdateReportCrawlStateAsync(
        int membershipTypeId,
        long membershipId,
        string crawlState,
        bool queuedInRedis,
        string? error,
        CancellationToken cancellationToken)
    {
        var reports = _mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(report => report.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(report => report.PlayerMembershipId, membershipId);
        var update = Builders<DestinyReport>.Update
            .Set(report => report.CrawlState, crawlState)
            .Set(report => report.QueuedInRedis, queuedInRedis)
            .Set(report => report.CrawlError, error ?? "");

        if (crawlState == DestinyReport.CrawlStateRunning)
        {
            update = update.Set(report => report.StartedAtUtc, DateTimeOffset.UtcNow);
        }
        else
        {
            update = update
                .Set(report => report.LeaseExpiresAtUtc, null)
                .Set(report => report.LeaseOwner, "");
        }

        await reports.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReportCrawlState?> GetReportCrawlStateAsync(
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var reports = _mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(report => report.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(report => report.PlayerMembershipId, membershipId);

        var report = await reports
            .Find(filter)
            .Project(report => new ReportCrawlState(report.CrawlState, report.CrawlError))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (report is null || string.IsNullOrWhiteSpace(report.CrawlState))
        {
            return null;
        }

        return report;
    }

    private static async Task UpdateJobStatusAsync(
        IDatabase redisDatabase,
        int membershipTypeId,
        long membershipId,
        RedisValue streamEntryId,
        string status,
        string? error,
        CrawlProgressSnapshot? progress = null)
    {
        var updatedAtUtc = DateTimeOffset.UtcNow;
        var statusKey = CrawlerQueue.JobStatusKey(membershipTypeId, membershipId);

        var entries = new List<HashEntry>
        {
            new("membershipTypeId", membershipTypeId),
            new("membershipId", membershipId),
            new("streamEntryId", streamEntryId.ToString()),
            new("status", status),
            new("updatedAtUtc", updatedAtUtc.ToString("O")),
            new("error", error ?? "")
        };

        if (progress is not null)
        {
            entries.Add(new HashEntry("progressPhase", progress.Phase));
            entries.Add(new HashEntry("progressLabel", progress.Label));
            entries.Add(new HashEntry("progressCurrent", progress.Current?.ToString() ?? ""));
            entries.Add(new HashEntry("progressTotal", progress.Total?.ToString() ?? ""));
            entries.Add(new HashEntry("progressStartedAtUtc", progress.StartedAtUtc.ToString("O")));
            entries.Add(new HashEntry("progressUpdatedAtUtc", progress.UpdatedAtUtc.ToString("O")));
        }

        await redisDatabase.HashSetAsync(statusKey, entries.ToArray()).ConfigureAwait(false);

        if (progress is null)
        {
            await redisDatabase.HashDeleteAsync(
                    statusKey,
                    [
                        "progressPhase",
                        "progressLabel",
                        "progressCurrent",
                        "progressTotal",
                        "progressStartedAtUtc",
                        "progressUpdatedAtUtc"
                    ])
                .ConfigureAwait(false);
        }

        var statusTtl = IsTerminalCrawlState(status)
            ? CrawlerQueue.TerminalJobStatusTtl
            : CrawlerQueue.ActiveJobStatusTtl;
        await redisDatabase.KeyExpireAsync(statusKey, statusTtl).ConfigureAwait(false);

        var jobEvent = new
        {
            MembershipTypeId = membershipTypeId,
            MembershipId = membershipId,
            Status = status,
            StreamEntryId = streamEntryId.ToString(),
            Error = error,
            UpdatedAtUtc = updatedAtUtc,
            Progress = progress
        };

        await redisDatabase.PublishAsync(RedisChannel.Literal(CrawlerQueue.EventsChannelName), JsonSerializer.Serialize(jobEvent))
            .ConfigureAwait(false);
    }

    private static bool IsTerminalCrawlState(string status)
    {
        return status is DestinyReport.CrawlStateCompleted
            or DestinyReport.CrawlStateFailed
            or DestinyReport.CrawlStatePrivate;
    }

    private sealed class RedisCrawlProgressReporter(
        IDatabase redisDatabase,
        int membershipTypeId,
        long membershipId,
        RedisValue streamEntryId,
        TimeSpan minimumPublishInterval) : ICrawlProgress
    {
        private readonly object _gate = new();
        private CrawlProgressSnapshot? _snapshot;
        private DateTimeOffset _lastPublishedAtUtc = DateTimeOffset.MinValue;

        public CrawlProgressSnapshot? Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _snapshot;
                }
            }
        }

        public ValueTask StartPhaseAsync(string phase, string label, long? total = null, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new CrawlProgressSnapshot(phase, label, 0, total, now, now);
            lock (_gate)
            {
                _snapshot = snapshot;
                _lastPublishedAtUtc = now;
            }

            return new ValueTask(PublishAsync(snapshot));
        }

        public ValueTask ReportAsync(long current, long? total = null, CancellationToken cancellationToken = default)
        {
            CrawlProgressSnapshot? snapshot;
            var shouldPublish = false;
            var now = DateTimeOffset.UtcNow;

            lock (_gate)
            {
                if (_snapshot is null)
                {
                    return ValueTask.CompletedTask;
                }

                snapshot = _snapshot with
                {
                    Current = current,
                    Total = total ?? _snapshot.Total,
                    UpdatedAtUtc = now
                };
                _snapshot = snapshot;

                if (now - _lastPublishedAtUtc >= minimumPublishInterval)
                {
                    _lastPublishedAtUtc = now;
                    shouldPublish = true;
                }
            }

            return shouldPublish ? new ValueTask(PublishAsync(snapshot)) : ValueTask.CompletedTask;
        }

        public ValueTask CompletePhaseAsync(long? current = null, long? total = null, CancellationToken cancellationToken = default)
        {
            CrawlProgressSnapshot? snapshot;
            var now = DateTimeOffset.UtcNow;

            lock (_gate)
            {
                if (_snapshot is null)
                {
                    return ValueTask.CompletedTask;
                }

                snapshot = _snapshot with
                {
                    Current = current ?? _snapshot.Current,
                    Total = total ?? _snapshot.Total,
                    UpdatedAtUtc = now
                };
                _snapshot = snapshot;
                _lastPublishedAtUtc = now;
            }

            return new ValueTask(PublishAsync(snapshot));
        }

        private async Task PublishAsync(CrawlProgressSnapshot snapshot)
        {
            var updatedAtUtc = DateTimeOffset.UtcNow;
            var statusKey = CrawlerQueue.JobStatusKey(membershipTypeId, membershipId);
            await redisDatabase.HashSetAsync(
                    statusKey,
                    [
                        new HashEntry("membershipTypeId", membershipTypeId),
                        new HashEntry("membershipId", membershipId),
                        new HashEntry("streamEntryId", streamEntryId.ToString()),
                        new HashEntry("status", DestinyReport.CrawlStateRunning),
                        new HashEntry("updatedAtUtc", updatedAtUtc.ToString("O")),
                        new HashEntry("error", ""),
                        new HashEntry("progressPhase", snapshot.Phase),
                        new HashEntry("progressLabel", snapshot.Label),
                        new HashEntry("progressCurrent", snapshot.Current?.ToString() ?? ""),
                        new HashEntry("progressTotal", snapshot.Total?.ToString() ?? ""),
                        new HashEntry("progressStartedAtUtc", snapshot.StartedAtUtc.ToString("O")),
                        new HashEntry("progressUpdatedAtUtc", snapshot.UpdatedAtUtc.ToString("O"))
                    ])
                .ConfigureAwait(false);
            await redisDatabase.KeyExpireAsync(statusKey, CrawlerQueue.ActiveJobStatusTtl).ConfigureAwait(false);

            var jobEvent = new
            {
                MembershipTypeId = membershipTypeId,
                MembershipId = membershipId,
                Status = DestinyReport.CrawlStateRunning,
                StreamEntryId = streamEntryId.ToString(),
                Error = (string?)null,
                UpdatedAtUtc = updatedAtUtc,
                Progress = snapshot
            };

            await redisDatabase.PublishAsync(RedisChannel.Literal(CrawlerQueue.EventsChannelName), JsonSerializer.Serialize(jobEvent))
                .ConfigureAwait(false);
        }
    }

    private static bool TryReadCrawlerJob(StreamEntry entry, out int membershipTypeId, out long membershipId)
    {
        membershipTypeId = 0;
        membershipId = 0;

        var membershipTypeIdValue = entry.Values.FirstOrDefault(value => value.Name == "membershipTypeId").Value;
        var membershipIdValue = entry.Values.FirstOrDefault(value => value.Name == "membershipId").Value;

        return int.TryParse(membershipTypeIdValue.ToString(), out membershipTypeId)
            && long.TryParse(membershipIdValue.ToString(), out membershipId)
            && membershipTypeId > 0
            && membershipId > 0;
    }

    private sealed record ReportCrawlState(string CrawlState, string CrawlError);
}
