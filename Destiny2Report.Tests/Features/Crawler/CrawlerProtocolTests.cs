using System.Buffers.Binary;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Reports;
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

    [Theory]
    [InlineData(11000, true)]
    [InlineData(11001, true)]
    [InlineData(112, false)]
    public void Queue_recognizes_find_and_modify_duplicate_key_commands(int errorCode, bool expected)
    {
        Assert.Equal(expected, CrawlerJobQueue.IsDuplicateKeyCommand(errorCode));
    }

    [Fact]
    public void Queue_admission_honors_the_report_full_recrawl_marker()
    {
        var filter = CrawlerJobQueue.BuildFullRecrawlFilter(3, 42);
        var rendered = filter.Render(new RenderArgs<DestinyReport>(
            BsonSerializer.LookupSerializer<DestinyReport>(),
            BsonSerializer.SerializerRegistry));

        Assert.Equal(3, rendered["PlatformId"].AsInt32);
        Assert.Equal(42, rendered["PlayerMembershipId"].AsInt64);
        Assert.True(rendered["NeedsFullRecrawl"].AsBoolean);
    }

    [Fact]
    public void Dispatch_status_backfills_identity_without_overwriting_a_worker_with_a_newer_fence()
    {
        var newerFenceBranch = CrawlerJobQueue.DispatchStatusScript[
            CrawlerJobQueue.DispatchStatusScript.IndexOf(
                "if currentRun == ARGV[1] and currentFence > tonumber(ARGV[2]) then",
                StringComparison.Ordinal)..];

        Assert.Contains("'streamEntryId', ARGV[6]", newerFenceBranch);
        Assert.True(
            newerFenceBranch.IndexOf("'streamEntryId', ARGV[6]", StringComparison.Ordinal)
            < newerFenceBranch.IndexOf("return 0", StringComparison.Ordinal));
        Assert.Contains("redis.call('EXPIRE', KEYS[1], ARGV[9])", CrawlerJobQueue.DispatchStatusScript);
    }

    [Theory]
    [InlineData("progressPhase")]
    [InlineData("progressLabel")]
    [InlineData("progressCurrent")]
    [InlineData("progressTotal")]
    [InlineData("progressStartedAtUtc")]
    [InlineData("progressUpdatedAtUtc")]
    public void Dispatch_status_clears_progress_from_a_previous_run(string field)
    {
        Assert.Contains($"'{field}', ''", CrawlerJobQueue.DispatchStatusScript);
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

    [Fact]
    public void Active_job_display_name_uses_the_shared_compact_field()
    {
        var document = new CrawlJob
        {
            PlayerKey = CrawlJob.CreatePlayerKey(3, 42),
            MembershipTypeId = 3,
            MembershipId = 42,
            DisplayName = "Guardian"
        }.ToBsonDocument();

        Assert.Equal("Guardian", document["dn"].AsString);
        Assert.DoesNotContain("DisplayName", document.Names);
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
    public void Finalizer_loads_compact_artifacts_by_globally_unique_generation()
    {
        var filter = CrawlGenerationStore.BuildArtifactGenerationFilter("generation");
        var rendered = filter.Render(new RenderArgs<CrawlArtifactDocument>(
            BsonSerializer.LookupSerializer<CrawlArtifactDocument>(),
            BsonSerializer.SerializerRegistry));

        Assert.Equal("""{ "g" : "generation" }""", rendered.ToJson());
        Assert.DoesNotContain("\"p\"", rendered.ToJson());
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

    [Fact]
    public void Finalizer_promotion_is_fenced_by_player_run_owner_and_fence()
    {
        var job = new CrawlJob
        {
            PlayerKey = CrawlJob.CreatePlayerKey(3, 42),
            RunId = "run",
            FinalizerOwner = "owner",
            FinalizerFence = 7
        };
        var filter = CrawlGenerationStore.BuildFinalizerOwnershipFilter(job);
        var rendered = filter.Render(new RenderArgs<CrawlJob>(
            BsonSerializer.LookupSerializer<CrawlJob>(),
            BsonSerializer.SerializerRegistry));

        var json = rendered.ToJson();
        Assert.Contains("\"_id\"", json);
        Assert.Contains("\"r\" : \"run\"", json);
        Assert.Contains("\"fo\" : \"owner\"", json);
        Assert.Contains("\"ffn\" : 7", json);
    }

    [Fact]
    public void Idle_scheduler_only_claims_mongo_only_queued_or_expired_reports()
    {
        var filter = CrawlerIdleMongoScheduler.BuildReportClaimFilter(DateTime.UtcNow);
        var rendered = filter.Render(new RenderArgs<DestinyReport>(
            BsonSerializer.LookupSerializer<DestinyReport>(),
            BsonSerializer.SerializerRegistry));

        var json = rendered.ToJson();
        Assert.Contains("\"QueuedInRedis\" : false", json);
        Assert.Contains("\"CrawlState\" : \"queued\"", json);
        Assert.Contains("\"CrawlState\" : \"running\"", json);
        Assert.Contains("\"LeaseExpiresAtUtc\" : { \"$lt\"", json);
    }

    [Fact]
    public void Idle_scheduler_yields_to_queued_and_running_crawler_jobs()
    {
        var filter = CrawlerIdleMongoScheduler.BuildActiveCrawlerFilter();
        var rendered = filter.Render(new RenderArgs<CrawlJob>(
            BsonSerializer.LookupSerializer<CrawlJob>(),
            BsonSerializer.SerializerRegistry));

        var json = rendered.ToJson();
        Assert.Contains("\"s\" : { \"$in\" : [\"queued\", \"running\"] }", json);
        Assert.DoesNotContain("awaiting_finalization", json);
    }

    [Fact]
    public void Durable_terminal_job_overrides_stale_running_redis_status()
    {
        var job = CreateJob(CrawlJob.StateCompleted, dispatched: true, streamEntryId: "new-0");
        var redisStatus = CreateStatus(DestinyReport.CrawlStateRunning, "new-0");

        var reconciled = ReportHandlers.ReconcileQueueStatus(job, redisStatus);

        Assert.NotNull(reconciled);
        Assert.Equal(DestinyReport.CrawlStateCompleted, reconciled.Status);
    }

    [Fact]
    public void Undispatched_mongo_job_overrides_a_previous_terminal_redis_status()
    {
        var job = CreateJob(CrawlJob.StateQueued, dispatched: false);
        var redisStatus = CreateStatus(DestinyReport.CrawlStateCompleted, "old-0");

        var reconciled = ReportHandlers.ReconcileQueueStatus(job, redisStatus);

        Assert.NotNull(reconciled);
        Assert.Equal(DestinyReport.CrawlStateQueued, reconciled.Status);
        Assert.Null(reconciled.StreamEntryId);
    }

    [Fact]
    public void Matching_live_redis_status_keeps_its_progress_snapshot()
    {
        var job = CreateJob(CrawlJob.StateRunning, dispatched: true, streamEntryId: "new-0");
        var progress = new CrawlProgressSnapshot(
            "activities",
            "Loading activities",
            12,
            20,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var redisStatus = CreateStatus(DestinyReport.CrawlStateRunning, "new-0") with
        {
            Progress = progress
        };

        var reconciled = ReportHandlers.ReconcileQueueStatus(job, redisStatus);

        Assert.Same(redisStatus, reconciled);
        Assert.Same(progress, reconciled!.Progress);
    }

    private static CrawlJob CreateJob(string state, bool dispatched, string streamEntryId = "") => new()
    {
        PlayerKey = CrawlJob.CreatePlayerKey(3, 42),
        MembershipTypeId = 3,
        MembershipId = 42,
        RunId = "run",
        State = state,
        DispatchedToRedis = dispatched,
        StreamEntryId = streamEntryId,
        QueuedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private static ReportQueueStatusResponse CreateStatus(string status, string? streamEntryId) => new(
        3,
        42,
        status,
        streamEntryId,
        null,
        null,
        0,
        DateTimeOffset.UtcNow,
        null);

}
