namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityModePlaytimeBreakdown
{
    public int Mode { get; init; }
    public string ModeName { get; init; } = "";
    public TimeSpan Playtime { get; init; }
}
