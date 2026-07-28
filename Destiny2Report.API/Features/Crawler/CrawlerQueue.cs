namespace Destiny2Report.API.Features.Crawler;

public static class CrawlerQueue
{
    public const int ProtocolVersion = 1;
    public const string StreamName = "crawler:jobs";
    public const string ConsumerGroupName = "crawler-workers";
    public const string EventsChannelName = "crawler:job-events";
    public static readonly TimeSpan ActiveJobStatusTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan TerminalJobStatusTtl = TimeSpan.FromHours(6);

    public static string JobStatusKey(int membershipTypeId, long membershipId) =>
        $"crawler:job:{membershipTypeId}:{membershipId}";

}
