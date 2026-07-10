namespace Destiny2Report.API.Features.Crawler.Models;

public record WeaponClassAggregateReport
{
    public string ClassName { get; init; } = "";
    public IReadOnlyCollection<WeaponModeAggregateReport> Modes { get; init; } = [];
}
