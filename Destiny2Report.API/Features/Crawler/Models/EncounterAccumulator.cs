namespace Destiny2Report.API.Features.Crawler.Models;

public record EncounterAccumulator
{
    public int MembershipType { get; set; }
    public long MembershipId { get; set; }
    public int Count { get; set; }
}
