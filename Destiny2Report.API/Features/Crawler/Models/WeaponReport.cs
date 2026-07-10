namespace Destiny2Report.API.Features.Crawler.Models;

public record WeaponReport
{
    public string Name { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public int TotalKills { get; init; }
}
