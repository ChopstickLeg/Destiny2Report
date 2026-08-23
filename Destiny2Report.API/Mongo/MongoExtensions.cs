using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Reports;
using Destiny2Report.API.Features.PushNotifications;
using Destiny2Report.API.Features.Leaderboards;
using Destiny2Report.API.Observability;
using MongoDB.Driver;

namespace Destiny2Report.API.Mongo;

public static class MongoExtensions
{
    public static IServiceCollection AddMongo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMongoClient>(provider =>
        {
            var connectionString = configuration.GetConnectionString("MongoDb")
                ?? throw new InvalidOperationException("ConnectionStrings:MongoDb is required.");

            var settings = MongoClientSettings.FromConnectionString(connectionString);
            provider.GetRequiredService<MongoCommandMetrics>().Configure(settings);
            return new MongoClient(settings);
        });

        services.AddSingleton(provider =>
        {
            var databaseName = configuration["Mongo:DatabaseName"]
                ?? throw new InvalidOperationException("Mongo:DatabaseName is required.");

            var mongoClient = provider.GetRequiredService<IMongoClient>();

            return mongoClient.GetDatabase(databaseName);
        });

        return services;
    }

    public static async Task EnsureMongoIndexesAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        var mongoDatabase = app.Services.GetRequiredService<IMongoDatabase>();

        var crawlJobs = mongoDatabase.GetCollection<CrawlJob>("crawl_jobs");
        var crawlJobDispatchKeys = Builders<CrawlJob>.IndexKeys
            .Ascending(job => job.State)
            .Ascending(job => job.DispatchedToRedis)
            .Ascending(job => job.QueuedAtUtc)
            .Ascending(job => job.LeaseExpiresAtUtc);
        await crawlJobs.EnsureIndexesAsync(
            [new CreateIndexModel<CrawlJob>(crawlJobDispatchKeys, new CreateIndexOptions { Name = "ix_crawl_jobs_state_dispatch_lease" })],
            cancellationToken);

        var compactReports = mongoDatabase.GetCollection<CrawlReportDocument>("reports");
        await compactReports.EnsureIndexesAsync(
            [new CreateIndexModel<CrawlReportDocument>(
                Builders<CrawlReportDocument>.IndexKeys.Ascending(item => item.PlayerKey).Ascending(item => item.Generation),
                new CreateIndexOptions { Name = "ux_reports_player_generation", Unique = true })],
            cancellationToken);

        var compactState = mongoDatabase.GetCollection<CrawlStateDocument>("crawl_state");
        await compactState.EnsureIndexesAsync(
            [new CreateIndexModel<CrawlStateDocument>(
                Builders<CrawlStateDocument>.IndexKeys.Ascending(item => item.PlayerKey).Ascending(item => item.Generation),
                new CreateIndexOptions { Name = "ux_crawl_state_player_generation", Unique = true })],
            cancellationToken);

        var compactArtifacts = mongoDatabase.GetCollection<CrawlArtifactDocument>("crawl_artifacts");
        const string legacyArtifactIndex = "ux_crawl_artifacts_generation_dimensions";
        using (var artifactIndexCursor = await compactArtifacts.Indexes.ListAsync(cancellationToken))
        {
            var artifactIndexes = await artifactIndexCursor.ToListAsync(cancellationToken);
            if (artifactIndexes.Any(index =>
                    index.TryGetValue("name", out var name)
                    && name.IsString
                    && name.AsString == legacyArtifactIndex))
            {
                await compactArtifacts.Indexes.DropOneAsync(legacyArtifactIndex, cancellationToken);
            }
        }
        await compactArtifacts.EnsureIndexesAsync(
            [new CreateIndexModel<CrawlArtifactDocument>(
                Builders<CrawlArtifactDocument>.IndexKeys
                    .Ascending(item => item.Generation)
                    .Ascending(item => item.Kind)
                    .Ascending(item => item.ActivityMode)
                    .Ascending(item => item.SpecificActivityMode)
                    .Ascending(item => item.CharacterClass)
                    .Ascending(item => item.Hash)
                    .Ascending(item => item.EncounteredMembershipType)
                    .Ascending(item => item.EncounteredMembershipId),
                new CreateIndexOptions { Name = "ux_crawl_artifacts_generation_dimensions_v2", Unique = true })],
            cancellationToken);

        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var reportIndexKeys = Builders<DestinyReport>.IndexKeys
            .Ascending(report => report.PlatformId)
            .Ascending(report => report.PlayerMembershipId);
        var backgroundQueueIndexKeys = Builders<DestinyReport>.IndexKeys
            .Ascending(report => report.CrawlState)
            .Ascending(report => report.QueuedInRedis)
            .Ascending(report => report.QueuedAtUtc)
            .Ascending(report => report.LeaseExpiresAtUtc);

        await reports.EnsureIndexesAsync(
            [
                new CreateIndexModel<DestinyReport>(
                    reportIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_destiny_reports_platform_player",
                        Unique = true
                    }),
                new CreateIndexModel<DestinyReport>(
                    backgroundQueueIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ix_destiny_reports_background_queue"
                    })
            ],
            cancellationToken);

        var completedLeaderboardIndexKeys = Builders<DestinyReport>.IndexKeys
            .Ascending(report => report.HasCompletedCrawl)
            .Ascending(report => report.CrawlState);
        await reports.EnsureIndexesAsync(
            [new CreateIndexModel<DestinyReport>(completedLeaderboardIndexKeys, new CreateIndexOptions { Name = "ix_destiny_reports_leaderboard_completion" })],
            cancellationToken);

        var leaderboardBoards = mongoDatabase.GetCollection<LeaderboardBoard>("leaderboard_boards");
        var leaderboardPlayerIndexKeys = Builders<LeaderboardBoard>.IndexKeys
            .Ascending("Entries.MembershipTypeId")
            .Ascending("Entries.MembershipId");
        await leaderboardBoards.EnsureIndexesAsync(
            [new CreateIndexModel<LeaderboardBoard>(leaderboardPlayerIndexKeys, new CreateIndexOptions { Name = "ix_leaderboard_boards_player" })],
            cancellationToken);

        var leaderboardPlayerScores = mongoDatabase.GetCollection<PlayerLeaderboardSnapshot>("leaderboard_player_scores");
        await leaderboardPlayerScores.EnsureIndexesAsync(
            [new CreateIndexModel<PlayerLeaderboardSnapshot>(
                Builders<PlayerLeaderboardSnapshot>.IndexKeys
                    .Ascending(item => item.MembershipTypeId)
                    .Ascending(item => item.MembershipId),
                new CreateIndexOptions { Name = "ux_leaderboard_player_scores_identity", Unique = true })],
            cancellationToken);

        var storyShares = mongoDatabase.GetCollection<StoryShare>("story_shares");
        var storyShareTokenIndexKeys = Builders<StoryShare>.IndexKeys
            .Ascending(share => share.TokenHash);
        var storyShareOwnerIndexKeys = Builders<StoryShare>.IndexKeys
            .Ascending(share => share.MembershipTypeId)
            .Ascending(share => share.MembershipId)
            .Descending(share => share.CreatedAtUtc);

        await storyShares.EnsureIndexesAsync(
            [
                new CreateIndexModel<StoryShare>(
                    storyShareTokenIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_story_shares_token_hash",
                        Unique = true
                    }),
                new CreateIndexModel<StoryShare>(
                    storyShareOwnerIndexKeys,
                    new CreateIndexOptions { Name = "ix_story_shares_owner_created" })
            ],
            cancellationToken);

        var pushSubscriptions = mongoDatabase.GetCollection<ReportPushSubscription>(
            ReportPushNotificationService.CollectionName);
        var pushSubscriptionIdentityIndexKeys = Builders<ReportPushSubscription>.IndexKeys
            .Ascending(subscription => subscription.EndpointHash)
            .Ascending(subscription => subscription.MembershipTypeId)
            .Ascending(subscription => subscription.MembershipId);
        var pushSubscriptionExpiryIndexKeys = Builders<ReportPushSubscription>.IndexKeys
            .Ascending(subscription => subscription.ExpiresAtUtc);

        await pushSubscriptions.EnsureIndexesAsync(
            [
                new CreateIndexModel<ReportPushSubscription>(
                    pushSubscriptionIdentityIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_report_push_endpoint_player",
                        Unique = true
                    }),
                new CreateIndexModel<ReportPushSubscription>(
                    pushSubscriptionExpiryIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ttl_report_push_expiry",
                        ExpireAfter = TimeSpan.Zero
                    })
            ],
            cancellationToken);

        var accumulators = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators");
        var accumulatorIndexKeys = Builders<CrawlAccumulator>.IndexKeys
            .Ascending(accumulator => accumulator.PlatformId)
            .Ascending(accumulator => accumulator.PlayerMembershipId);

        await accumulators.EnsureIndexesAsync(
            [
                new CreateIndexModel<CrawlAccumulator>(
                    accumulatorIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_crawl_accumulators_platform_player",
                        Unique = true
                    })
            ],
            cancellationToken);

        var weapons = mongoDatabase.GetCollection<WeaponAggregate>("weapon_aggregates");
        var weaponUniqueIndexKeys = Builders<WeaponAggregate>.IndexKeys
            .Ascending(weapon => weapon.OwnerMembershipType)
            .Ascending(weapon => weapon.OwnerMembershipId)
            .Ascending(weapon => weapon.ActivityMode)
            .Ascending(weapon => weapon.ClassName)
            .Ascending(weapon => weapon.SpecificActivityMode)
            .Ascending(weapon => weapon.WeaponHash);

        await weapons.EnsureIndexesAsync(
            [
                new CreateIndexModel<WeaponAggregate>(
                    weaponUniqueIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_weapon_aggregates_owner_mode_class_specific_mode_hash",
                        Unique = true
                    })
            ],
            cancellationToken);
        var deaths = mongoDatabase.GetCollection<DeathAggregate>("death_aggregates");
        var deathUniqueIndexKeys = Builders<DeathAggregate>.IndexKeys
            .Ascending(death => death.OwnerMembershipType)
            .Ascending(death => death.OwnerMembershipId)
            .Ascending(death => death.ActivityMode)
            .Ascending(death => death.SpecificActivityMode);
        await deaths.EnsureIndexesAsync(
            [
                new CreateIndexModel<DeathAggregate>(
                    deathUniqueIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_death_aggregates_owner_mode_specific_mode",
                        Unique = true
                    })
            ],
            cancellationToken);
        var emblems = mongoDatabase.GetCollection<EmblemAggregate>("emblem_aggregates");
        var emblemUniqueIndexKeys = Builders<EmblemAggregate>.IndexKeys
            .Ascending(emblem => emblem.OwnerMembershipType)
            .Ascending(emblem => emblem.OwnerMembershipId)
            .Ascending(emblem => emblem.EmblemHash);
        await emblems.EnsureIndexesAsync(
            [
                new CreateIndexModel<EmblemAggregate>(
                    emblemUniqueIndexKeys,
                    new CreateIndexOptions { Name = "ux_emblem_aggregates_owner_emblem", Unique = true })
            ],
            cancellationToken);
        var encounters = mongoDatabase.GetCollection<PlayerEncounterAggregate>("player_encounters");
        var encounterPairIndexKeys = Builders<PlayerEncounterAggregate>.IndexKeys
            .Ascending(encounter => encounter.OwnerMembershipType)
            .Ascending(encounter => encounter.OwnerMembershipId)
            .Ascending(encounter => encounter.EncounteredMembershipType)
            .Ascending(encounter => encounter.EncounteredMembershipId);
        var encounterTopIndexKeys = Builders<PlayerEncounterAggregate>.IndexKeys
            .Ascending(encounter => encounter.OwnerMembershipType)
            .Ascending(encounter => encounter.OwnerMembershipId)
            .Descending(encounter => encounter.Count);

        await encounters.EnsureIndexesAsync(
            [
                new CreateIndexModel<PlayerEncounterAggregate>(
                    encounterPairIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_player_encounters_owner_encountered",
                        Unique = true
                    }),
                new CreateIndexModel<PlayerEncounterAggregate>(
                    encounterTopIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ix_player_encounters_owner_count"
                    })
            ],
            cancellationToken);
    }

    private static async Task EnsureIndexesAsync<TDocument>(
        this IMongoCollection<TDocument> collection,
        IReadOnlyCollection<CreateIndexModel<TDocument>> indexModels,
        CancellationToken cancellationToken)
    {
        using var indexCursor = await collection.Indexes.ListAsync(cancellationToken);
        var existingIndexes = await indexCursor.ToListAsync(cancellationToken);
        var existingIndexNames = existingIndexes
            .Select(index => index.TryGetValue("name", out var name) ? name.AsString : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        var missingIndexModels = indexModels
            .Where(indexModel => indexModel.Options?.Name is not { } indexName || !existingIndexNames.Contains(indexName))
            .ToArray();

        if (missingIndexModels.Length > 0)
        {
            await collection.Indexes.CreateManyAsync(missingIndexModels, cancellationToken);
        }
    }

}
