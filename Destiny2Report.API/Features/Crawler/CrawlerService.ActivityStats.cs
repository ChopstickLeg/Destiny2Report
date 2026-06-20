using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;
using ReportPlayer = Destiny2Report.API.Features.Crawler.Models.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private async Task ApplyActivityDerivedStatsAsync(
        DestinyReport report,
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<DestinyHistoricalStatsPeriodGroup> activityHistory,
        IReadOnlyDictionary<long, DestinyPostGameCarnageReportData> pgcrs,
        ManifestContext manifest,
        CancellationToken cancellationToken)
    {
        var activityDefinitions = await manifest.GetTableAsync("DestinyActivityDefinition", cancellationToken).ConfigureAwait(false);
        var destinationDefinitions = await manifest.GetTableAsync("DestinyDestinationDefinition", cancellationToken).ConfigureAwait(false);

        ApplyPatrolTime(report, activityHistory.Where(activity => IncludesMode(activity, ActivityModes.Patrol)), activityDefinitions, destinationDefinitions);
        await ApplyPgcrAggregatesAsync(
                report,
                platformId,
                playerMembershipId,
                activityHistory,
                pgcrs,
                activityDefinitions,
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ApplyPatrolTime(
        DestinyReport report,
        IEnumerable<DestinyHistoricalStatsPeriodGroup> patrolActivities,
        JObject activityDefinitions,
        JObject destinationDefinitions)
    {
        foreach (var activity in patrolActivities)
        {
            var activityDefinition = GetDefinition(activityDefinitions, activity.ActivityDetails.ReferenceId)
                ?? GetDefinition(activityDefinitions, activity.ActivityDetails.DirectorActivityHash);
            var destinationHash = activityDefinition?["destinationHash"]?.Value<long>() ?? 0;
            var destination = GetDefinition(destinationDefinitions, destinationHash);
            var destinationName = destination?["displayProperties"]?["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(destinationName))
            {
                destinationName = destinationHash > 0 ? destinationHash.ToString() : "Unknown";
            }

            var seconds = GetStat(activity.Values, "timePlayedSeconds");
            if (seconds <= 0)
            {
                seconds = GetStat(activity.Values, "activityDurationSeconds");
            }

            report.PatrolTimeByPlanet[destinationName] = report.PatrolTimeByPlanet.GetValueOrDefault(destinationName) + TimeSpan.FromSeconds(seconds);
        }
    }

    private async Task ApplyPgcrAggregatesAsync(
        DestinyReport report,
        int platformId,
        long playerMembershipId,
        IReadOnlyCollection<DestinyHistoricalStatsPeriodGroup> activityHistory,
        IReadOnlyDictionary<long, DestinyPostGameCarnageReportData> pgcrs,
        JObject activityDefinitions,
        ManifestContext manifest,
        CancellationToken cancellationToken)
    {
        var allPgcrs = pgcrs.Values.OrderBy(pgcr => pgcr.Period).ToArray();
        var playerEncounterCounts = new Dictionary<(int MembershipType, long MembershipId), int>();
        var pvpOpponents = new Dictionary<long, RivalAggregate>();
        var gambitOpponents = new Dictionary<long, RivalAggregate>();
        var pveWeapons = new Dictionary<int, int>();
        var pvpWeapons = new Dictionary<int, int>();
        var gambitWeapons = new Dictionary<int, int>();
        var raidCompletions = new Dictionary<string, ActivityCompletionAggregate>(StringComparer.OrdinalIgnoreCase);
        var dungeonCompletions = new Dictionary<string, ActivityCompletionAggregate>(StringComparer.OrdinalIgnoreCase);
        var playersSherpaed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pendingSherpaChecks = new List<SherpaCheck>();
        var completedRaidHistoryByPlayer = new ConcurrentDictionary<(int MembershipType, long MembershipId), Lazy<Task<IReadOnlyCollection<CompletedRaidActivity>?>>>();
        var membershipTypeByPlayer = new ConcurrentDictionary<long, Lazy<Task<int?>>>();
        var activityTime = new TimeSpan();

        foreach (var pgcr in allPgcrs)
        {
            var playerEntry = FindPlayerEntry(pgcr, playerMembershipId);
            if (playerEntry is null)
            {
                continue;
            }

            var completionReason = GetPgcrCompletionReason(pgcr);
            var playerCompleted = IsNormallyCompleted(playerEntry.Values, completionReason);
            var playerKills = GetStat(playerEntry.Values, "kills");
            var playerDeaths = GetStat(playerEntry.Values, "deaths");
            if (playerKills <= 0)
            {
                report.ZeroKillActivities++;
            }

            var activityName = ActivityName(activityDefinitions, pgcr.ActivityDetails.ReferenceId, pgcr.ActivityDetails.DirectorActivityHash);
            var isRaid = IncludesMode(pgcr, ActivityModes.Raid);
            var isDungeon = IncludesMode(pgcr, ActivityModes.Dungeon);
            var isPvp = IncludesMode(pgcr, ActivityModes.AllPvP);
            var isGambit = IncludesMode(pgcr, ActivityModes.Gambit) || IncludesMode(pgcr, ActivityModes.GambitPrime);
            var isPrivateCrucible = isPvp && HasActivityTypeHash(pgcr, activityDefinitions, PrivateCrucibleActivityTypeHashes);
            var isPrivateGambit = isGambit && HasActivityTypeHash(pgcr, activityDefinitions, PrivateGambitActivityTypeHashes);
            var activityPlayerEntries = GetActivityPlayerEntries(pgcr);
            var activityWasStartedFromBeginning = GetActivityWasStartedFromBeginning(pgcr, activityPlayerEntries);
            var wasStartedFromBeginning = activityWasStartedFromBeginning == true;
            var isFlawless = (isRaid || isDungeon) && playerCompleted && wasStartedFromBeginning && activityPlayerEntries.Length > 0 && activityPlayerEntries.All(entry => GetStat(entry.Values, "deaths") <= 0);
            var isSolo = (isRaid || isDungeon) && playerCompleted && wasStartedFromBeginning && activityPlayerEntries.Select(entry => entry.Player?.DestinyUserInfo?.MembershipId).Distinct().Count() == 1;
            var activityCompletedAt = GetActivityCompletedAt(pgcr, playerEntry);
            var isContest = IsContest(pgcr, activityCompletedAt, isRaid, isDungeon);
            var isSoloFlawless = isSolo && isFlawless;
            var playerActivitySeconds = GetStat(playerEntry.Values, "timePlayedSeconds");
            if (playerActivitySeconds <= 0)
            {
                playerActivitySeconds = GetStat(playerEntry.Values, "activityDurationSeconds");
            }

            activityTime += TimeSpan.FromSeconds(playerActivitySeconds);

            if (playerCompleted && isRaid)
            {
                AddCompletion(raidCompletions, activityName, isContest, isFlawless, isSolo, isSoloFlawless);
                var normalizedRaidName = ContestModeLookup.NormalizeActivityName(activityName);
                if (HasPriorCompletedRaid(activityHistory, normalizedRaidName, activityCompletedAt, pgcr.ActivityDetails.InstanceId, activityDefinitions))
                {
                    pendingSherpaChecks.Add(new SherpaCheck(pgcr, normalizedRaidName, activityCompletedAt));
                }
            }

            if (playerCompleted && isDungeon)
            {
                AddCompletion(dungeonCompletions, activityName, isContest, isFlawless, isSolo, isSoloFlawless);
            }

            var otherPlayers = (pgcr.Entries ?? [])
                .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId is > 0)
                .Where(entry => entry.Player.DestinyUserInfo.MembershipId != playerMembershipId)
                .Select(entry => entry.Player.DestinyUserInfo)
                .GroupBy(player => (player.MembershipType, player.MembershipId))
                .Select(group => group.First())
                .ToArray();

            foreach (var otherPlayer in otherPlayers)
            {
                var key = (otherPlayer.MembershipType, otherPlayer.MembershipId);
                playerEncounterCounts[key] = playerEncounterCounts.GetValueOrDefault(key) + 1;
            }

            if (isPvp)
            {
                if (!isPrivateCrucible)
                {
                    TrackRivals(pvpOpponents, pgcr, playerEntry, playerMembershipId, playerKills, playerDeaths);
                    AddWeapons(pvpWeapons, playerEntry);
                }
            }
            else if (isGambit)
            {
                if (!isPrivateGambit)
                {
                    TrackRivals(gambitOpponents, pgcr, playerEntry, playerMembershipId, playerKills, playerDeaths);
                    AddWeapons(gambitWeapons, playerEntry);
                    report.GambitMotesBanked += (int)GetMoteStat(playerEntry, "bank", "deposit");
                    report.GambitMotesLost += (int)GetMoteStat(playerEntry, "lost");
                }
            }
            else
            {
                AddWeapons(pveWeapons, playerEntry);
            }
        }

        await ApplySherpaChecksAsync(cancellationToken).ConfigureAwait(false);

        report.RaidCompletions = ToCompletionSummaries(raidCompletions);
        report.DungeonCompletions = ToCompletionSummaries(dungeonCompletions);
        report.PlayersSherpaed = ToSherpaReports(playersSherpaed);
        await ApplyPlayerEncounterCountsAsync(
                report,
                platformId,
                playerMembershipId,
                playerEncounterCounts,
                cancellationToken)
            .ConfigureAwait(false);

        ApplyRival(report, pvpOpponents, isGambit: false);
        ApplyRival(report, gambitOpponents, isGambit: true);

        var weaponDefinitions = await GetInventoryItemSummariesAsync(
                manifest.Manifest,
                TopWeaponHashes(pveWeapons).Concat(TopWeaponHashes(pvpWeapons)).Concat(TopWeaponHashes(gambitWeapons)),
                cancellationToken)
            .ConfigureAwait(false);

        report.PvETopWeapons = BuildWeaponReports(pveWeapons, weaponDefinitions);
        report.CrucibleTopWeapons = BuildWeaponReports(pvpWeapons, weaponDefinitions);
        report.GambitTopWeapons = BuildWeaponReports(gambitWeapons, weaponDefinitions);

        report.TotalActivityTime = activityTime;

        async Task<IReadOnlyCollection<CompletedRaidActivity>?> GetCompletedRaidHistoryAsync(int membershipType, long membershipId)
        {
            var lazyHistory = completedRaidHistoryByPlayer.GetOrAdd(
                (membershipType, membershipId),
                key => new Lazy<Task<IReadOnlyCollection<CompletedRaidActivity>?>>(
                    () => FetchCompletedRaidHistoryAsync(
                        key.MembershipType,
                        key.MembershipId,
                        activityDefinitions,
                        cancellationToken)));

            return await lazyHistory.Value.ConfigureAwait(false);
        }

        async Task ApplySherpaChecksAsync(CancellationToken cancellationToken)
        {
            var unresolvedCandidateChecks = pendingSherpaChecks
                .SelectMany(check => GetCompletedFireteamMembers(check.Pgcr, playerMembershipId)
                    .Select(player => new SherpaCandidateCheck(
                        check.Pgcr,
                        check.NormalizedRaidName,
                        check.CompletedAt,
                        player.MembershipType,
                        player.MembershipId)))
                .ToArray();

            var resolvedCandidateChecks = await Task.WhenAll(unresolvedCandidateChecks.Select(ResolveCandidateCheckAsync))
                .ConfigureAwait(false);
            var candidateChecks = resolvedCandidateChecks.OfType<SherpaCandidateCheck>().ToArray();

            if (candidateChecks.Length == 0)
            {
                return;
            }

            var candidatePlayers = candidateChecks
                .Select(check => (check.MembershipType, check.MembershipId))
                .Distinct()
                .ToArray();

            using var throttler = new SemaphoreSlim(MaxConcurrentSherpaHistoryRequests);
            var historyTasks = candidatePlayers.Select(async player =>
            {
                await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var history = await GetCompletedRaidHistoryAsync(player.MembershipType, player.MembershipId).ConfigureAwait(false);
                    return (player.MembershipType, player.MembershipId, History: history);
                }
                finally
                {
                    throttler.Release();
                }
            });

            var histories = await Task.WhenAll(historyTasks).ConfigureAwait(false);
            var historyByPlayer = histories.ToDictionary(
                item => (item.MembershipType, item.MembershipId),
                item => item.History);

            foreach (var check in candidateChecks)
            {
                if (!historyByPlayer.TryGetValue((check.MembershipType, check.MembershipId), out var history)
                    || history is null
                    || history.Any(activity => IsPriorCompletedRaid(
                        activity,
                        check.NormalizedRaidName,
                        check.CompletedAt,
                        check.Pgcr.ActivityDetails.InstanceId)))
                {
                    continue;
                }

                playersSherpaed[check.NormalizedRaidName] = playersSherpaed.GetValueOrDefault(check.NormalizedRaidName) + 1;
            }
        }

        async Task<SherpaCandidateCheck?> ResolveCandidateCheckAsync(SherpaCandidateCheck check)
        {
            if (check.MembershipType > 0)
            {
                return check;
            }

            var resolvedMembershipType = await ResolveMembershipTypeAsync(check.MembershipId).ConfigureAwait(false);
            if (resolvedMembershipType is not > 0)
            {
                logger.LogDebug(
                    "Skipping sherpa candidate {MembershipId} because their membership type could not be resolved.",
                    check.MembershipId);
                return null;
            }

            return check with { MembershipType = resolvedMembershipType.Value };
        }

        async Task<int?> ResolveMembershipTypeAsync(long membershipId)
        {
            var lazyMembershipType = membershipTypeByPlayer.GetOrAdd(
                membershipId,
                key => new Lazy<Task<int?>>(() => FetchMembershipTypeAsync(key, cancellationToken)));

            return await lazyMembershipType.Value.ConfigureAwait(false);
        }

        async Task<int?> FetchMembershipTypeAsync(long membershipId, CancellationToken cancellationToken)
        {
            try
            {
                var response = await bungieClient.Destiny2_GetLinkedProfilesAsync(
                        getAllMemberships: true,
                        membershipId: membershipId,
                        membershipType: AllMembershipTypes,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var payload = EnsureSuccess(response, item => item.Response, $"GetLinkedProfiles:{membershipId}");
                return SelectLinkedProfileMembershipType(payload.Profiles, membershipId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not resolve membership type for sherpa candidate {MembershipId}.", membershipId);
                return null;
            }
        }

        IEnumerable<(int MembershipType, long MembershipId)> GetCompletedFireteamMembers(
            DestinyPostGameCarnageReportData raidPgcr,
            long ownerMembershipId)
        {
            return GetActivityPlayerEntries(raidPgcr)
                .Where(entry => IsNormallyCompleted(entry.Values, GetPgcrCompletionReason(raidPgcr)))
                .Select(entry => entry.Player?.DestinyUserInfo)
                .Where(player => player?.MembershipId > 0)
                .Where(player => player!.MembershipId != ownerMembershipId)
                .GroupBy(player => player!.MembershipId)
                .Select(group => (
                    MembershipType: group.Select(player => player!.MembershipType).FirstOrDefault(membershipType => membershipType > 0),
                    MembershipId: group.Key));
        }
    }

    private static DestinyPostGameCarnageReportEntry? FindPlayerEntry(DestinyPostGameCarnageReportData pgcr, long membershipId)
    {
        return pgcr.Entries?.FirstOrDefault(entry => entry.Player?.DestinyUserInfo?.MembershipId == membershipId);
    }

    private static bool IncludesMode(DestinyPostGameCarnageReportData pgcr, int mode)
    {
        return pgcr.ActivityDetails.Mode == mode || (pgcr.ActivityDetails.Modes?.Contains(mode) ?? false);
    }

    private static bool IncludesMode(DestinyHistoricalStatsPeriodGroup activity, int mode)
    {
        return activity.ActivityDetails.Mode == mode || (activity.ActivityDetails.Modes?.Contains(mode) ?? false);
    }

    private static bool HasActivityTypeHash(
        DestinyPostGameCarnageReportData pgcr,
        JObject activityDefinitions,
        IReadOnlySet<long> activityTypeHashes)
    {
        var definition = GetDefinition(activityDefinitions, pgcr.ActivityDetails.ReferenceId)
            ?? GetDefinition(activityDefinitions, pgcr.ActivityDetails.DirectorActivityHash);
        var activityTypeHash = definition?["activityTypeHash"]?.Value<long>() ?? 0;
        return activityTypeHashes.Contains(activityTypeHash);
    }

    private static bool? GetActivityWasStartedFromBeginning(
        DestinyPostGameCarnageReportData pgcr,
        IReadOnlyCollection<DestinyPostGameCarnageReportEntry> activityPlayerEntries)
    {
        if (pgcr.Period >= SeasonOfTheHauntedRelease)
        {
            return pgcr.ActivityWasStartedFromBeginning;
        }

        if (pgcr.Period < BeyondLightRelease)
        {
            if (pgcr.StartingPhaseIndex is not { } startingPhaseIndex)
            {
                return null;
            }

            var activityHash = ToUnsignedHash(pgcr.ActivityDetails.DirectorActivityHash);
            if (ScourgeOfThePastActivityHashes.Contains(activityHash))
            {
                return startingPhaseIndex <= 1;
            }

            if (LeviathanActivityHashes.Contains(activityHash))
            {
                return startingPhaseIndex is 0 or 2;
            }

            return startingPhaseIndex == 0;
        }

        if (pgcr.Period >= WitchQueenRelease)
        {
            var deathless = activityPlayerEntries.Count > 0
                && activityPlayerEntries.All(entry => GetStat(entry.Values, "deaths") <= 0);
            if (pgcr.ActivityWasStartedFromBeginning == true || deathless)
            {
                return pgcr.ActivityWasStartedFromBeginning;
            }
        }

        return null;
    }

    private static string ActivityName(JObject definitions, int referenceId, int directorActivityHash)
    {
        var definition = GetDefinition(definitions, referenceId) ?? GetDefinition(definitions, directorActivityHash);
        return definition?["displayProperties"]?["name"]?.Value<string>() ?? referenceId.ToString();
    }

    private static long ToUnsignedHash(int hash)
    {
        return unchecked((uint)hash);
    }

    private bool IsContest(
        DestinyPostGameCarnageReportData pgcr,
        DateTimeOffset activityCompletedAt,
        bool isRaid,
        bool isDungeon)
    {
        var contestWindows = isRaid
            ? contestMode.Raids
            : isDungeon
                ? contestMode.Dungeons
                : null;
        if (contestWindows is null)
        {
            return false;
        }

        return IsContestActivityHash(pgcr.ActivityDetails.ReferenceId, activityCompletedAt, contestWindows)
            || IsContestActivityHash(pgcr.ActivityDetails.DirectorActivityHash, activityCompletedAt, contestWindows);
    }

    private static bool IsContestActivityHash(
        long activityHash,
        DateTimeOffset activityCompletedAt,
        IReadOnlyDictionary<long, IReadOnlyCollection<ContestModeActivityWindow>> contestWindows)
    {
        return contestWindows.TryGetValue(activityHash, out var windows)
            && windows.Any(window => activityCompletedAt >= window.Start && activityCompletedAt < window.End);
    }

    private static DateTimeOffset GetActivityCompletedAt(
        DestinyPostGameCarnageReportData pgcr,
        DestinyPostGameCarnageReportEntry playerEntry)
    {
        var durationSeconds = GetStat(playerEntry.Values, "activityDurationSeconds");
        return durationSeconds > 0
            ? pgcr.Period.AddSeconds(durationSeconds)
            : pgcr.Period;
    }

    private static DateTimeOffset GetActivityCompletedAt(DestinyHistoricalStatsPeriodGroup activity)
    {
        var durationSeconds = GetStat(activity.Values, "activityDurationSeconds");
        return durationSeconds > 0
            ? activity.Period.AddSeconds(durationSeconds)
            : activity.Period;
    }

    private static bool IsNormallyCompleted(IDictionary<string, DestinyHistoricalStatsValue>? values)
    {
        return GetStat(values, "completed") > 0
            && GetStat(values, "completionReason") == 0;
    }

    private static bool IsNormallyCompleted(
        IDictionary<string, DestinyHistoricalStatsValue>? values,
        double completionReason)
    {
        return GetStat(values, "completed") > 0
            && completionReason == 0;
    }

    private static double GetPgcrCompletionReason(DestinyPostGameCarnageReportData pgcr)
    {
        return GetStat(pgcr.Entries?.FirstOrDefault()?.Values, "completionReason");
    }

    private static int? SelectLinkedProfileMembershipType(
        IEnumerable<DestinyProfileUserInfoCard>? profiles,
        long membershipId)
    {
        var matchingProfiles = (profiles ?? [])
            .Where(profile => profile.MembershipId == membershipId && profile.MembershipType > 0)
            .OrderByDescending(profile => profile.IsCrossSavePrimary)
            .ThenBy(profile => profile.IsOverridden)
            .ThenByDescending(profile => profile.DateLastPlayed)
            .ToArray();

        return matchingProfiles.Length > 0
            ? matchingProfiles[0].MembershipType
            : null;
    }

    private static DestinyPostGameCarnageReportEntry[] GetActivityPlayerEntries(DestinyPostGameCarnageReportData pgcr)
    {
        return (pgcr.Entries ?? [])
            .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId > 0)
            .ToArray();
    }

    private static void AddCompletion(
        IDictionary<string, ActivityCompletionAggregate> completions,
        string activityName,
        bool contestClear,
        bool flawlessClear,
        bool soloClear,
        bool soloFlawlessClear)
    {
        var key = ContestModeLookup.NormalizeActivityName(activityName);
        if (!completions.TryGetValue(key, out var completion))
        {
            completion = new ActivityCompletionAggregate(key);
            completions[key] = completion;
        }

        completion.CompletionCount++;
        completion.ContestClear |= contestClear;
        completion.FlawlessClear |= flawlessClear;
        completion.SoloClear |= soloClear;
        completion.SoloFlawlessClear |= soloFlawlessClear;
    }

    private static List<ActivityCompletionSummary> ToCompletionSummaries(
        IDictionary<string, ActivityCompletionAggregate> completions)
    {
        return completions.Values
            .OrderBy(completion => completion.ActivityName, StringComparer.OrdinalIgnoreCase)
            .Select(completion => new ActivityCompletionSummary
            {
                ActivityName = completion.ActivityName,
                CompletionCount = completion.CompletionCount,
                ContestClear = completion.ContestClear,
                FlawlessClear = completion.FlawlessClear,
                SoloClear = completion.SoloClear,
                SoloFlawlessClear = completion.SoloFlawlessClear
            })
            .ToList();
    }

    private static bool HasPriorCompletedRaid(
        IEnumerable<DestinyHistoricalStatsPeriodGroup> activities,
        string normalizedRaidName,
        DateTimeOffset activityCompletedAt,
        long activityInstanceId,
        JObject activityDefinitions)
    {
        return activities
            .Where(activity => IsNormallyCompleted(activity.Values))
            .Where(activity => IncludesMode(activity, ActivityModes.Raid))
            .Any(activity =>
            {
                var raidName = ActivityName(
                    activityDefinitions,
                    activity.ActivityDetails.ReferenceId,
                    activity.ActivityDetails.DirectorActivityHash);
                var completedRaid = new CompletedRaidActivity(
                    ContestModeLookup.NormalizeActivityName(raidName),
                    GetActivityCompletedAt(activity),
                    activity.ActivityDetails.InstanceId);

                return IsPriorCompletedRaid(completedRaid, normalizedRaidName, activityCompletedAt, activityInstanceId);
            });
    }

    private static bool IsPriorCompletedRaid(
        CompletedRaidActivity completedRaid,
        string normalizedRaidName,
        DateTimeOffset activityCompletedAt,
        long activityInstanceId)
    {
        return string.Equals(completedRaid.RaidName, normalizedRaidName, StringComparison.OrdinalIgnoreCase)
            && completedRaid.InstanceId != activityInstanceId
            && completedRaid.CompletedAt <= activityCompletedAt;
    }

    private static List<SherpaReport> ToSherpaReports(IDictionary<string, int> playersSherpaed)
    {
        return playersSherpaed
            .Where(item => item.Value > 0)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SherpaReport
            {
                RaidName = item.Key,
                PlayerCount = item.Value
            })
            .ToList();
    }
}
