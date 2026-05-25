namespace Destiny2Report.API.Features.Crawler;

public interface ICrawlerService
{
    Task CrawlAsync(int platformId, long playerMembershipId, CancellationToken cancellationToken);
}
