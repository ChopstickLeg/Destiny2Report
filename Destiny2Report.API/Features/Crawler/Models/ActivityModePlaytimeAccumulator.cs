namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityModePlaytimeAccumulator
{
    public long TotalSeconds { get; set; }
    public Dictionary<string, long> MostSpecificModeSeconds { get; set; } = new();
}
