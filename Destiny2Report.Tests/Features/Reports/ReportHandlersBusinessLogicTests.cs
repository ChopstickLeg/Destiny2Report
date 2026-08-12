using System.Reflection;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Reports;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Destiny2Report.Tests.Features.Reports;

public sealed class ReportHandlersBusinessLogicTests
{
    private static readonly Type HandlerType = typeof(ReportHandlers);

    [Theory]
    [InlineData(0, 4611686018463095984, "Invalid membership type id", "membershipTypeId must be a positive integer.")]
    [InlineData(1, 0, "Invalid membership id", "membershipId must be a positive integer.")]
    public void TryValidateMembership_rejects_non_positive_membership_values(
        int membershipTypeId,
        long membershipId,
        string expectedTitle,
        string expectedDetail)
    {
        var arguments = new object?[] { membershipTypeId, membershipId, null };

        var isValid = (bool)Invoke("TryValidateMembership", arguments)!;

        Assert.False(isValid);
        var problem = Assert.IsType<ProblemDetails>(arguments[2]);
        Assert.Equal(expectedTitle, problem.Title);
        Assert.Equal(expectedDetail, problem.Detail);
        Assert.Equal(400, problem.Status);
    }

    [Fact]
    public void TryValidateMembership_accepts_realistic_membership_values()
    {
        var arguments = new object?[] { 1, 4611686018463095984L, null };

        var isValid = (bool)Invoke("TryValidateMembership", arguments)!;

        Assert.True(isValid);
        Assert.IsType<ProblemDetails>(arguments[2]);
    }

    [Fact]
    public void TryValidateQueueRequests_accepts_membership_objects()
    {
        IReadOnlyList<ReportQueueRequest> requests =
        [
            new(1, 4611686018463095984),
            new(2, 123456789)
        ];
        var arguments = new object?[] { requests, null, null };

        var isValid = (bool)Invoke("TryValidateQueueRequests", arguments)!;

        Assert.True(isValid);
        var memberships = Assert.IsAssignableFrom<IReadOnlyList<(int MembershipTypeId, long MembershipId)>>(arguments[1]);
        Assert.Equal([(1, 4611686018463095984L), (2, 123456789L)], memberships);
    }

    [Fact]
    public void TryValidateQueueRequests_rejects_an_empty_request()
    {
        var arguments = new object?[] { Array.Empty<ReportQueueRequest>(), null, null };

        var isValid = (bool)Invoke("TryValidateQueueRequests", arguments)!;

        Assert.False(isValid);
        var problem = Assert.IsType<ProblemDetails>(arguments[2]);
        Assert.Equal("Missing memberships", problem.Title);
    }

    [Fact]
    public void TryReadMatchingJobEvent_reads_only_events_for_requested_membership()
    {
        var updatedAt = DateTimeOffset.Parse("2026-06-19T12:00:00Z");
        var matchingJson = """
            {
              "MembershipTypeId": 1,
              "MembershipId": 4611686018463095984,
              "Status": "running",
              "StreamEntryId": "1750000000000-0",
              "Error": null,
              "UpdatedAtUtc": "2026-06-19T12:00:00Z"
            }
            """;
        var arguments = new object?[]
        {
            (RedisValue)matchingJson,
            1,
            4611686018463095984L,
            null
        };

        var matched = (bool)Invoke("TryReadMatchingJobEvent", arguments)!;

        Assert.True(matched);
        var jobEvent = Assert.IsType<ReportJobEvent>(arguments[3]);
        Assert.Equal("running", jobEvent.Status);
        Assert.Equal("1750000000000-0", jobEvent.StreamEntryId);
        Assert.Equal(updatedAt, jobEvent.UpdatedAtUtc);
    }

    [Fact]
    public void TryReadMatchingJobEvent_ignores_malformed_and_other_membership_events()
    {
        var malformedArguments = new object?[] { (RedisValue)"{", 1, 4611686018463095984L, null };
        var otherMembershipArguments = new object?[]
        {
            (RedisValue)"""{"MembershipTypeId":2,"MembershipId":99,"Status":"completed","UpdatedAtUtc":"2026-06-19T12:00:00Z"}""",
            1,
            4611686018463095984L,
            null
        };

        Assert.False((bool)Invoke("TryReadMatchingJobEvent", malformedArguments)!);
        Assert.False((bool)Invoke("TryReadMatchingJobEvent", otherMembershipArguments)!);

        Assert.Equal("", Assert.IsType<ReportJobEvent>(malformedArguments[3]).Status);
        Assert.Equal("", Assert.IsType<ReportJobEvent>(otherMembershipArguments[3]).Status);
    }

    [Fact]
    public void QueueStatusLocation_points_to_mapped_membership_queue_endpoint()
    {
        var result = (string)Invoke(
            "QueueStatusLocation",
            1,
            4611686018463095984L)!;

        Assert.Equal("/api/reports/1/4611686018463095984/queue", result);
    }

