namespace Destiny2Report.API.Features.Crawler.Models;

public record DestinyPlayer
{
    public long MembershipId { get; init; }
    public int MembershipType { get; init; }
    public string DisplayName { get; init; } = "";
    public string EmblemUrl { get; init; } = "";
}
