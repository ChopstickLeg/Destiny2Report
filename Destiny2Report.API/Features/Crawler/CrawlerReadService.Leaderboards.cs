using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Leaderboards;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerReadService
{
    public async Task<IReadOnlyCollection<LeaderboardMetric>> GetLeaderboardMetricsAsync(
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var reportFilter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, membershipId)
            & Builders<DestinyReport>.Filter.Eq(item => item.HasCompletedCrawl, true);
        var accumulatorFilter = Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, membershipTypeId)
            & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, membershipId);
        var reportTask = mongoDatabase.GetCollection<DestinyReport>("destiny_reports").Find(reportFilter).FirstOrDefaultAsync(cancellationToken);
        var accumulatorTask = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators").Find(accumulatorFilter).FirstOrDefaultAsync(cancellationToken);
        await Task.WhenAll(reportTask, accumulatorTask).ConfigureAwait(false);
        return reportTask.Result is null || accumulatorTask.Result is null
            ? []
            : await BuildLeaderboardMetricsAsync(reportTask.Result, accumulatorTask.Result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<LeaderboardMetric>> BuildLeaderboardMetricsAsync(
        DestinyReport report,
        CrawlAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LeaderboardMetric>(StringComparer.Ordinal);
        void Add(string key, string category, string title, string description, string unit, int order, long score)
        {
            if (score > 0) result[key] = new LeaderboardMetric(key, category, title, description, unit, order, score);
        }

        foreach (var classGroup in report.CharacterPlaytime
                     .Where(character => IsKnownClass(character.Class))
                     .GroupBy(character => character.Class, StringComparer.OrdinalIgnoreCase))
        {
            var className = NormalizeClass(classGroup.Key);
            Add($"time.class.{className.ToLowerInvariant()}", "Time", $"{className} playtime", $"Time spent playing {className} characters.", "seconds", 10, (long)classGroup.Sum(item => item.Playtime.TotalSeconds));
        }

        var patrolSeconds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (destination, duration) in report.PatrolTimeByPlanet)
        {
            if (!DestinyDisplayNames.TryCanonicalPatrolDestination(destination, out var canonical)) continue;
            patrolSeconds[canonical] = patrolSeconds.GetValueOrDefault(canonical) + (long)duration.TotalSeconds;
        }
        Add("time.patrol.total", "Time", "Patrol time", "Total time spent in patrol destinations.", "seconds", 20, patrolSeconds.Values.Sum());
        foreach (var (destination, seconds) in patrolSeconds)
            Add($"time.patrol.{DestinyDisplayNames.Slug(destination)}", "Time", $"{destination} patrol time", $"Time spent patrolling {destination}.", "seconds", 21, seconds);

        foreach (var (modeKey, aggregate) in accumulator.PlaytimeByActivityMode)
        {
            if (!int.TryParse(modeKey, out var broadMode)) continue;
            var broad = broadMode switch
            {
                ActivityModes.AllPvE => ("pve", "PvE"),
                ActivityModes.AllPvP => ("crucible", "Crucible"),
                ActivityModes.AllPvECompetitive => ("gambit", "Gambit"),
                _ => default
            };
            if (broad == default) continue;
            Add($"time.mode.{broad.Item1}", "Time", $"{broad.Item2} playtime", $"Time spent in {broad.Item2} activities.", "seconds", 30, aggregate.TotalSeconds);
            foreach (var (specificKey, seconds) in aggregate.MostSpecificModeSeconds)
            {
                if (!int.TryParse(specificKey, out var mode) || !TryRecognizedMode(mode, out var label)) continue;
                Add($"time.mode.{mode}", "Time", $"{label} playtime", $"Time spent in {label}.", "seconds", 31, seconds);
            }
        }

        Add("combat.kills.total", "Combat", "Total kills", "All kills recorded across activity history.", "count", 100, report.TotalKills);

        var weapons = mongoDatabase.GetCollection<WeaponAggregate>("weapon_aggregates");
        var weaponFilter = Builders<WeaponAggregate>.Filter.Eq(item => item.OwnerMembershipType, report.PlatformId)
            & Builders<WeaponAggregate>.Filter.Eq(item => item.OwnerMembershipId, report.PlayerMembershipId);
        var aggregates = await weapons.Find(weaponFilter).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var classGroup in aggregates.Where(item => IsKnownClass(item.ClassName)).GroupBy(item => NormalizeClass(item.ClassName)))
            Add($"combat.kills.class.{classGroup.Key.ToLowerInvariant()}", "Combat", $"{classGroup.Key} kills", $"Kills earned while playing {classGroup.Key}.", "count", 110, classGroup.Sum(item => (long)item.Kills));
        foreach (var modeGroup in aggregates.GroupBy(item => item.ActivityMode, StringComparer.OrdinalIgnoreCase))
        {
            var key = modeGroup.Key.Equals("Crucible", StringComparison.OrdinalIgnoreCase) ? "crucible" : modeGroup.Key.ToLowerInvariant();
            var label = key == "pve" ? "PvE" : key == "crucible" ? "Crucible" : "Gambit";
            Add($"combat.kills.mode.{key}", "Combat", $"{label} kills", $"Kills earned in {label} activities.", "count", 120, modeGroup.Sum(item => (long)item.Kills));
        }
        foreach (var modeGroup in aggregates.GroupBy(item => item.SpecificActivityMode))
        {
            if (!TryRecognizedMode(modeGroup.Key, out var label)) continue;
            Add($"combat.kills.mode.{modeGroup.Key}", "Combat", $"{label} kills", $"Kills earned in {label}.", "count", 121, modeGroup.Sum(item => (long)item.Kills));
        }

        var realWeaponAggregates = aggregates.Where(item => item.WeaponHash > 0).ToArray();
        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await GetInventoryItemSummariesAsync(manifest, realWeaponAggregates.Select(item => item.WeaponHash), cancellationToken).ConfigureAwait(false);
        var byWeapon = realWeaponAggregates.GroupBy(item => item.WeaponHash).Select(group => (Hash: group.Key, Kills: group.Sum(item => (long)item.Kills)));
        var categoryKills = new Dictionary<string, (string Label, long Kills)>(StringComparer.Ordinal);
        var damageKills = new Dictionary<int, long>();
        long exoticKills = 0;
        foreach (var weapon in byWeapon)
        {
            if (!definitions.TryGetValue(weapon.Hash, out var definition)
                || !LeaderboardMetricRules.IsRecognizedWeapon(weapon.Hash, definition.CategoryName)) continue;
            var categoryKey = DestinyDisplayNames.Slug(definition.CategoryName);
            if (!string.IsNullOrWhiteSpace(categoryKey))
            {
                var existing = categoryKills.GetValueOrDefault(categoryKey);
                categoryKills[categoryKey] = (definition.CategoryName, existing.Kills + weapon.Kills);
            }
            if (definition.TierType == 6) exoticKills += weapon.Kills;
            if (DamageTypeName(definition.DamageType) is not null) damageKills[definition.DamageType] = damageKills.GetValueOrDefault(definition.DamageType) + weapon.Kills;
        }
        foreach (var (key, value) in categoryKills)
            Add($"combat.weapon-type.{key}", "Combat", $"{value.Label} kills", $"Kills earned with {value.Label} weapons.", "count", 130, value.Kills);
        Add("combat.exotic", "Combat", "Exotic weapon kills", "Kills earned with Exotic weapons.", "count", 140, exoticKills);
        foreach (var (damageType, kills) in damageKills)
        {
            var label = DamageTypeName(damageType)!;
            Add($"combat.damage.{label.ToLowerInvariant()}", "Combat", $"{label} weapon kills", $"Kills earned with {label} weapons.", "count", 141, kills);
        }

        Add("competition.crucible.wins", "Competition", "Crucible wins", "Wins across Crucible playlists.", "count", 200, report.CrucibleWins);
        foreach (var playlist in report.PvpPlaylists.Where(item => item.Wins > 0 && TryRecognizedMode(item.Mode, out _)))
        {
            TryRecognizedMode(playlist.Mode, out var label);
            Add($"competition.crucible.playlist.{playlist.Mode}", "Competition", $"{label} wins", $"Wins in {label}.", "count", 201, playlist.Wins);
        }
        Add("competition.gambit.wins", "Competition", "Gambit wins", "Wins across Gambit modes.", "count", 210, report.GambitWins);
        foreach (var playlist in report.GambitPlaylists.Where(item => item.Wins > 0 && TryRecognizedMode(item.Mode, out _)))
        {
            TryRecognizedMode(playlist.Mode, out var label);
            Add($"competition.gambit.playlist.{playlist.Mode}", "Competition", $"{label} wins", $"Wins in {label}.", "count", 211, playlist.Wins);
        }

        Add("oddities.good-boy-protocol", "Oddities", "Good Boy Protocol", "Good Boy Protocol activations.", "count", 300, report.GoodBoyProtocol);
        Add("oddities.fish-caught", "Oddities", "Fish caught", "Fish caught across Destiny 2.", "count", 301, report.FishCaught);
        Add("oddities.misadventures", "Oddities", "Misadventures", "Deaths attributed to misadventure.", "count", 302, report.Misadventures);
        Add("oddities.zero-kill-activities", "Oddities", "Zero-kill activities", "Activities completed without recording a kill.", "count", 303, report.ZeroKillActivities);
        Add("oddities.gambit-motes-banked", "Oddities", "Gambit motes banked", "Total Gambit motes banked.", "count", 304, report.GambitMotes.MotesBanked.Total);
        Add("oddities.gambit-motes-lost", "Oddities", "Gambit motes lost", "Total Gambit motes lost.", "count", 305, report.GambitMotes.MotesLost.Total);
        Add("oddities.gambit-motes-denied", "Oddities", "Gambit motes denied", "Total opposing motes denied.", "count", 306, report.GambitMotes.MotesDenied.Total);
        Add("oddities.unique-players", "Oddities", "Unique players encountered", "Distinct Guardians encountered in recorded activities.", "count", 307, report.UniquePlayersPlayedWith);
        if (report.LongestPlaytimeStreak is { } streak)
            Add("oddities.longest-streak", "Oddities", "Longest play streak", "Longest consecutive daily play streak.", "days", 308, Math.Max(1, (streak.EndDate.Date - streak.StartDate.Date).Days + 1));

        return result.Values.ToArray();
    }

    private static bool IsKnownClass(string value) => value.Equals("Titan", StringComparison.OrdinalIgnoreCase) || value.Equals("Hunter", StringComparison.OrdinalIgnoreCase) || value.Equals("Warlock", StringComparison.OrdinalIgnoreCase);
    private static string NormalizeClass(string value) => value.Equals("Titan", StringComparison.OrdinalIgnoreCase) ? "Titan" : value.Equals("Hunter", StringComparison.OrdinalIgnoreCase) ? "Hunter" : "Warlock";

    private static bool TryRecognizedMode(int mode, out string label)
    {
        if (!ActivityModeTypeNames.TryGetValue(mode, out var raw) || raw.StartsWith("Reserved", StringComparison.Ordinal) || raw == "None")
        {
            label = "";
            return false;
        }
        label = DestinyDisplayNames.HumanizeIdentifier(raw);
        return true;
    }

    private static string? DamageTypeName(int damageType) => damageType switch
    {
        1 => "Kinetic", 2 => "Arc", 3 => "Solar", 4 => "Void", 6 => "Stasis", 7 => "Strand", _ => null
    };
}
