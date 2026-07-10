namespace Destiny2Report.API.Features.Crawler.Models;

public record CrucibleKillsReport
{
    public long Total { get; init; }
    public Dictionary<string, long> ByMode { get; init; } = new();
}
