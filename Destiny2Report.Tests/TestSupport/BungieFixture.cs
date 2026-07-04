using D2Report.BungieClient;

namespace Destiny2Report.Tests.TestSupport;

internal static class BungieFixture
{
    public static DestinyHistoricalStatsValue Stat(double value)
    {
        return new DestinyHistoricalStatsValue
        {
            Basic = new Basic
            {
                Value = value,
                DisplayValue = value.ToString()
            }
        };
    }

    public static Dictionary<string, DestinyHistoricalStatsValue> Stats(params (string Id, double Value)[] values)
    {
        return values.ToDictionary(item => item.Id, item => Stat(item.Value));
    }

    public static DestinyHistoricalStatsByPeriod Bucket(params (string Id, double Value)[] values)
    {
        return new DestinyHistoricalStatsByPeriod
        {
            AllTime = Stats(values)
        };
    }

    public static DestinyHistoricalStatsPerCharacter HistoricalCharacter(
        long characterId,
        IDictionary<string, DestinyHistoricalStatsByPeriod>? results = null,
        DestinyHistoricalStatsByPeriod? merged = null)
    {
        return new DestinyHistoricalStatsPerCharacter
        {
            CharacterId = characterId,
            Results = results ?? new Dictionary<string, DestinyHistoricalStatsByPeriod>(),
            Merged = merged ?? Bucket()
        };
    }

    public static DestinyPostGameCarnageReportData Pgcr(
        DateTimeOffset period,
        int mode,
        long instanceId,
        params DestinyPostGameCarnageReportEntry[] entries)
    {
        return new DestinyPostGameCarnageReportData
        {
            Period = period,
            ActivityDetails = new ActivityDetails
            {
                InstanceId = instanceId,
                Mode = mode,
                Modes = [mode],
                ReferenceId = 1000 + mode,
                DirectorActivityHash = 2000 + mode
            },
            Entries = entries
        };
    }

    public static DestinyHistoricalStatsPeriodGroup Activity(
        DateTimeOffset period,
        int mode,
        long instanceId,
        int referenceId,
        int directorActivityHash,
        params (string Id, double Value)[] values)
    {
        return new DestinyHistoricalStatsPeriodGroup
        {
            Period = period,
            ActivityDetails = new ActivityDetails2
            {
                InstanceId = instanceId,
                Mode = mode,
                Modes = [mode],
                ReferenceId = referenceId,
                DirectorActivityHash = directorActivityHash
            },
            Values = Stats(values)
        };
    }

    public static DestinyPostGameCarnageReportEntry Entry(
        long membershipId,
        int membershipType = 1,
        long characterId = 1,
        int standing = 0,
        int emblemHash = 0,
        string displayName = "Player",
        string characterClass = "Warlock",
        IDictionary<string, DestinyHistoricalStatsValue>? values = null,
        Extended? extended = null)
    {
        return new DestinyPostGameCarnageReportEntry
        {
            CharacterId = characterId,
            Standing = standing,
            Player = new Player
            {
                CharacterClass = characterClass,
                EmblemHash = emblemHash,
                DestinyUserInfo = new DestinyUserInfo
                {
                    MembershipId = membershipId,
                    MembershipType = membershipType,
                    DisplayName = displayName,
                    BungieGlobalDisplayName = displayName
                }
            },
            Values = values ?? Stats(),
            Extended = extended
        };
    }

    public static DestinyHistoricalWeaponStats Weapon(int referenceId, params (string Id, double Value)[] values)
    {
        return new DestinyHistoricalWeaponStats
        {
            ReferenceId = referenceId,
            Values = Stats(values)
        };
    }
}
