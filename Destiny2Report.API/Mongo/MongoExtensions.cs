using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Search;

namespace Destiny2Report.API.Mongo;

public static class MongoExtensions
{
    private const string PlayerFullDisplayNameSearchIndex = "player-full-display-name";

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
        await reports.EnsureFullDisplayNameSearchIndexAsync(cancellationToken);

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

    private static async Task EnsureFullDisplayNameSearchIndexAsync(
        this IMongoCollection<DestinyReport> reports,
        CancellationToken cancellationToken)
    {
        using var searchIndexes = await reports.SearchIndexes
            .ListAsync(PlayerFullDisplayNameSearchIndex, aggregateOptions: null, cancellationToken)
            .ConfigureAwait(false);
        var existingSearchIndexes = await searchIndexes.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existingSearchIndexes.Count > 0)
        {
            return;
        }

        var definition = new BsonDocument
        {
            {
                "mappings",
                new BsonDocument
                {
                    { "dynamic", false },
                    {
                        "fields",
                        new BsonDocument
                        {
                            { nameof(DestinyReport.FullDisplayName), new BsonDocument("type", "string") }
                        }
                    }
                }
            }
        };

        await reports.SearchIndexes.CreateOneAsync(
                new CreateSearchIndexModel(PlayerFullDisplayNameSearchIndex, definition),
                cancellationToken)
            .ConfigureAwait(false);
    }

}
