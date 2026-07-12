namespace Destiny2Report.API.Features.Crawler.Models;

public record DeathModeAggregateReport
{
    public int SpecificActivityModeId { get; init; }
    public string SpecificActivityMode { get; init; } = "";
    public long Deaths { get; init; }
}
