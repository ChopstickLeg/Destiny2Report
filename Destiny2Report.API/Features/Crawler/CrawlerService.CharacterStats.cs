using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Crawler.Models.Bungie;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Diagnostics;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;
using ReportPlayer = Destiny2Report.API.Features.Crawler.Models.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private sealed record CharacterIdentity(string Class, string Race);

    private static Dictionary<long, string> BuildCharacterClassMap(
        IEnumerable<DestinyHistoricalStatsPerCharacter> historicalCharacters,
        IEnumerable<DestinyPostGameCarnageReportData> pgcrs,
        long playerMembershipId,
        IReadOnlyCollection<long> historicalCharacterIds)
    {
        var characterClasses = historicalCharacterIds.ToDictionary(characterId => characterId, _ => "Unknown");
        foreach (var character in historicalCharacters)
        {
            if (TryReadHistoricalCharacterClass(character, out var className))
            {
                characterClasses[character.CharacterId] = className;
            }
        }

        foreach (var pgcr in pgcrs)
        {
            TryFillCharacterClassFromPgcr(characterClasses, pgcr, playerMembershipId);
        }

        return characterClasses;
    }

    private static void TryFillCharacterClassFromPgcr(
        IDictionary<long, string> characterClasses,
        DestinyPostGameCarnageReportData pgcr,
        long playerMembershipId)
    {
        foreach (var entry in pgcr.Entries ?? [])
        {
            if (entry.Player?.DestinyUserInfo?.MembershipId != playerMembershipId
                || !characterClasses.TryGetValue(entry.CharacterId, out var currentClass)
                || currentClass != "Unknown"
                || string.IsNullOrWhiteSpace(entry.Player.CharacterClass))
            {
                continue;
            }

            characterClasses[entry.CharacterId] = entry.Player.CharacterClass;
        }
    }

    private static bool TryReadHistoricalCharacterClass(DestinyHistoricalStatsPerCharacter character, out string className)
    {
        className = "Unknown";
        foreach (var key in new[] { "characterClass", "className", "class", "classType" })
        {
            if (!character.AdditionalProperties.TryGetValue(key, out var value))
            {
                continue;
            }

            className = int.TryParse(value?.ToString(), out var classType)
                ? ClassName(classType)
                : ClassName(value?.ToString() ?? "");

            if (className != "Unknown")
            {
                return true;
            }
        }

        return false;
    }

    private static string ClassName(string className)
    {
        if (className.Equals("Titan", StringComparison.OrdinalIgnoreCase))
        {
            return "Titan";
        }

        if (className.Equals("Hunter", StringComparison.OrdinalIgnoreCase))
        {
            return "Hunter";
        }

        if (className.Equals("Warlock", StringComparison.OrdinalIgnoreCase))
        {
            return "Warlock";
        }

        return "Unknown";
    }

    private static string ClassName(int classType) => classType switch
    {
        0 => "Titan",
        1 => "Hunter",
        2 => "Warlock",
        _ => "Unknown"
    };

    private static Dictionary<long, string> NormalizeCharacterClassMap(IReadOnlyDictionary<long, string> characterClassById)
    {
        return characterClassById.ToDictionary(
            item => item.Key,
            item => ClassName(item.Value));
    }

    private static List<CharacterPlaytimeReport> BuildCharacterPlaytime(
        IEnumerable<DestinyHistoricalStatsPerCharacter> historicalCharacters,
        IReadOnlyDictionary<long, string> characterClassById,
        IReadOnlyDictionary<long, string> recoveredRaceById,
        IEnumerable<DestinyCharacterComponent> profileCharacters)
    {
        var normalizedClassById = NormalizeCharacterClassMap(characterClassById);
        var currentCharacters = profileCharacters.ToDictionary(character => character.CharacterId);
        var historical = historicalCharacters.ToDictionary(character => character.CharacterId);
        var characterIds = historical.Keys.Concat(currentCharacters.Keys).Distinct();

        return characterIds.Select(characterId =>
            {
                currentCharacters.TryGetValue(characterId, out var current);
                historical.TryGetValue(characterId, out var history);
                var playtime = current is not null
                    ? TimeSpan.FromMinutes(current.MinutesPlayedTotal)
                    : TimeSpan.FromSeconds(GetStat(history?.Merged?.AllTime, "secondsPlayed"));
                return new CharacterPlaytimeReport
                {
                    Race = current is null ? recoveredRaceById.GetValueOrDefault(characterId, "Unknown") : RaceName(current.RaceType),
                    Class = current is null ? normalizedClassById.GetValueOrDefault(characterId, "Unknown") : ClassName(current.ClassType),
                    IsDeleted = current is null || history?.Deleted == true,
                    Playtime = playtime
                };
            })
            .OrderByDescending(character => character.Playtime)
            .ToList();
    }

    private static string RaceName(int raceType) => raceType switch
    {
        0 => "Human",
        1 => "Awoken",
        2 => "Exo",
        _ => "Unknown"
    };

    private static string RaceName(string raceName)
    {
        if (raceName.Equals("Human", StringComparison.OrdinalIgnoreCase))
        {
            return "Human";
        }

        if (raceName.Equals("Awoken", StringComparison.OrdinalIgnoreCase))
        {
            return "Awoken";
        }

        if (raceName.Equals("Exo", StringComparison.OrdinalIgnoreCase))
        {
            return "Exo";
        }

        return "Unknown";
    }

    private static CharacterIdentity? ReadCharacterIdentityFromPgcr(
        DestinyPostGameCarnageReportData pgcr,
        long playerMembershipId,
        long characterId,
        IReadOnlyDictionary<string, ManifestCharacterIdentityDefinition> classDefinitions,
        IReadOnlyDictionary<string, ManifestCharacterIdentityDefinition> raceDefinitions)
    {
        var entry = (pgcr.Entries ?? [])
            .FirstOrDefault(item =>
                item.CharacterId == characterId
                && item.Player?.DestinyUserInfo?.MembershipId == playerMembershipId);
        if (entry?.Player is null)
        {
            return null;
        }

        var className = ClassName(entry.Player.CharacterClass ?? "");
        if (className == "Unknown")
        {
            var classDefinition = GetDefinition(classDefinitions, entry.Player.ClassHash);
            className = ClassName(classDefinition?.DisplayProperties?.Name ?? "");
        }

        var raceDefinition = GetDefinition(raceDefinitions, entry.Player.RaceHash);
        var raceName = RaceName(raceDefinition?.DisplayProperties?.Name ?? "");
        return className == "Unknown" && raceName == "Unknown"
            ? null
            : new CharacterIdentity(className, raceName);
    }
}
