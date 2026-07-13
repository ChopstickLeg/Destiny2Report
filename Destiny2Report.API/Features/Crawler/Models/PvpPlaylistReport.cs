namespace Destiny2Report.API.Features.Crawler.Models;

public record PvpPlaylistReport
{
    public int Mode { get; init; }
    public string ModeName { get; init; } = "";
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Matches => Wins + Losses;
    public double WinRate => Matches == 0 ? 0 : Math.Round((double)Wins / Matches, 4);
}
