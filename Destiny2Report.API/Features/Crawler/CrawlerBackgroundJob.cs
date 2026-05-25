using StackExchange.Redis;

namespace Destiny2Report.API.Features.Crawler;

public class CrawlerBackgroundJob : BackgroundService
{
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
                var entries = await redisDatabase.StreamReadGroupAsync(
                        CrawlerQueue.StreamName,
                        CrawlerQueue.ConsumerGroupName,
                        _consumerName,
                        ">",
                        count: 1)
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

    private async Task ProcessEntryAsync(IDatabase redisDatabase, StreamEntry entry, CancellationToken stoppingToken)
    {
        if (!TryReadCrawlerJob(entry, out var membershipTypeId, out var bungieMembershipId))
        {
            _logger.LogWarning("Acknowledging malformed crawler stream entry {EntryId}.", entry.Id);
            await redisDatabase.StreamAcknowledgeAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, entry.Id)
                .ConfigureAwait(false);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var crawlerService = scope.ServiceProvider.GetRequiredService<ICrawlerService>();

        await crawlerService.CrawlAsync(membershipTypeId, bungieMembershipId, stoppingToken).ConfigureAwait(false);
        await redisDatabase.StreamAcknowledgeAndDeleteAsync(CrawlerQueue.StreamName, CrawlerQueue.ConsumerGroupName, StreamTrimMode.DeleteReferences, entry.Id)
            .ConfigureAwait(false);

        _logger.LogInformation("Completed crawler stream entry {EntryId}.", entry.Id);
    }

    private static bool TryReadCrawlerJob(StreamEntry entry, out int membershipTypeId, out long bungieMembershipId)
    {
        membershipTypeId = 0;
        bungieMembershipId = 0;

        var membershipTypeIdValue = entry.Values.FirstOrDefault(value => value.Name == "membershipTypeId").Value;
        var bungieMembershipIdValue = entry.Values.FirstOrDefault(value => value.Name == "bungieMembershipId").Value;

        return int.TryParse(membershipTypeIdValue.ToString(), out membershipTypeId)
            && long.TryParse(bungieMembershipIdValue.ToString(), out bungieMembershipId)
            && membershipTypeId > 0
            && bungieMembershipId > 0;
    }
}
