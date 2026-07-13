namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityPlaytimeAggregateReport
{
    public string ActivityMode { get; init; } = "";
    public TimeSpan TotalPlaytime { get; init; }
    public IReadOnlyCollection<ActivityModePlaytimeBreakdown> Modes { get; init; } = [];
}
