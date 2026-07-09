namespace Destiny2Report.API.Features.Crawler;

public sealed class CrawlerOptions
{
    public const string SectionName = "Crawler";

    public int? MaxBufferedPgcrs { get; init; }

    public int PgcrRequestsPerSecond { get; init; } = 45;

    public int PgcrRateLimitQueueLimit { get; init; } = 1_000;

    public int SherpaHistoryRequestsPerSecond { get; init; } = 8;

    public int SherpaHistoryRateLimitQueueLimit { get; init; } = 1_000;
}
