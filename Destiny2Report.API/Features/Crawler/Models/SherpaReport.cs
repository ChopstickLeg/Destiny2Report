namespace Destiny2Report.API.Features.Crawler.Models;

public record SherpaReport
{
    public string RaidName { get; init; } = "";
    public int PlayerCount { get; init; }
}
