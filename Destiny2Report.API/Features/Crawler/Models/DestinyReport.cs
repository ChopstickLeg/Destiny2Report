using System.Net;

namespace Destiny2Report.API.Features.Crawler.Models;

public record DestinyReport
{
    int PlatformId { get; init; }
    long PlayerMembershipId { get; init; }
    TimeSpan TotalPlaytime { get; set; }
    Dictionary<string, TimeSpan> PlaytimeByClass { get; set; } = new();
    Dictionary<string, TimeSpan> PlaytimeByActivity { get; set; } = new();
    Dictionary<string, TimeSpan> PatrolTimeByPlanet { get; set; } = new();
    long TotalKills { get; set; }
    long TotalDeaths { get; set; }
    double CrucibleKd { get; set; }
    double CrucibleKda { get; set; }
    double GambitKd { get; set; }
    double GambitKda { get; set; }
    int CrucibleMatchesPlayed { get; set; }
    int GambitMatchesPlayed { get; set; }
    int CrucibleWins { get; set; }
    int GambitWins { get; set; }
    DestinyPlayer CrucibleRival { get; set; }
    DestinyPlayer GambitRival { get; set; }
    double KdAgainstCrucibleRival { get; set; }
    double KdAgainstGambitRival { get; set; }
    int GambitMotesBanked { get; set; }
    int GambitMotesLost { get; set; }
    List<DestinyTriumphSeal> TriumphSeals { get; set; } = new();
    int Misadventures { get; set; }
    int ZeroKillActivities { get; set; }
    TimeSpan NonActivityTime { get; set; }
    List<ActivityCompletion> RaidCompletions { get; set; } = new();
    List<ActivityCompletion> DungeonCompletions { get; set; } = new();
    Dictionary<int, List<DestinyPlayer>> MostPlayedWith { get; set; } = new();
    Dictionary<int, List<DestinyPlayer>> MostPlayedWithRaid { get; set; } = new();
    int UniquePlayersPlayedWith { get; set; }
    List<(DestinyPlayer Player, ActivityCompletion ActivityCompletion)> PlayersSherpaed { get; set; } = new();
    List<WeaponReport> PvETopWeapons { get; set; } = new();
    List<WeaponReport> CrucibleTopWeapons { get; set; } = new();
    List<WeaponReport> GambitTopWeapons { get; set; } = new();
}

public record DestinyTriumphSeal
{
    string Name { get; init; }
    string Description { get; init; }
    string IconUrl { get; init; }
    List<DestinyTriumph> Triumphs { get; init; } = new();
}

public record DestinyTriumph
{
    string Name { get; init; }
    string Description { get; init; }
    string IconUrl { get; init; }
    int Points { get; init; }
    bool IsCompleted { get; init; }
}

public record ActivityCompletion
{
    string RaidName { get; init; }
    DateTime CompletionDate { get; init; }
    bool? isContest { get; init; }
    bool? IsDayOne { get; init; }
    bool? isFlawless { get; init; }
}

public record DestinyPlayer
{
    long MembershipId { get; init; }
    int MembershipType { get; init; }
    string DisplayName { get; init; }
    string EmblemUrl { get; init; }
}

public record WeaponReport
{
    string Name { get; init; }
    string IconUrl { get; init; }
    int TotalKills { get; init; }
}