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

public partial class CrawlerService(
    ILogger<CrawlerService> logger,
    IMongoDatabase mongoDatabase,
    ID2ReportClient bungieClient,
    HybridCache cache,
    IHttpClientFactory httpClientFactory,
    IOptions<ContestModeOptions> contestModeOptions,
    IOptions<ConquestOptions> conquestOptions,
    IOptions<ActivityTriumphRecordOptions> activityTriumphRecordOptions,
    CrawlerPgcrThrottler pgcrThrottler,
    CrawlerSherpaHistoryThrottler sherpaHistoryThrottler,
    IOptions<CrawlerOptions> crawlerOptions) : ICrawlerService
{
    private const string BungieNetBaseUrl = "https://www.bungie.net";
    private const int GeneralStatsGroup = 1;
    private const int BasicProfileComponent = 100;
    private const int ProfileCharactersComponent = 200;
    private const int ProfileRecordsComponent = 900;
    private const int MetricsComponent = 1100;
    private const int PageSize = 250;
    private const int RecentActivityInstanceIdLimit = 5000;
    private const int AllMembershipTypes = 254;
    private const string InventoryItemDefinitionType = "DestinyInventoryItemDefinition";

    private static readonly int[] AccountStatGroups = [GeneralStatsGroup];
    private static readonly int[] ProfileComponents = [BasicProfileComponent, ProfileRecordsComponent, MetricsComponent, ProfileCharactersComponent];
    private static readonly int[] ProfileCharactersComponents = [BasicProfileComponent, ProfileCharactersComponent];
    private static readonly int[] ModeStatGroups = [GeneralStatsGroup];
    private static readonly long[] TriumphSealRootPresentationNodeHashes = [616318467, 1881970629];
    private static readonly TimeSpan ManifestCacheDuration = TimeSpan.FromDays(1);
    private static readonly TimeSpan ManifestTableCacheDuration = TimeSpan.FromDays(365);
    private static readonly HashSet<long> PrivateGambitActivityTypeHashes = [146907730, 2516284680];
    private static readonly HashSet<long> PrivateCrucibleActivityTypeHashes = [4260058063];
    private static readonly int[] ActivityPlaytimeBroadModes =
    [
        ActivityModes.AllPvP,
        ActivityModes.AllPvE,
        ActivityModes.AllPvECompetitive
    ];
    private static readonly IReadOnlyDictionary<int, string> ActivityModeTypeNames = new Dictionary<int, string>
    {
        [0] = "None",
        [2] = "Story",
        [3] = "Strike",
        [4] = "Raid",
        [5] = "AllPvP",
        [6] = "Patrol",
        [7] = "AllPvE",
        [9] = "Reserved9",
        [10] = "Control",
        [11] = "Reserved11",
        [12] = "Clash",
        [13] = "Reserved13",
        [15] = "CrimsonDoubles",
        [16] = "Nightfall",
        [17] = "HeroicNightfall",
        [18] = "AllStrikes",
        [19] = "IronBanner",
        [20] = "Reserved20",
        [21] = "Reserved21",
        [22] = "Reserved22",
        [24] = "Reserved24",
        [25] = "AllMayhem",
        [26] = "Reserved26",
        [27] = "Reserved27",
        [28] = "Reserved28",
        [29] = "Reserved29",
        [30] = "Reserved30",
        [31] = "Supremacy",
        [32] = "PrivateMatchesAll",
        [37] = "Survival",
        [38] = "Countdown",
        [39] = "TrialsOfTheNine",
        [40] = "Social",
        [41] = "TrialsCountdown",
        [42] = "TrialsSurvival",
        [43] = "IronBannerControl",
        [44] = "IronBannerClash",
        [45] = "IronBannerSupremacy",
        [46] = "ScoredNightfall",
        [47] = "ScoredHeroicNightfall",
        [48] = "Rumble",
        [49] = "AllDoubles",
        [50] = "Doubles",
        [51] = "PrivateMatchesClash",
        [52] = "PrivateMatchesControl",
        [53] = "PrivateMatchesSupremacy",
        [54] = "PrivateMatchesCountdown",
        [55] = "PrivateMatchesSurvival",
        [56] = "PrivateMatchesMayhem",
        [57] = "PrivateMatchesRumble",
        [58] = "HeroicAdventure",
        [59] = "Showdown",
        [60] = "Lockdown",
        [61] = "Scorched",
        [62] = "ScorchedTeam",
        [63] = "Gambit",
        [64] = "AllPvECompetitive",
        [65] = "Breakthrough",
        [66] = "BlackArmoryRun",
        [67] = "Salvage",
        [68] = "IronBannerSalvage",
        [69] = "PvPCompetitive",
        [70] = "PvPQuickplay",
        [71] = "ClashQuickplay",
        [72] = "ClashCompetitive",
        [73] = "ControlQuickplay",
        [74] = "ControlCompetitive",
        [75] = "GambitPrime",
        [76] = "Reckoning",
        [77] = "Menagerie",
        [78] = "VexOffensive",
        [79] = "NightmareHunt",
        [80] = "Elimination",
        [81] = "Momentum",
        [82] = "Dungeon",
        [83] = "Sundial",
        [84] = "TrialsOfOsiris",
        [85] = "Dares",
        [86] = "Offensive",
        [87] = "LostSector",
        [88] = "Rift",
        [89] = "ZoneControl",
        [90] = "IronBannerRift",
        [91] = "IronBannerZoneControl",
        [92] = "Relic"
    };

    public static string GetSpecificActivityModeName(int mode)
    {
        return ActivityModeTypeNames.GetValueOrDefault(mode) ?? $"Mode {mode}";
    }

    private static readonly TimeSpan IncrementalCrawlOverlap = TimeSpan.FromHours(48);
    private static readonly DateTimeOffset BeyondLightRelease = new(2020, 11, 10, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WitchQueenRelease = new(2022, 2, 22, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SeasonOfTheHauntedRelease = new(2022, 5, 24, 17, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<long> ScourgeOfThePastActivityHashes = [548750096, 2812525063];
    private static readonly HashSet<long> LeviathanActivityHashes =
    [
        2693136600, 2693136601, 2693136602, 2693136603, 2693136604, 2693136605,
        89727599, 287649202, 1699948563, 1875726950, 3916343513, 4039317196,
        417231112, 508802457, 757116822, 771164842, 1685065161, 1800508819,
        2449714930, 3446541099, 4206123728, 3912437239, 3879860661, 3857338478
    ];
    private readonly ContestModeLookup contestMode = ContestModeLookup.FromOptions(contestModeOptions.Value);
    private readonly ConquestLookup conquests = ConquestLookup.FromOptions(conquestOptions.Value);
    private readonly ActivityTriumphRecordOptions activityTriumphRecords = activityTriumphRecordOptions.Value;
    private readonly CrawlerOptions crawler = crawlerOptions.Value;
    private static int MaxConcurrentDefinitionRequests => Math.Max(1, Math.Min(8, Environment.ProcessorCount));

    public async Task CrawlAsync(int platformId, long playerMembershipId, ICrawlProgress? progress, CancellationToken cancellationToken)
    {
        using var activity = AppTelemetry.ActivitySource.StartActivity("crawler.player.crawl", ActivityKind.Internal);
        activity?.SetTag("destiny.membership_type_id", platformId);
        activity?.SetTag("destiny.membership_id", playerMembershipId);

        logger.LogInformation("Crawling Destiny report for membership {MembershipType}/{MembershipId}.", platformId, playerMembershipId);

        try
        {
            var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
            var accumulators = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators");
            var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, platformId)
                & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, playerMembershipId);
            var accumulatorFilter = Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, platformId)
                & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, playerMembershipId);
            var existingReportTask = reports.Find(filter).FirstOrDefaultAsync(cancellationToken);
            var existingAccumulatorTask = accumulators.Find(accumulatorFilter).FirstOrDefaultAsync(cancellationToken);

            if (progress is not null)
            {
                await progress.StartPhaseAsync("manifest", "Loading manifest", cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);

            if (progress is not null)
            {
                await progress.StartPhaseAsync("profile", "Loading profile", total: 2, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var profileTask = ExecuteBungieOperationAsync(
                $"GetProfile:{platformId}:{playerMembershipId}",
                () => bungieClient.Destiny2_GetProfileAsync(ProfileComponents, playerMembershipId, platformId, cancellationToken),
                cancellationToken);
            var accountStatsTask = ExecuteBungieOperationAsync(
                $"GetHistoricalStatsForAccount:{platformId}:{playerMembershipId}",
                () => bungieClient.Destiny2_GetHistoricalStatsForAccountAsync(playerMembershipId, AccountStatGroups, platformId, cancellationToken),
                cancellationToken);

            try
            {
                await Task.WhenAll(profileTask, accountStatsTask, existingReportTask, existingAccumulatorTask).ConfigureAwait(false);
            }
            catch (Exception) when (IsNotFoundFault(profileTask))
            {
                await MarkPlayerNotFoundAsync(platformId, playerMembershipId, cancellationToken).ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                logger.LogInformation(
                    "Marked Destiny report for membership {MembershipType}/{MembershipId} as failed because the initial GetProfile call returned not found.",
                    platformId,
                    playerMembershipId);
                return;
            }
            catch (Exception ex) when (IsPrivateProfileFault(profileTask) || IsPrivateProfileFault(accountStatsTask) || IsPrivateProfileException(ex))
            {
                await MarkPlayerPrivateAsync(platformId, playerMembershipId, ex.Message, cancellationToken).ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                logger.LogInformation(
                    "Marked Destiny report for membership {MembershipType}/{MembershipId} as private because an initial profile request returned a privacy restriction.",
                    platformId,
                    playerMembershipId);
                return;
            }

            if (progress is not null)
            {
                await progress.CompletePhaseAsync(current: 2, total: 2, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (IsPrivateProfileResponse(profileTask.Result) || IsPrivateProfileResponse(accountStatsTask.Result))
            {
                await MarkPlayerPrivateAsync(platformId, playerMembershipId, "Destiny profile is not public.", cancellationToken).ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                logger.LogInformation(
                    "Marked Destiny report for membership {MembershipType}/{MembershipId} as private because an initial profile response returned a privacy restriction.",
                    platformId,
                    playerMembershipId);
                return;
            }

            var profile = EnsureSuccess(profileTask.Result, response => response.Response, "GetProfile");
            var accountStats = EnsureSuccess(accountStatsTask.Result, response => response.Response, "GetHistoricalStatsForAccount");
            var historicalCharacters = accountStats.Characters?.ToArray() ?? [];
            var existingReport = existingReportTask.Result;
            var existingAccumulator = existingAccumulatorTask.Result;
            var requiresFullCrawl = existingAccumulator is null
                || !existingAccumulator.FirstActivityDiscoveryCompleted
                || existingAccumulator.NeedsFullRecrawl
                || existingReport?.NeedsFullRecrawl == true;
            var accumulator = requiresFullCrawl
                ? NewAccumulator(platformId, playerMembershipId)
                : existingAccumulator!;
            var crawlAfter = requiresFullCrawl
                ? (DateTimeOffset?)null
                : new DateTimeOffset(accumulator.NewestActivityPeriod, TimeSpan.Zero).Subtract(IncrementalCrawlOverlap);

            var characterIds = historicalCharacters.Select(character => character.CharacterId).ToArray();
            var profileCharacters = profile.Characters?.Data?.Values.ToArray() ?? [];
            var currentCharacterIds = profileCharacters.Select(character => character.CharacterId).ToHashSet();
            var deletedCharacterIds = historicalCharacters
                .Where(character => character.Deleted || !currentCharacterIds.Contains(character.CharacterId))
                .Select(character => character.CharacterId)
                .Distinct()
                .ToArray();

            if (progress is not null)
            {
                await progress.StartPhaseAsync("character-stats", "Loading character stats", total: characterIds.Length, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var historicalStatsTask = FetchModeStatsAsync(platformId, playerMembershipId, characterIds, cancellationToken);
            var deletedCharacterIdentitiesTask = FetchDeletedCharacterIdentitiesAsync(
                platformId,
                playerMembershipId,
                deletedCharacterIds,
                manifest,
                cancellationToken);

            await Task.WhenAll(historicalStatsTask, deletedCharacterIdentitiesTask).ConfigureAwait(false);

            if (progress is not null)
            {
                await progress.CompletePhaseAsync(current: characterIds.Length, total: characterIds.Length, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var recentActivityIds = requiresFullCrawl
                ? new HashSet<long>()
                : accumulator.RecentActivityInstanceIds.ToHashSet();
            var characterClassById = BuildCharacterClassMap(historicalCharacters, [], playerMembershipId, characterIds);
            var recoveredIdentityById = deletedCharacterIdentitiesTask.Result;
            var recoveredRaceById = recoveredIdentityById.ToDictionary(
                item => item.Key,
                item => item.Value.Race);
            foreach (var (characterId, identity) in recoveredIdentityById)
            {
                if (identity.Class != "Unknown")
                {
                    characterClassById[characterId] = identity.Class;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var userInfo = profile.Profile?.Data?.UserInfo;
            var report = new DestinyReport
            {
                PlatformId = platformId,
                PlayerMembershipId = playerMembershipId,
                DisplayName = !string.IsNullOrWhiteSpace(userInfo?.BungieGlobalDisplayName)
                    ? userInfo.BungieGlobalDisplayName
                    : userInfo?.DisplayName ?? "",
                DisplayCode = userInfo?.BungieGlobalDisplayNameCode ?? 0,
                CrawlState = DestinyReport.CrawlStateCompleted,
                QueuedInRedis = false,
                QueuedAtUtc = existingReport?.QueuedAtUtc,
                StartedAtUtc = existingReport?.StartedAtUtc,
                LastCrawledAtUtc = now.UtcDateTime,
                LeaseExpiresAtUtc = null,
                LeaseOwner = "",
                CrawlError = "",
                NeedsFullRecrawl = false,
                FullRecrawlReason = ""
            };

            ApplyAccountStats(
                report,
                accountStats,
                historicalCharacters,
                characterClassById,
                recoveredRaceById,
                profile);
            ApplyProfileStats(report, profile, manifest);
            ApplyModeStats(report, historicalStatsTask.Result);
            await ApplyActivityDerivedStatsAsync(report, accumulator, platformId, playerMembershipId, characterIds, crawlAfter, recentActivityIds, characterClassById, manifest, requiresFullCrawl, progress, cancellationToken).ConfigureAwait(false);
            // The broader PGCR crawl may recover a class when the one-record lookup could not.
            report.CharacterPlaytime = BuildCharacterPlaytime(
                historicalCharacters,
                characterClassById,
                recoveredRaceById,
                profileCharacters);
            if (progress is not null)
            {
                await progress.StartPhaseAsync("triumphs", "Applying triumphs", cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            ApplyTriumphSeals(report, profile, manifest);
            ApplyActivityTriumphRecords(report, profile);

            if (progress is not null)
            {
                await progress.StartPhaseAsync("saving", "Saving report", total: 2, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await reports.ReplaceOneAsync(filter, report, new ReplaceOptions { IsUpsert = true }, cancellationToken)
                .ConfigureAwait(false);
            await accumulators.ReplaceOneAsync(accumulatorFilter, accumulator, new ReplaceOptions { IsUpsert = true }, cancellationToken)
                .ConfigureAwait(false);

            if (progress is not null)
            {
                await progress.CompletePhaseAsync(current: 2, total: 2, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (IsPrivateProfileException(ex))
        {
            await MarkPlayerPrivateAsync(platformId, playerMembershipId, ex.Message, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            logger.LogInformation(
                "Marked Destiny report for membership {MembershipType}/{MembershipId} as private because Bungie returned a privacy restriction.",
                platformId,
                playerMembershipId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private async Task MarkPlayerNotFoundAsync(int platformId, long playerMembershipId, CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var accumulators = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators");
        var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, platformId)
            & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, playerMembershipId);
        var accumulatorFilter = Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, platformId)
            & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, playerMembershipId);
        var now = DateTimeOffset.UtcNow;
        var report = new DestinyReport
        {
            PlatformId = platformId,
            PlayerMembershipId = playerMembershipId,
            CrawlState = DestinyReport.CrawlStateFailed,
            QueuedInRedis = false,
            LastCrawledAtUtc = now.UtcDateTime,
            LeaseExpiresAtUtc = null,
            LeaseOwner = "",
            CrawlError = "Destiny account not found.",
            NeedsFullRecrawl = false,
            FullRecrawlReason = ""
        };

        await reports.ReplaceOneAsync(filter, report, new ReplaceOptions { IsUpsert = true }, cancellationToken)
            .ConfigureAwait(false);
        await accumulators.DeleteOneAsync(accumulatorFilter, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkPlayerPrivateAsync(int platformId, long playerMembershipId, string error, CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var accumulators = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators");
        var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, platformId)
            & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, playerMembershipId);
        var accumulatorFilter = Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, platformId)
            & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, playerMembershipId);
        var now = DateTimeOffset.UtcNow;
        var report = new DestinyReport
        {
            PlatformId = platformId,
            PlayerMembershipId = playerMembershipId,
            CrawlState = DestinyReport.CrawlStatePrivate,
            QueuedInRedis = false,
            LastCrawledAtUtc = now.UtcDateTime,
            LeaseExpiresAtUtc = null,
            LeaseOwner = "",
            CrawlError = string.IsNullOrWhiteSpace(error) ? "Destiny profile is not public." : error,
            NeedsFullRecrawl = false,
            FullRecrawlReason = ""
        };

        await reports.ReplaceOneAsync(filter, report, new ReplaceOptions { IsUpsert = true }, cancellationToken)
            .ConfigureAwait(false);
        await accumulators.DeleteOneAsync(accumulatorFilter, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsNotFoundFault(Task task)
    {
        return task.Exception?.Flatten().InnerExceptions.OfType<ApiException>().Any(exception => exception.IsNotFound()) == true;
    }

    private static bool IsPrivateProfileFault(Task task)
    {
        return task.Exception?.Flatten().InnerExceptions.Any(IsPrivateProfileException) == true;
    }

    private async Task<T> ExecuteBungieOperationAsync<T>(
        string operation,
        Func<Task<T>> request,
        CancellationToken cancellationToken)
    {
        using var activity = AppTelemetry.ActivitySource.StartActivity("bungie.operation", ActivityKind.Client);
        activity?.SetTag("bungie.operation", operation);

        try
        {
            var response = await request().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTransportFailure(ex))
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName);
            activity?.SetTag("error.message", ex.Message);

            logger.LogWarning(
                ex,
                "Bungie operation {BungieOperation} failed before a Bungie API response was available.",
                operation);

            throw new BungieOperationException(operation, ex);
        }
    }

    private static bool IsTransportFailure(Exception exception)
    {
        return exception is HttpRequestException
            or OperationCanceledException
            or TimeoutException;
    }

    private static bool IsBungieOperationFailure(Exception exception)
    {
        return exception is BungieOperationException;
    }

    private sealed class BungieOperationException(string operation, Exception innerException)
        : Exception($"Bungie operation '{operation}' failed: {innerException.Message}", innerException);
}
