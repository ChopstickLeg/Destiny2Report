using System.Reflection;
using Destiny2Report.API.Features.Reports;
using Microsoft.AspNetCore.Mvc;
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

    private static object? Invoke(string methodName, params object?[] arguments)
    {
        var method = HandlerType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.Name == methodName)
            .Single(method => method.GetParameters().Length == arguments.Length);

        return method.Invoke(null, arguments);
    }
}
