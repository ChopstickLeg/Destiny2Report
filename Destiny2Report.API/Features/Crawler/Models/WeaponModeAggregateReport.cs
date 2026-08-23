namespace Destiny2Report.API.Features.Crawler.Models;

public record WeaponModeAggregateReport
{
    public int SpecificActivityModeId { get; init; }
    public string SpecificActivityMode { get; init; } = "";
    public IReadOnlyCollection<WeaponCategoryAggregateReport> Categories { get; init; } = [];
}
