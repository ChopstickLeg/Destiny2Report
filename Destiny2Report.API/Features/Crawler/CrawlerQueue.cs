namespace Destiny2Report.API.Features.Crawler;

public static class CrawlerQueue
{
    public const string StreamName = "crawler:jobs";
    public const string ConsumerGroupName = "crawler-workers";
    public const string EventsChannelName = "crawler:job-events";

    public static string JobStatusKey(int membershipTypeId, long membershipId) =>
        $"crawler:job:{membershipTypeId}:{membershipId}";
}
