namespace Destiny2Report.API.Features.Crawler.Models;

public record PlayerEncounterReport
{
    public DestinyPlayer Player { get; init; } = new();
    public int EncounterCount { get; init; }
}
