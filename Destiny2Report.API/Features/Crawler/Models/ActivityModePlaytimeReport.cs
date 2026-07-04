namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityModePlaytimeReport
{
    public int Mode { get; init; }
    public string ModeName { get; init; } = "";
    public TimeSpan TotalPlaytime { get; init; }
    public List<ActivityModePlaytimeBreakdown> MostSpecificModes { get; init; } = new();
}
