namespace Destiny2Report.API.Features.Crawler.Models;

public record WeaponModeAggregateReport
{
    public string SpecificActivityMode { get; init; } = "";
    public IReadOnlyCollection<WeaponCategoryAggregateReport> Categories { get; init; } = [];
}
