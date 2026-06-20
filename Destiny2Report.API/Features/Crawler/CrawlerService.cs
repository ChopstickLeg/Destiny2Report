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

public partial class CrawlerService(
    ILogger<CrawlerService> logger,
    IMongoDatabase mongoDatabase,
    ID2ReportClient bungieClient,
    HybridCache cache,
    IHttpClientFactory httpClientFactory,
    IOptions<ContestModeOptions> contestModeOptions,
    IOptions<ActivityTriumphRecordOptions> activityTriumphRecordOptions) : ICrawlerService
{
    private const string BungieNetBaseUrl = "https://www.bungie.net";
    private const int GeneralStatsGroup = 1;
    private const int BasicProfileComponent = 100;
    private const int ProfileCharactersComponent = 200;
    private const int ProfileRecordsComponent = 900;
    private const int MetricsComponent = 1100;
    private const int PageSize = 250;
    private const int MaxConcurrentPgcrRequests = 45;
    private const int MaxConcurrentSherpaHistoryRequests = 8;
    private const int AllMembershipTypes = 254;
    private const string InventoryItemDefinitionType = "DestinyInventoryItemDefinition";

    private static readonly int[] AccountStatGroups = [GeneralStatsGroup];
    private static readonly int[] ProfileComponents = [ProfileRecordsComponent, MetricsComponent, ProfileCharactersComponent];
    private static readonly int[] ProfileCharactersComponents = [BasicProfileComponent, ProfileCharactersComponent];
    private static readonly int[] ModeStatGroups = [GeneralStatsGroup];
    private static readonly long[] TriumphSealRootPresentationNodeHashes = [616318467, 1881970629];
    private static readonly TimeSpan ManifestCacheDuration = TimeSpan.FromDays(1);
    private static readonly HashSet<long> PrivateGambitActivityTypeHashes = [146907730, 2516284680];
    private static readonly HashSet<long> PrivateCrucibleActivityTypeHashes = [4260058063];
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
    private readonly ActivityTriumphRecordOptions activityTriumphRecords = activityTriumphRecordOptions.Value;

    public async Task CrawlAsync(int platformId, long playerMembershipId, CancellationToken cancellationToken)
    {
        using var activity = AppTelemetry.ActivitySource.StartActivity("crawler.player.crawl", ActivityKind.Internal);
        activity?.SetTag("destiny.membership_type_id", platformId);
        activity?.SetTag("destiny.membership_id", playerMembershipId);

        logger.LogInformation("Crawling Destiny report for membership {MembershipType}/{MembershipId}.", platformId, playerMembershipId);

        try
        {
            var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
            var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);

            var profileTask = bungieClient.Destiny2_GetProfileAsync(ProfileComponents, playerMembershipId, platformId, cancellationToken);
            var accountStatsTask = bungieClient.Destiny2_GetHistoricalStatsForAccountAsync(playerMembershipId, AccountStatGroups, platformId, cancellationToken);

            await Task.WhenAll(profileTask, accountStatsTask).ConfigureAwait(false);

            var profile = EnsureSuccess(profileTask.Result, response => response.Response, "GetProfile");
            var accountStats = EnsureSuccess(accountStatsTask.Result, response => response.Response, "GetHistoricalStatsForAccount");
            var historicalCharacters = accountStats.Characters?.ToArray() ?? [];

            var characterIds = historicalCharacters.Select(character => character.CharacterId).ToArray();

            var historicalStatsTask = FetchModeStatsAsync(platformId, playerMembershipId, characterIds, cancellationToken);
            var weaponHistoryTask = FetchUniqueWeaponHistoryAsync(platformId, playerMembershipId, characterIds, cancellationToken);
            var activityHistoryTask = FetchActivityHistoriesAsync(platformId, playerMembershipId, characterIds, cancellationToken);

            await Task.WhenAll(historicalStatsTask, weaponHistoryTask, activityHistoryTask).ConfigureAwait(false);

            var activityHistory = activityHistoryTask.Result;
            var pgcrs = await FetchPgcrsAsync(activityHistory, cancellationToken).ConfigureAwait(false);
            var characterClassById = BuildCharacterClassMap(historicalCharacters, pgcrs.Values, playerMembershipId, characterIds);

            var report = new DestinyReport
            {
                PlatformId = platformId,
                PlayerMembershipId = playerMembershipId
            };

            ApplyAccountStats(report, accountStats, historicalCharacters, characterClassById);
            ApplyProfileStats(report, profile, manifest);
            ApplyModeStats(report, historicalStatsTask.Result);
            await ApplyActivityDerivedStatsAsync(report, platformId, playerMembershipId, activityHistory, pgcrs, manifest, cancellationToken).ConfigureAwait(false);
            await ApplyWeaponStatsAsync(report, weaponHistoryTask.Result, manifest, cancellationToken).ConfigureAwait(false);
            ApplyTriumphSeals(report, profile, manifest);
            ApplyActivityTriumphRecords(report, profile);

            var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, platformId)
                & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, playerMembershipId);

            await reports.ReplaceOneAsync(filter, report, new ReplaceOptions { IsUpsert = true }, cancellationToken)
                .ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
