namespace Destiny2Report.API.Features.Crawler.Models;

public record WeaponCategoryAggregateReport
{
    public int OwnerMembershipType { get; init; }
    public long OwnerMembershipId { get; init; }
    public string ActivityMode { get; init; } = "";
    public string CategoryKey { get; init; } = "";
    public string CategoryName { get; init; } = "";
    public int Kills { get; init; }
    public IReadOnlyCollection<WeaponAggregate> Weapons { get; init; } = [];
}
