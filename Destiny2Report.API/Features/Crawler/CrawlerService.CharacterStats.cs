using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
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

    private static Dictionary<string, TimeSpan> BuildPlaytimeByClass(
        IEnumerable<DestinyHistoricalStatsPerCharacter> historicalCharacters,
        IReadOnlyDictionary<long, string> characterClassById)
    {
        var playtimeByClass = new Dictionary<string, TimeSpan>();
        var normalizedClassById = NormalizeCharacterClassMap(characterClassById);

        foreach (var character in historicalCharacters)
        {
            var className = normalizedClassById.GetValueOrDefault(character.CharacterId, "Unknown");
            var seconds = GetStat(character.Merged?.AllTime, "secondsPlayed");
            playtimeByClass[className] = playtimeByClass.GetValueOrDefault(className) + TimeSpan.FromSeconds(seconds);
        }

        return playtimeByClass;
    }

    private static Dictionary<string, TimeSpan> BuildTotalPlaytimeByClass(IEnumerable<DestinyCharacterComponent> characters)
    {
        var playtimeByClass = new Dictionary<string, TimeSpan>();

        foreach (var character in characters)
        {
            var className = ClassName(character.ClassType);
            playtimeByClass[className] = playtimeByClass.GetValueOrDefault(className) + TimeSpan.FromMinutes(character.MinutesPlayedTotal);
        }

        return playtimeByClass;
    }
}
