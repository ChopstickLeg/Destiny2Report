namespace Destiny2Report.API.Features.Crawler.Models;

public record PvpPlaylistAccumulator
{
    public int Wins { get; set; }
    public int Losses { get; set; }
}
