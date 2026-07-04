namespace Destiny2Report.API.Features.Crawler;

public sealed class CrawlerOptions
{
    public const string SectionName = "Crawler";

    public int MaxConcurrentPgcrRequests { get; init; } = 50;

    public int? MaxBufferedPgcrs { get; init; }
}
