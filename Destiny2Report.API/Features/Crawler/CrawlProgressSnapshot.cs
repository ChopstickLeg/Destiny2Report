namespace Destiny2Report.API.Features.Crawler;

public sealed record CrawlProgressSnapshot(
    string Phase,
    string Label,
    long? Current,
    long? Total,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc);
