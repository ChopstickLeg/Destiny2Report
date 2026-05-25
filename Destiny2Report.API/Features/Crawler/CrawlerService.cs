using D2Report.BungieClient;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Destiny2Report.API.Features.Crawler;

public class CrawlerService(
    ILogger<CrawlerService> logger,
    IMongoDatabase mongoDatabase,
    ID2ReportClient bungieClient) : ICrawlerService
{

    public async Task CrawlAsync(int platformId, long playerMembershipId, CancellationToken cancellationToken)
    {
        
    }
}
