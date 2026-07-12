namespace Destiny2Report.API.Features.Crawler.Models;

public record CharacterPlaytimeReport
{
    public string Race { get; init; } = "Unknown";
    public string Class { get; init; } = "Unknown";
    public bool IsDeleted { get; init; }
    public TimeSpan Playtime { get; init; }
}
