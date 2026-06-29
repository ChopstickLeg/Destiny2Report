using System.Collections;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.Tests.TestSupport;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerWeaponAndRivalTests
{
    [Fact]
    public void AddWeapons_prefers_unique_weapon_kills_and_falls_back_to_kills()
    {
        var weapons = new Dictionary<int, int>();
        var entry = BungieFixture.Entry(
            4611686018463095984,
            extended: new Extended
            {
                Weapons =
                [
                    BungieFixture.Weapon(100, ("uniqueWeaponKills", 7), ("kills", 99)),
                    BungieFixture.Weapon(200, ("uniqueWeaponKills", 0), ("kills", 4)),
                    BungieFixture.Weapon(100, ("kills", 3))
                ]
            });

        CrawlerReflection.Invoke("AddWeapons", weapons, entry);

        Assert.Equal(10, weapons[100]);
        Assert.Equal(4, weapons[200]);
    }

    [Fact]
    public void GetMoteStat_returns_exact_stat_and_ignores_other_mote_like_values()
    {
        var entry = BungieFixture.Entry(
            4611686018463095984,
            values: BungieFixture.Stats(("motesDeposited", 12), ("kills", 30)),
            extended: new Extended
            {
                Values = BungieFixture.Stats(("bankedMotes", 8), ("motesLost", 3)),
                ScoreboardValues = BungieFixture.Stats(("primevalMotesBanked", 5), ("deaths", 1))
            });

        var banked = (double)CrawlerReflection.Invoke("GetMoteStat", entry, "motesDeposited")!;
        var lost = (double)CrawlerReflection.Invoke("GetMoteStat", entry, "motesLost")!;

        Assert.Equal(12, banked);
        Assert.Equal(3, lost);
    }

    [Fact]
    public void AddGambitMoteStats_tracks_totals_by_gambit_mode()
    {
        var accumulator = new CrawlAccumulator();
        var gambitEntry = BungieFixture.Entry(
            4611686018463095984,
            values: BungieFixture.Stats(("motesDeposited", 12), ("motesLost", 3), ("bankOverage", 4)));
        var gambitPgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 63, 1, gambitEntry);
        gambitPgcr.ActivityDetails.Modes = [7, 64, 63, 75];
        var primeEntry = BungieFixture.Entry(
            4611686018463095984,
            values: BungieFixture.Stats(("motesDeposited", 9), ("motesLost", 2), ("bankOverage", 5)));
        var primePgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2020-01-01T00:00:00Z"), 75, 2, primeEntry);
        primePgcr.ActivityDetails.Modes = [7, 64, 75];

        CrawlerReflection.Invoke("AddGambitMoteStats", accumulator, gambitPgcr, gambitEntry, true);
        CrawlerReflection.Invoke("AddGambitMoteStats", accumulator, primePgcr, primeEntry, false);

        Assert.Equal(21, accumulator.GambitMotesBanked);
        Assert.Equal(5, accumulator.GambitMotesLost);
        Assert.Equal(9, accumulator.GambitBankOverage);
        Assert.Equal(12, accumulator.GambitMotesBankedByMode["63"]);
        Assert.Equal(9, accumulator.GambitMotesBankedByMode["75"]);
        Assert.Equal(3, accumulator.GambitMotesLostByMode["63"]);
        Assert.Equal(2, accumulator.GambitMotesLostByMode["75"]);
        Assert.Equal(4, accumulator.GambitBankOverageByMode["63"]);
        Assert.Equal(5, accumulator.GambitBankOverageByMode["75"]);
        Assert.Equal(12, accumulator.GambitMotesBankedByCompletionStatus["completed"]);
        Assert.Equal(9, accumulator.GambitMotesBankedByCompletionStatus["incomplete"]);
    }

    [Fact]
    public void BuildWeaponReports_returns_top_ten_positive_weapons_in_kill_order()
    {
        var weaponKills = Enumerable.Range(1, 12)
            .ToDictionary(hash => hash, hash => hash * 10);
        weaponKills[13] = 0;

        var result = (List<WeaponReport>)CrawlerReflection.Invoke("BuildWeaponReports", weaponKills, null)!;

        Assert.Equal(10, result.Count);
        Assert.Equal([12, 11, 10, 9, 8, 7, 6, 5, 4, 3], result.Select(item => int.Parse(item.Name)));
        Assert.Equal([120, 110, 100, 90, 80, 70, 60, 50, 40, 30], result.Select(item => item.TotalKills));
    }

    [Fact]
    public void BuildWeaponReports_groups_different_hashes_by_resolved_weapon_name()
    {
        var weaponKills = new Dictionary<int, int>
        {
            [100] = 7,
            [200] = 13,
            [300] = 5
        };
        var summaryType = CrawlerReflection.NestedType("WeaponDefinitionSummary");
        var definitionsType = typeof(Dictionary<,>).MakeGenericType(typeof(int), summaryType);
        var definitions = (IDictionary)Activator.CreateInstance(definitionsType)!;

        definitions.Add(100, Activator.CreateInstance(summaryType, "Edge Transit", "/edge-old.png"));
        definitions.Add(200, Activator.CreateInstance(summaryType, "Edge Transit", "/edge-new.png"));
        definitions.Add(300, Activator.CreateInstance(summaryType, "Other Half", "/other.png"));

        var result = (List<WeaponReport>)CrawlerReflection.Invoke("BuildWeaponReports", weaponKills, definitions)!;

        Assert.Collection(
            result,
            weapon =>
            {
                Assert.Equal("Edge Transit", weapon.Name);
                Assert.Equal(20, weapon.TotalKills);
            },
            weapon =>
            {
                Assert.Equal("Other Half", weapon.Name);
                Assert.Equal(5, weapon.TotalKills);
            });
    }

    [Fact]
    public void TrackRivals_counts_only_opposing_team_players_once_per_match()
    {
        var aggregateType = CrawlerReflection.NestedType("RivalAggregate");
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(long), aggregateType);
        var rivals = Activator.CreateInstance(dictionaryType)!;
        var report = new DestinyReport();
        const long ownerId = 4611686018463095984;

        var ownerEntry = BungieFixture.Entry(
            ownerId,
            standing: 0,
            values: BungieFixture.Stats(("team", 17), ("kills", 20), ("deaths", 5)));
        var opponentOneFirstCharacter = BungieFixture.Entry(
            100,
            characterId: 1001,
            displayName: "Opponent",
            values: BungieFixture.Stats(("team", 18)));
        var opponentOneSecondCharacter = BungieFixture.Entry(
            100,
            characterId: 1002,
            displayName: "Opponent",
            values: BungieFixture.Stats(("team", 18)));
        var teammate = BungieFixture.Entry(
            200,
            characterId: 2001,
            displayName: "Teammate",
            values: BungieFixture.Stats(("team", 17)));

        var pgcr = BungieFixture.Pgcr(
            DateTimeOffset.Parse("2024-04-01T00:00:00Z"),
            5,
            900,
            ownerEntry,
            opponentOneFirstCharacter,
            opponentOneSecondCharacter,
            teammate);

        CrawlerReflection.Invoke("TrackRivals", rivals, pgcr, ownerEntry, ownerId, 20d, 5d);
        CrawlerReflection.Invoke("ApplyRival", report, rivals, false);

        Assert.NotNull(report.CrucibleRival);
        Assert.Equal(100, report.CrucibleRival.MembershipId);
        Assert.Equal(4.0, report.KdAgainstCrucibleRival);
        Assert.Single((IEnumerable)rivals);
    }
}
