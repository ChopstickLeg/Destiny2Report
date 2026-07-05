using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Destiny2Report.API.Mongo;

public static class MongoExtensions
{
    public static IServiceCollection AddMongo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMongoClient>(_ =>
        {
            var connectionString = configuration.GetConnectionString("MongoDb")
                ?? throw new InvalidOperationException("ConnectionStrings:MongoDb is required.");

            return new MongoClient(connectionString);
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
            .Ascending(weapon => weapon.WeaponKey);
        var weaponTopIndexKeys = Builders<WeaponAggregate>.IndexKeys
            .Ascending(weapon => weapon.OwnerMembershipType)
            .Ascending(weapon => weapon.OwnerMembershipId)
            .Ascending(weapon => weapon.ActivityMode)
            .Descending(weapon => weapon.Kills);
        var weaponCategoryIndexKeys = Builders<WeaponAggregate>.IndexKeys
            .Ascending(weapon => weapon.OwnerMembershipType)
            .Ascending(weapon => weapon.OwnerMembershipId)
            .Ascending(weapon => weapon.ActivityMode)
            .Ascending(weapon => weapon.CategoryKey)
            .Descending(weapon => weapon.Kills);

        await weapons.EnsureIndexesAsync(
            [
                new CreateIndexModel<WeaponAggregate>(
                    weaponUniqueIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_weapon_aggregates_owner_mode_key",
                        Unique = true
                    }),
                new CreateIndexModel<WeaponAggregate>(
                    weaponTopIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ix_weapon_aggregates_owner_mode_kills"
                    }),
                new CreateIndexModel<WeaponAggregate>(
                    weaponCategoryIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ix_weapon_aggregates_owner_mode_category_kills"
                    })
            ],
            cancellationToken);

        var weaponCategories = mongoDatabase.GetCollection<WeaponCategoryAggregate>("weapon_category_aggregates");
        var weaponCategoryUniqueIndexKeys = Builders<WeaponCategoryAggregate>.IndexKeys
            .Ascending(category => category.OwnerMembershipType)
            .Ascending(category => category.OwnerMembershipId)
            .Ascending(category => category.ActivityMode)
            .Ascending(category => category.CategoryKey);
        var weaponCategoryTopIndexKeys = Builders<WeaponCategoryAggregate>.IndexKeys
            .Ascending(category => category.OwnerMembershipType)
            .Ascending(category => category.OwnerMembershipId)
            .Ascending(category => category.ActivityMode)
            .Descending(category => category.Kills);

        await weaponCategories.EnsureIndexesAsync(
            [
                new CreateIndexModel<WeaponCategoryAggregate>(
                    weaponCategoryUniqueIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ux_weapon_category_aggregates_owner_mode_category",
                        Unique = true
                    }),
                new CreateIndexModel<WeaponCategoryAggregate>(
                    weaponCategoryTopIndexKeys,
                    new CreateIndexOptions
                    {
                        Name = "ix_weapon_category_aggregates_owner_mode_kills"
                    })
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
