namespace Destiny2Report.API.Features.Crawler.Models;

// Read-model only: display metadata is resolved from the manifest, never persisted per aggregate row.
public record WeaponAggregateDetailReport
{
    public int OwnerMembershipType { get; init; }
    public long OwnerMembershipId { get; init; }
    public string ActivityMode { get; init; } = "";
    public string WeaponKey { get; init; } = "";
    public string WeaponName { get; init; } = "";
    public long ReferenceId { get; init; }
    public string IconUrl { get; init; } = "";
    public string CategoryKey { get; init; } = "";
    public string CategoryName { get; init; } = "";
    public int Kills { get; init; }
}
