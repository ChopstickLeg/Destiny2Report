namespace Destiny2Report.API.Features.Crawler.Models;

public record WeaponActivityModeAggregateReport
{
    public string ActivityMode { get; init; } = "";
    public IReadOnlyCollection<WeaponClassAggregateReport> Classes { get; init; } = [];
}
