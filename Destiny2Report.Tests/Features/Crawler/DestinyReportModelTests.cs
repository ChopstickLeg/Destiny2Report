using Destiny2Report.API.Features.Crawler.Models;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class DestinyReportModelTests
{
    [Fact]
    public void MostPlayedWith_keeps_only_top_limit_in_original_order()
    {
        var report = new DestinyReport();
        var encounters = Enumerable.Range(1, DestinyReport.MostPlayedWithLimit + 5)
            .Select(index => new PlayerEncounterReport
            {
                Player = new DestinyPlayer
                {
                    MembershipType = 1,
                    MembershipId = index,
                    DisplayName = $"Player {index}"
                },
                EncounterCount = index
            })
            .ToList();

        report.MostPlayedWith = encounters;

        Assert.Equal(DestinyReport.MostPlayedWithLimit, report.MostPlayedWith.Count);
        Assert.Equal(Enumerable.Range(1, DestinyReport.MostPlayedWithLimit), report.MostPlayedWith.Select(item => (int)item.Player.MembershipId));
    }

    [Fact]
    public void MostPlayedWith_allows_null_assignment_as_empty_list()
    {
        var report = new DestinyReport
        {
            MostPlayedWith = null!
        };

        Assert.Empty(report.MostPlayedWith);
    }

    [Fact]
    public void TriumphSeals_keeps_only_completed_seals()
    {
        var report = new DestinyReport
        {
            TriumphSeals =
            [
                new DestinyTriumphSeal { Name = "Conqueror", IsCompleted = true },
                new DestinyTriumphSeal { Name = "Unfinished", IsCompleted = false },
                new DestinyTriumphSeal { Name = "Rivensbane", IsCompleted = true }
            ]
        };

        Assert.Equal(["Conqueror", "Rivensbane"], report.TriumphSeals.Select(seal => seal.Name));
    }
}
