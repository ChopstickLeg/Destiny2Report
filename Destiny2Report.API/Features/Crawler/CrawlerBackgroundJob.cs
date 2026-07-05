using Destiny2Report.API.Observability;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;

namespace Destiny2Report.API.Features.Crawler;

public class CrawlerBackgroundJob : BackgroundService
{
    private const int ReadBatchSize = 1;
    private static readonly TimeSpan PendingMessageIdleTimeout = TimeSpan.FromMinutes(1);

    private readonly ILogger<CrawlerBackgroundJob> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly string _consumerName = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    public CrawlerBackgroundJob(
        ILogger<CrawlerBackgroundJob> logger,
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _redis = redis;
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

        await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, "running", null)
            .ConfigureAwait(false);

        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await crawlerService.CrawlAsync(membershipTypeId, membershipId, stoppingToken).ConfigureAwait(false);
                await redisDatabase.StreamAcknowledgeAndDeleteAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, StreamTrimMode.DeleteReferences, entry.Id)
                    .ConfigureAwait(false);
                await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, "completed", null)
                    .ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogInformation("Completed crawler stream entry {EntryId}.", entry.Id);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "Crawler stream entry {EntryId} failed on attempt {Attempt}; retrying once immediately.",
                    entry.Id,
                    attempt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await UpdateJobStatusAsync(redisDatabase, membershipTypeId, membershipId, entry.Id, "failed", ex.Message)
                    .ConfigureAwait(false);
                await redisDatabase.StreamAcknowledgeAndDeleteAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, StreamTrimMode.DeleteReferences, entry.Id)
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

    private static async Task UpdateJobStatusAsync(
        IDatabase redisDatabase,
        int membershipTypeId,
        long membershipId,
        RedisValue streamEntryId,
        string status,
        string? error)
    {
        var updatedAtUtc = DateTimeOffset.UtcNow;
        var statusKey = CrawlerQueue.JobStatusKey(membershipTypeId, membershipId);

        await redisDatabase.HashSetAsync(
                statusKey,
                [
                    new HashEntry("membershipTypeId", membershipTypeId),
                    new HashEntry("membershipId", membershipId),
                    new HashEntry("streamEntryId", streamEntryId),
                    new HashEntry("status", status),
                    new HashEntry("updatedAtUtc", updatedAtUtc.ToString("O")),
                    new HashEntry("error", error ?? "")
                ])
            .ConfigureAwait(false);

        if (status is "completed" or "failed")
        {
            await redisDatabase.KeyExpireAsync(statusKey, TimeSpan.FromHours(6)).ConfigureAwait(false);
        }

        var jobEvent = new
        {
            MembershipTypeId = membershipTypeId,
            MembershipId = membershipId,
            Status = status,
            StreamEntryId = streamEntryId.ToString(),
            Error = error,
            UpdatedAtUtc = updatedAtUtc
        };

        await redisDatabase.PublishAsync(RedisChannel.Literal(CrawlerQueue.EventsChannelName), JsonSerializer.Serialize(jobEvent))
            .ConfigureAwait(false);
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
}
