namespace Destiny2Report.API.Features.Crawler.Models;

public record DeathActivityModeAggregateReport
{
    public string ActivityMode { get; init; } = "";
    public long Deaths { get; init; }
    public IReadOnlyCollection<DeathModeAggregateReport> Modes { get; init; } = [];
}
