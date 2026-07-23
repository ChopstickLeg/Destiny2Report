using System.Buffers.Binary;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Destiny2Report.Tests.Features.Crawler;

public sealed class CrawlerProtocolTests
{
    [Fact]
    public void Player_key_is_stable_and_language_neutral()
    {
        var key = CrawlJob.CreatePlayerKey(3, 0x0102_0304_0506_0708);

        Assert.Equal(12, key.Length);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(key));
        Assert.Equal(0x0102_0304_0506_0708, BinaryPrimitives.ReadInt64BigEndian(key.AsSpan(4)));
    }

    [Fact]
    public void Crawler_has_one_rust_worker_stream()
    {
        Assert.Equal("crawler:jobs", CrawlerQueue.StreamName);
        Assert.Equal("crawler-workers", CrawlerQueue.ConsumerGroupName);
    }

    [Fact]
    public void Queryable_report_document_keeps_metrics_as_bson_fields()
    {
        var document = new CrawlReportDocument
        {
            PlayerKey = CrawlJob.CreatePlayerKey(3, long.MaxValue),
            Generation = Guid.NewGuid().ToString("N"),
            Data = BsonDocument.Parse("""{"platformId":3,"totalKills":1234,"crawlState":"completed"}""")
        };

        Assert.Equal(1234, document.Data["totalKills"].AsInt32);
        Assert.Equal("completed", document.Data["crawlState"].AsString);
        Assert.DoesNotContain("v", document.ToBsonDocument().Names);
    }

    [Theory]
    [InlineData("PvE", StoredActivityMode.Pve)]
    [InlineData("Crucible", StoredActivityMode.Crucible)]
    [InlineData("Gambit", StoredActivityMode.Gambit)]
    public void Activity_modes_are_stored_as_bytes(string text, StoredActivityMode stored)
    {
        Assert.Equal(stored, CrawlStorageMappings.ToStoredMode(text));
        Assert.Equal(text, CrawlStorageMappings.FromStoredMode(stored));
    }

    [Theory]
    [InlineData("Titan", StoredCharacterClass.Titan)]
    [InlineData("Hunter", StoredCharacterClass.Hunter)]
    [InlineData("Warlock", StoredCharacterClass.Warlock)]
    public void Character_classes_are_stored_as_bytes(string text, StoredCharacterClass stored)
    {
        Assert.Equal(stored, CrawlStorageMappings.ToStoredClass(text));
        Assert.Equal(text, CrawlStorageMappings.FromStoredClass(stored));
    }

    [Fact]
    public void Artifact_bson_uses_short_queryable_numeric_fields()
    {
        var stored = new CrawlArtifactDocument
        {
            PlayerKey = CrawlJob.CreatePlayerKey(3, 42),
            Generation = "generation",
            Kind = CrawlArtifactKind.Weapon,
            ActivityMode = StoredActivityMode.Crucible,
            SpecificActivityMode = 70,
            CharacterClass = StoredCharacterClass.Warlock,
            Hash = uint.MaxValue,
            Value = 99,
            CreatedAtUtc = DateTime.UtcNow
        }.ToBsonDocument();

        Assert.Equal((int)CrawlArtifactKind.Weapon, stored["k"].AsInt32);
        Assert.Equal((int)StoredActivityMode.Crucible, stored["m"].AsInt32);
        Assert.Equal((int)StoredCharacterClass.Warlock, stored["c"].AsInt32);
        Assert.Equal(uint.MaxValue, stored["h"].AsInt64);
        Assert.Equal(99, stored["n"].AsInt64);
        Assert.DoesNotContain("ActivityMode", stored.Names);
    }

    [Fact]
    public void Finalizer_claim_accepts_legacy_jobs_with_missing_owner_fields()
    {
        var filter = CrawlerFinalizerBackgroundService.BuildClaimFilter(DateTime.UtcNow);
        var rendered = filter.Render(new RenderArgs<CrawlJob>(
            BsonSerializer.LookupSerializer<CrawlJob>(),
            BsonSerializer.SerializerRegistry));

        var json = rendered.ToJson();
        Assert.Contains("\"fo\" : { \"$exists\" : false }", json);
        Assert.Contains("\"s\" : \"awaiting_finalization\"", json);
    }

}
