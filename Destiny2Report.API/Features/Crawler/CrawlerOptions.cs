namespace Destiny2Report.API.Features.Crawler;

public sealed class CrawlerOptions
{
    public const string SectionName = "Crawler";

    public int BackgroundConcurrency { get; init; } = 1;
}