    [Fact]
    public void BuildQueueStatus_preserves_position_error_and_updated_timestamp()
    {
        var updatedAt = DateTimeOffset.Parse("2026-06-19T13:00:00Z");

        var result = (ReportQueueStatusResponse)Invoke(
            "BuildQueueStatus",
            1,
            4611686018463095984L,
            "failed",
            "1750000000000-0",
            "Bungie timeout",
            3L,
            12L,
            updatedAt)!;

        Assert.Equal(1, result.MembershipTypeId);
        Assert.Equal(4611686018463095984, result.MembershipId);
        Assert.Equal("failed", result.Status);
        Assert.Equal("1750000000000-0", result.StreamEntryId);
        Assert.Equal("Bungie timeout", result.Error);
        Assert.Equal(3, result.Position);
        Assert.Equal(12, result.QueueLength);
        Assert.Equal(updatedAt, result.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildQueueCohortFilter_separates_redis_and_mongo_queues(bool dispatchedToRedis)
    {
        var job = new CrawlJob
        {
            State = CrawlJob.StateQueued,
            DispatchedToRedis = dispatchedToRedis
        };

        var filter = ReportHandlers.BuildQueueCohortFilter(job);
        var rendered = filter.Render(new RenderArgs<CrawlJob>(
            BsonSerializer.LookupSerializer<CrawlJob>(),
            BsonSerializer.SerializerRegistry));

        Assert.Equal(CrawlJob.StateQueued, rendered["s"].AsString);
        Assert.Equal(dispatchedToRedis, rendered["d"].AsBoolean);
    }

    [Fact]
    public void BuildJobsAheadFilter_uses_enqueue_time_then_player_key_for_stable_ordering()
    {
        var job = new CrawlJob
        {
            PlayerKey = CrawlJob.CreatePlayerKey(1, 4611686018463095984L),
            QueuedAtUtc = DateTime.Parse("2026-08-12T12:00:00Z").ToUniversalTime()
        };

        var filter = ReportHandlers.BuildJobsAheadFilter(job);
        var rendered = filter.Render(new RenderArgs<CrawlJob>(
            BsonSerializer.LookupSerializer<CrawlJob>(),
            BsonSerializer.SerializerRegistry));
        var renderedJson = rendered.ToJson();

        Assert.Contains("$or", renderedJson);
        Assert.Contains("qa", renderedJson);
        Assert.Contains("_id", renderedJson);
        Assert.Contains("$lt", renderedJson);
    }

    [Theory]
    [InlineData(DestinyReport.CrawlStateCompleted, true)]
    [InlineData(DestinyReport.CrawlStateFailed, true)]
    [InlineData(DestinyReport.CrawlStatePrivate, true)]
    [InlineData(DestinyReport.CrawlStateQueued, false)]
    [InlineData(DestinyReport.CrawlStateRunning, false)]
    public void IsTerminalCrawlState_treats_private_as_terminal(string status, bool expected)
    {
        var result = (bool)Invoke("IsTerminalCrawlState", status)!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetForegroundCrawlRetryAfter_returns_remaining_six_hour_cooldown()
    {
        var now = DateTimeOffset.Parse("2026-07-20T18:00:00Z");
        var lastCrawled = DateTime.Parse("2026-07-20T14:30:00Z").ToUniversalTime();

        var result = (TimeSpan?)Invoke("GetForegroundCrawlRetryAfter", lastCrawled, false, now);

        Assert.Equal(TimeSpan.FromHours(2.5), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-07-20T12:00:00Z")]
    public void GetForegroundCrawlRetryAfter_allows_never_or_old_crawls(string? lastCrawledValue)
    {
        var now = DateTimeOffset.Parse("2026-07-20T18:00:00Z");
        var lastCrawled = lastCrawledValue is null ? (DateTime?)null : DateTime.Parse(lastCrawledValue).ToUniversalTime();

        var result = (TimeSpan?)Invoke("GetForegroundCrawlRetryAfter", lastCrawled, false, now);

        Assert.Null(result);
    }

    [Fact]
    public void GetForegroundCrawlRetryAfter_allows_recent_crawl_when_full_recrawl_is_needed()
    {
        var now = DateTimeOffset.Parse("2026-07-20T18:00:00Z");
        var lastCrawled = DateTime.Parse("2026-07-20T17:30:00Z").ToUniversalTime();

        var result = (TimeSpan?)Invoke("GetForegroundCrawlRetryAfter", lastCrawled, true, now);

        Assert.Null(result);
    }

    private static object? Invoke(string methodName, params object?[] arguments)
    {
        var method = HandlerType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.Name == methodName)
            .Single(method =>
            {
                var parameters = method.GetParameters();
                var requiredParameterCount = parameters.Count(parameter => !parameter.IsOptional);

                return requiredParameterCount <= arguments.Length && arguments.Length <= parameters.Length;
            });

        var parameters = method.GetParameters();
        var invokeArguments = new object?[parameters.Length];
        Array.Copy(arguments, invokeArguments, arguments.Length);
        for (var index = arguments.Length; index < parameters.Length; index++)
        {
            invokeArguments[index] = parameters[index].DefaultValue;
        }

        var result = method.Invoke(null, invokeArguments);
        Array.Copy(invokeArguments, arguments, arguments.Length);

        return result;
    }
}
