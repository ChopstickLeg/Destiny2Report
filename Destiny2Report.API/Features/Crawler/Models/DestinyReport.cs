using System.Net;

namespace Destiny2Report.API.Features.Crawler.Models;

public record DestinyReport
{
    public int PlatformId { get; init; }
    public long PlayerMembershipId { get; init; }
    public DateTimeOffset CrawledAt { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan TotalPlaytime { get; set; }
    public Dictionary<string, TimeSpan> PlaytimeByClass { get; set; } = new();
    public Dictionary<string, TimeSpan> PlaytimeByActivity { get; set; } = new();
    public Dictionary<string, TimeSpan> PatrolTimeByPlanet { get; set; } = new();
    public int GoodBoyProtocol { get; set; }
    public int FishCaught { get; set; }
    public long TotalKills { get; set; }
    public long TotalDeaths { get; set; }
    public double CrucibleKd { get; set; }
    public double CrucibleKda { get; set; }
    public double GambitKd { get; set; }
    public double GambitKda { get; set; }
    public int CrucibleMatchesPlayed { get; set; }
    public int GambitMatchesPlayed { get; set; }
    public int CrucibleWins { get; set; }
    public int GambitWins { get; set; }
    public DestinyPlayer? CrucibleRival { get; set; }
    public DestinyPlayer? GambitRival { get; set; }
    public double KdAgainstCrucibleRival { get; set; }
    public double KdAgainstGambitRival { get; set; }
    public int GambitMotesBanked { get; set; }
    public int GambitMotesLost { get; set; }
    public List<DestinyTriumphSeal> TriumphSeals { get; set; } = new();
    public int Misadventures { get; set; }
    public int ZeroKillActivities { get; set; }
    public TimeSpan NonActivityTime { get; set; }
    public List<ActivityCompletion> RaidCompletions { get; set; } = new();
    public List<ActivityCompletion> DungeonCompletions { get; set; } = new();
    public List<ActivityCompletion> DayOneRaidCompletions { get; set; } = new();
    public List<ActivityCompletion> DayOneDungeonCompletions { get; set; } = new();
    public ActivityCompletion? FirstRaidFlawless { get; set; }
    public ActivityCompletion? FirstDungeonFlawless { get; set; }
    public ActivityCompletion? FirstDungeonSoloFlawless { get; set; }
    public Dictionary<int, List<DestinyPlayer>> MostPlayedWith { get; set; } = new();
    public Dictionary<int, List<DestinyPlayer>> MostPlayedWithRaid { get; set; } = new();
    public int UniquePlayersPlayedWith { get; set; }
    public List<SherpaReport> PlayersSherpaed { get; set; } = new();
    public List<WeaponReport> PvETopWeapons { get; set; } = new();
    public List<WeaponReport> CrucibleTopWeapons { get; set; } = new();
    public List<WeaponReport> GambitTopWeapons { get; set; } = new();
}

public record DestinyTriumphSeal
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public List<DestinyTriumph> Triumphs { get; init; } = new();
}

public record DestinyTriumph
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public int Points { get; init; }
    public bool IsCompleted { get; init; }
}

public record ActivityCompletion
{
    public string RaidName { get; init; } = "";
    public DateTime CompletionDate { get; init; }
    public bool? IsContest { get; init; }
    public bool? IsDayOne { get; init; }
    public bool? IsFlawless { get; init; }
    public bool? IsSolo { get; init; }
    public long InstanceId { get; init; }
}

public record DestinyPlayer
{
    public long MembershipId { get; init; }
    public int MembershipType { get; init; }
    public string DisplayName { get; init; } = "";
    public string EmblemUrl { get; init; } = "";
}

public record WeaponReport
{
    public string Name { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public int TotalKills { get; init; }
}

public record SherpaReport
{
    public DestinyPlayer Player { get; init; } = new();
    public ActivityCompletion ActivityCompletion { get; init; } = new();
}
