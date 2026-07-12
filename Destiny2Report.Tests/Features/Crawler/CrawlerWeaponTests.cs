using System.Collections;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.Tests.TestSupport;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerWeaponTests
{
    [Fact]
    public void AddWeapons_prefers_unique_weapon_kills_and_falls_back_to_kills()
    {
        var weapons = NewWeaponDeltaDictionary();
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

        CrawlerReflection.Invoke("AddWeapons", weapons, new[] { entry });

        Assert.Equal(10, TotalKills(weapons[100L]!));
        Assert.Equal(4, TotalKills(weapons[200L]!));
        Assert.Equal(7, IntProperty(weapons[100L]!, "UniqueWeaponKills"));
        Assert.Equal(3, IntProperty(weapons[100L]!, "WeaponKills"));
    }

    [Fact]
    public void AddWeapons_normalizes_overflowed_signed_reference_ids_to_unsigned_hashes()
    {
        var weapons = NewWeaponDeltaDictionary();
        var entry = BungieFixture.Entry(
            4611686018463095984,
            extended: new Extended
            {
                Weapons =
                [
                    BungieFixture.Weapon(unchecked((int)3_500_000_000u), ("uniqueWeaponKills", 9))
                ]
            });

        CrawlerReflection.Invoke("AddWeapons", weapons, new[] { entry });

        Assert.False(weapons.Contains(unchecked((int)3_500_000_000u)));
        Assert.Equal(9, TotalKills(weapons[3_500_000_000L]!));
    }

    [Fact]
    public void AddWeapons_adds_pgcr_extended_ability_kills_as_synthetic_ability_rows()
    {
        var weapons = NewWeaponDeltaDictionary();
        var entry = BungieFixture.Entry(
            4611686018463095984,
            extended: new Extended
            {
                Values = BungieFixture.Stats(
                    ("weaponKillsGrenade", 11),
                    ("weaponKillsMelee", 7),
                    ("weaponKillsSuper", 5))
            });

        CrawlerReflection.Invoke("AddWeapons", weapons, new[] { entry });

        Assert.Equal(11, IntProperty(weapons[-1L]!, "GrenadeKills"));
        Assert.Equal(7, IntProperty(weapons[-2L]!, "MeleeKills"));
        Assert.Equal(5, IntProperty(weapons[-3L]!, "SuperKills"));
    }

    [Fact]
    public void AddWeapons_adds_unaccounted_pgcr_kills_as_unknown()
    {
        var weapons = NewWeaponDeltaDictionary();
        var entry = BungieFixture.Entry(
            4611686018463095984,
            values: BungieFixture.Stats(("kills", 20)),
            extended: new Extended
            {
                Weapons = [BungieFixture.Weapon(100, ("uniqueWeaponKills", 7))],
                Values = BungieFixture.Stats(("weaponKillsGrenade", 5), ("weaponKillsMelee", 3))
            });

        CrawlerReflection.Invoke("AddWeapons", weapons, new[] { entry });

        Assert.Equal(5, IntProperty(weapons[-4L]!, "UnknownKills"));
        Assert.Equal(20, weapons.Values.Cast<object>().Sum(TotalKills));
    }

    [Fact]
    public void AddWeapons_does_not_add_unknown_kills_when_extended_data_exceeds_pgcr_kills()
    {
        var weapons = NewWeaponDeltaDictionary();
        var entry = BungieFixture.Entry(
            4611686018463095984,
            values: BungieFixture.Stats(("kills", 4)),
            extended: new Extended
            {
                Weapons = [BungieFixture.Weapon(100, ("uniqueWeaponKills", 5))]
            });

        CrawlerReflection.Invoke("AddWeapons", weapons, new[] { entry });

        Assert.False(weapons.Contains(-4L));
    }

    [Fact]
    public void GetWeaponDeltasForClassAndMode_keeps_weapon_kills_in_their_class_and_specific_activity_mode()
    {
        var deltaType = CrawlerReflection.NestedType("WeaponKillDelta");
        var classAndModeType = typeof(ValueTuple<,>).MakeGenericType(typeof(string), typeof(int));
        var weaponsByClassAndModeType = typeof(Dictionary<,>).MakeGenericType(
            classAndModeType,
            typeof(Dictionary<,>).MakeGenericType(typeof(long), deltaType));
        var weaponsByClassAndMode = (IDictionary)Activator.CreateInstance(weaponsByClassAndModeType)!;
        var control = BungieFixture.Pgcr(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 10, 1);
        var clash = BungieFixture.Pgcr(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 12, 2);

        var controlDeltas = CrawlerReflection.Invoke("GetWeaponDeltasForClassAndMode", weaponsByClassAndMode, control, "Titan")!;
        var clashDeltas = CrawlerReflection.Invoke("GetWeaponDeltasForClassAndMode", weaponsByClassAndMode, clash, "Hunter")!;

        Assert.NotSame(controlDeltas, clashDeltas);
        Assert.Same(controlDeltas, CrawlerReflection.Invoke("GetWeaponDeltasForClassAndMode", weaponsByClassAndMode, control, "Titan"));
        Assert.Equal(2, weaponsByClassAndMode.Count);
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
            values: BungieFixture.Stats(("motesDeposited", 12), ("motesLost", 3), ("motesDenied", 6), ("bankOverage", 4)));
        var gambitPgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), 63, 1, gambitEntry);
        gambitPgcr.ActivityDetails.Modes = [7, 64, 63, 75];
        var primeEntry = BungieFixture.Entry(
            4611686018463095984,
            values: BungieFixture.Stats(("motesDeposited", 9), ("motesLost", 2), ("motesDenied", 7), ("bankOverage", 5)));
        var primePgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2020-01-01T00:00:00Z"), 75, 2, primeEntry);
        primePgcr.ActivityDetails.Modes = [7, 64, 75];
        var competitiveEntry = BungieFixture.Entry(
            4611686018463095984,
            values: BungieFixture.Stats(("motesDeposited", 8), ("motesLost", 1), ("motesDenied", 4), ("bankOverage", 2)));
        var competitivePgcr = BungieFixture.Pgcr(DateTimeOffset.Parse("2025-01-01T00:00:00Z"), 64, 3, competitiveEntry);
        competitivePgcr.ActivityDetails.Modes = [7, 64];

        CrawlerReflection.Invoke("AddGambitMoteStats", accumulator, gambitPgcr, gambitEntry);
        CrawlerReflection.Invoke("AddGambitMoteStats", accumulator, primePgcr, primeEntry);
        CrawlerReflection.Invoke("AddGambitMoteStats", accumulator, competitivePgcr, competitiveEntry);

        Assert.Equal(29, accumulator.GambitMotesBanked);
        Assert.Equal(6, accumulator.GambitMotesLost);
        Assert.Equal(17, accumulator.GambitMotesDenied);
        Assert.Equal(12, accumulator.GambitMotesBankedByMode["63"]);
        Assert.Equal(9, accumulator.GambitMotesBankedByMode["75"]);
        Assert.Equal(8, accumulator.GambitMotesBankedByMode["64"]);
        Assert.Equal(3, accumulator.GambitMotesLostByMode["63"]);
        Assert.Equal(2, accumulator.GambitMotesLostByMode["75"]);
        Assert.Equal(1, accumulator.GambitMotesLostByMode["64"]);
        Assert.Equal(6, accumulator.GambitMotesDeniedByMode["63"]);
        Assert.Equal(7, accumulator.GambitMotesDeniedByMode["75"]);
        Assert.Equal(4, accumulator.GambitMotesDeniedByMode["64"]);

        accumulator.GambitMoteMatches = 3;
        var report = (GambitMotesReport)CrawlerReflection.Invoke("ToGambitMotesReport", accumulator)!;

        Assert.Equal(3, report.Matches);
        Assert.Equal(13.33, report.AverageMotesBanked);
        Assert.Equal(2, report.AverageMotesLost);
        Assert.Equal(40, report.MotesBanked.Total);
        Assert.Equal(16, report.MotesBanked.ByMode["Gambit"]);
        Assert.Equal(14, report.MotesBanked.ByMode["GambitPrime"]);
        Assert.Equal(10, report.MotesBanked.ByMode["AllPvECompetitive"]);
        Assert.Equal(6, report.MotesLost.Total);
        Assert.Equal(3, report.MotesLost.ByMode["Gambit"]);
        Assert.Equal(2, report.MotesLost.ByMode["GambitPrime"]);
        Assert.Equal(1, report.MotesLost.ByMode["AllPvECompetitive"]);
        Assert.Equal(17, report.MotesDenied.Total);
        Assert.Equal(6, report.MotesDenied.ByMode["Gambit"]);
        Assert.Equal(7, report.MotesDenied.ByMode["GambitPrime"]);
        Assert.Equal(4, report.MotesDenied.ByMode["AllPvECompetitive"]);
    }

    [Fact]
    public void BuildWeaponReports_returns_top_ten_positive_weapons_in_kill_order()
    {
        var weaponKills = NewWeaponDeltaDictionary();
        foreach (var hash in Enumerable.Range(1, 12))
        {
            weaponKills[(long)hash] = NewWeaponDelta(uniqueWeaponKills: hash * 10);
        }
        weaponKills[13L] = NewWeaponDelta();

        var result = (List<WeaponReport>)CrawlerReflection.Invoke("BuildWeaponReports", weaponKills, null)!;

        Assert.Equal(10, result.Count);
        Assert.Equal([12, 11, 10, 9, 8, 7, 6, 5, 4, 3], result.Select(item => int.Parse(item.Name)));
        Assert.Equal([120, 110, 100, 90, 80, 70, 60, 50, 40, 30], result.Select(item => item.TotalKills));
    }

    [Fact]
    public void BuildWeaponReports_groups_different_hashes_by_resolved_weapon_name()
    {
        var weaponKills = NewWeaponDeltaDictionary();
        weaponKills[100L] = NewWeaponDelta(uniqueWeaponKills: 7);
        weaponKills[200L] = NewWeaponDelta(uniqueWeaponKills: 13);
        weaponKills[300L] = NewWeaponDelta(uniqueWeaponKills: 5);
        var summaryType = CrawlerReflection.NestedType("WeaponDefinitionSummary");
        var definitionsType = typeof(Dictionary<,>).MakeGenericType(typeof(long), summaryType);
        var definitions = (IDictionary)Activator.CreateInstance(definitionsType)!;

        definitions.Add(100L, Activator.CreateInstance(summaryType, "Edge Transit", "/edge-old.png", "Grenade Launcher", "GRENADE LAUNCHER"));
        definitions.Add(200L, Activator.CreateInstance(summaryType, "Edge Transit", "/edge-new.png", "Grenade Launcher", "GRENADE LAUNCHER"));
        definitions.Add(300L, Activator.CreateInstance(summaryType, "Other Half", "/other.png", "Sword", "SWORD"));

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
    public void WeaponCategory_groups_synthetic_ability_rows_as_abilities()
    {
        var result = CrawlerReflection.Invoke("WeaponCategory", -1L, null)!;

        Assert.Equal("Abilities", result.GetType().GetField("Item1")!.GetValue(result));
        Assert.Equal("ABILITIES", result.GetType().GetField("Item2")!.GetValue(result));
    }

    private static IDictionary NewWeaponDeltaDictionary()
    {
        var deltaType = CrawlerReflection.NestedType("WeaponKillDelta");
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(long), deltaType);
        return (IDictionary)Activator.CreateInstance(dictionaryType)!;
    }

    private static object NewWeaponDelta(int uniqueWeaponKills = 0, int weaponKills = 0)
    {
        var delta = Activator.CreateInstance(CrawlerReflection.NestedType("WeaponKillDelta"))!;
        delta.GetType().GetProperty("UniqueWeaponKills")!.SetValue(delta, uniqueWeaponKills);
        delta.GetType().GetProperty("WeaponKills")!.SetValue(delta, weaponKills);
        return delta;
    }

    private static int TotalKills(object delta)
    {
        return IntProperty(delta, "TotalKills");
    }

    private static int IntProperty(object value, string name)
    {
        return (int)value.GetType().GetProperty(name)!.GetValue(value)!;
    }
}
