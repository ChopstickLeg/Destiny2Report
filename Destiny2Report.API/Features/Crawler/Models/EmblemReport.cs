namespace Destiny2Report.API.Features.Crawler.Models;

public record EmblemReport
{
    public string Name { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string BackgroundUrl { get; init; } = "";
    public TimeSpan TotalPlaytime { get; init; }
}
