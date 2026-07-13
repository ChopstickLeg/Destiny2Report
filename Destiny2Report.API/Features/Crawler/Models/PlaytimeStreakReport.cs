namespace Destiny2Report.API.Features.Crawler.Models;

public record PlaytimeStreakReport
{
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
}
