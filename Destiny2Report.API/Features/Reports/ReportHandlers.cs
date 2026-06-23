namespace Destiny2Report.API.Features.Reports;

using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

public static class ReportHandlers
{
    private const int AllMembershipTypes = 254;
    private static readonly TimeSpan QueueScanFallbackInterval = TimeSpan.FromSeconds(5);

    public static async Task<Results<Ok<ReportSummaryResponse>, BadRequest<ProblemDetails>>> GetSummary(
        long membershipId,
        int? season,
        ID2ReportClient bungieClient,
        CancellationToken cancellationToken)
    {
        if (membershipId <= 0)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Invalid membership id",
                Detail = "membershipId must be a positive integer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var linkedProfilesResponse = await bungieClient
            .Destiny2_GetLinkedProfilesAsync(
                getAllMemberships: true,
                membershipId: membershipId,
                membershipType: AllMembershipTypes,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var linkedProfiles = linkedProfilesResponse.Response.Profiles ?? [];
        var displayName = GetDisplayName(linkedProfilesResponse.Response, membershipId);

        var response = new ReportSummaryResponse(
            MembershipId: membershipId,
            Season: season,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            DisplayName: displayName,
            ActivityCount: linkedProfiles.Count);

        return TypedResults.Ok(response);
    }

    public static async Task<Results<Ok<DestinyReport>, NotFound, BadRequest<ProblemDetails>>> GetReport(
        int membershipTypeId,
        long membershipId,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(membershipTypeId, membershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var report = await FindReportAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
            .ConfigureAwait(false);

        return report is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(report);
    }

    public static async Task<Results<Accepted<ReportQueueResponse>, BadRequest<ProblemDetails>>> QueueCrawl(
        ReportQueueRequest request,
        IConnectionMultiplexer redis,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(request.MembershipTypeId, request.MembershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        System.Diagnostics.Activity.Current?.SetTag("destiny.membership_type_id", request.MembershipTypeId);
        System.Diagnostics.Activity.Current?.SetTag("destiny.membership_id", request.MembershipId);

        var queuedAtUtc = DateTimeOffset.UtcNow;
        var redisDatabase = redis.GetDatabase();
        var existingStatus = await GetStoredQueueStatusAsync(redisDatabase, request.MembershipTypeId, request.MembershipId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existingStatus is not null && existingStatus.Status is "queued" or "running")
        {
            var existingResponse = new ReportQueueResponse(
                JobId: existingStatus.StreamEntryId ?? "",
                MembershipTypeId: existingStatus.MembershipTypeId,
                MembershipId: existingStatus.MembershipId,
                Status: existingStatus.Status,
                QueuedAtUtc: existingStatus.UpdatedAtUtc);

            return TypedResults.Accepted($"/api/reports/jobs/{existingResponse.JobId}", existingResponse);
        }

        var jobId = await redisDatabase.StreamAddAsync(
                CrawlerQueue.StreamName,
                [
                    new NameValueEntry("membershipTypeId", request.MembershipTypeId),
                    new NameValueEntry("membershipId", request.MembershipId),
                    new NameValueEntry("queuedAtUtc", queuedAtUtc.ToString("O"))
                ])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        await redisDatabase.HashSetAsync(
                CrawlerQueue.JobStatusKey(request.MembershipTypeId, request.MembershipId),
                [
                    new HashEntry("membershipTypeId", request.MembershipTypeId),
                    new HashEntry("membershipId", request.MembershipId),
                    new HashEntry("streamEntryId", jobId),
                    new HashEntry("status", "queued"),
                    new HashEntry("queuedAtUtc", queuedAtUtc.ToString("O")),
                    new HashEntry("updatedAtUtc", queuedAtUtc.ToString("O")),
                    new HashEntry("error", "")
                ])
            .ConfigureAwait(false);

        var response = new ReportQueueResponse(
            JobId: jobId.ToString(),
            MembershipTypeId: request.MembershipTypeId,
            MembershipId: request.MembershipId,
            Status: "queued",
            QueuedAtUtc: queuedAtUtc);

        return TypedResults.Accepted($"/api/reports/jobs/{jobId}", response);
    }

    public static async Task<IResult> StreamQueuePosition(
        int membershipTypeId,
        long membershipId,
        IConnectionMultiplexer redis,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(membershipTypeId, membershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var redisDatabase = redis.GetDatabase();
        var initialStatus = await GetStoredQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        initialStatus ??= await GetQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (initialStatus is null)
        {
            var report = await FindReportAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
                .ConfigureAwait(false);

            if (report is null)
            {
                return TypedResults.NotFound();
            }

            initialStatus = BuildQueueStatus(membershipTypeId, membershipId, "completed", null, 0);
        }

        var events = StreamQueuePositionEvents(
            redis,
            redisDatabase,
            mongoDatabase,
            membershipTypeId,
            membershipId,
            initialStatus,
            cancellationToken);

        return TypedResults.ServerSentEvents(events);
    }

    private static async IAsyncEnumerable<SseItem<ReportQueueStatusResponse>> StreamQueuePositionEvents(
        IConnectionMultiplexer redis,
        IDatabase redisDatabase,
        IMongoDatabase mongoDatabase,
        int membershipTypeId,
        long membershipId,
        ReportQueueStatusResponse initialStatus,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (initialStatus.Status == "completed")
        {
            yield return new SseItem<ReportQueueStatusResponse>(initialStatus, "completed");
            yield break;
        }

        yield return new SseItem<ReportQueueStatusResponse>(initialStatus, "position");
        var subscriber = redis.GetSubscriber();
        var eventQueue = await subscriber.SubscribeAsync(RedisChannel.Literal(CrawlerQueue.EventsChannelName)).ConfigureAwait(false);
        var nextFallbackScanAt = DateTimeOffset.UtcNow.Add(QueueScanFallbackInterval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var readEventTask = eventQueue.ReadAsync(cancellationToken).AsTask();
                var fallbackDelayTask = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                var completedTask = await Task.WhenAny(readEventTask, fallbackDelayTask).ConfigureAwait(false);

                if (completedTask == readEventTask)
                {
                    var channelMessage = await readEventTask.ConfigureAwait(false);
                    if (TryReadMatchingJobEvent(channelMessage.Message, membershipTypeId, membershipId, out var jobEvent))
                    {
                        var eventStatus = BuildQueueStatus(
                            membershipTypeId,
                            membershipId,
                            jobEvent.Status,
                            jobEvent.StreamEntryId,
                            jobEvent.Error,
                            null,
                            0,
                            jobEvent.UpdatedAtUtc);

                        yield return new SseItem<ReportQueueStatusResponse>(eventStatus, jobEvent.Status);

                        if (jobEvent.Status is "completed" or "failed")
                        {
                            yield break;
                        }
                    }

                    continue;
                }

                if (DateTimeOffset.UtcNow < nextFallbackScanAt)
                {
                    continue;
                }

                nextFallbackScanAt = DateTimeOffset.UtcNow.Add(QueueScanFallbackInterval);

                var status = await GetStoredQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                status ??= await GetQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (status is not null)
                {
                    yield return new SseItem<ReportQueueStatusResponse>(status, status.Status);

                    if (status.Status is "completed" or "failed")
                    {
                        yield break;
                    }

                    continue;
                }

                var report = await FindReportAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
                    .ConfigureAwait(false);

                if (report is null)
                {
                    yield return new SseItem<ReportQueueStatusResponse>(
                        BuildQueueStatus(membershipTypeId, membershipId, "not_found", null, 0),
                        "not_found");
                    yield break;
                }

                yield return new SseItem<ReportQueueStatusResponse>(
                    BuildQueueStatus(membershipTypeId, membershipId, "completed", null, 0),
                    "completed");
                yield break;
            }
        }
        finally
        {
            await eventQueue.UnsubscribeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<DestinyReport?> FindReportAsync(
        IMongoDatabase mongoDatabase,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, membershipId);

        return await reports.Find(filter)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ReportQueueStatusResponse?> GetQueueStatusAsync(
        IDatabase redisDatabase,
        int membershipTypeId,
        long membershipId)
    {
        var entries = await redisDatabase.StreamRangeAsync(CrawlerQueue.StreamName).ConfigureAwait(false);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (!MatchesCrawlerJob(entry, membershipTypeId, membershipId))
            {
                continue;
            }

            return BuildQueueStatus(
                membershipTypeId,
                membershipId,
                "queued",
                entry.Id.ToString(),
                null,
                index + 1,
                entries.Length,
                DateTimeOffset.UtcNow);
        }

        return null;
    }

    private static async Task<ReportQueueStatusResponse?> GetStoredQueueStatusAsync(
        IDatabase redisDatabase,
        int membershipTypeId,
        long membershipId)
    {
        var values = await redisDatabase.HashGetAllAsync(CrawlerQueue.JobStatusKey(membershipTypeId, membershipId))
            .ConfigureAwait(false);

        if (values.Length == 0)
        {
            return null;
        }

        var fields = values.ToDictionary(item => item.Name.ToString(), item => item.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("status", out var status) || string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        fields.TryGetValue("streamEntryId", out var streamEntryId);
        fields.TryGetValue("error", out var error);
        fields.TryGetValue("updatedAtUtc", out var updatedAtUtcValue);

        var updatedAtUtc = DateTimeOffset.TryParse(updatedAtUtcValue, out var parsedUpdatedAtUtc)
            ? parsedUpdatedAtUtc
            : DateTimeOffset.UtcNow;

        return BuildQueueStatus(
            membershipTypeId,
            membershipId,
            status,
            string.IsNullOrWhiteSpace(streamEntryId) ? null : streamEntryId,
            string.IsNullOrWhiteSpace(error) ? null : error,
            null,
            0,
            updatedAtUtc);
    }

    private static ReportQueueStatusResponse BuildQueueStatus(
        int membershipTypeId,
        long membershipId,
        string status,
        string? streamEntryId,
        long queueLength)
    {
        return BuildQueueStatus(membershipTypeId, membershipId, status, streamEntryId, null, null, queueLength, DateTimeOffset.UtcNow);
    }

    private static ReportQueueStatusResponse BuildQueueStatus(
        int membershipTypeId,
        long membershipId,
        string status,
        string? streamEntryId,
        string? error,
        long? position,
        long queueLength,
        DateTimeOffset updatedAtUtc)
    {
        return new ReportQueueStatusResponse(
            MembershipTypeId: membershipTypeId,
            MembershipId: membershipId,
            Status: status,
            StreamEntryId: streamEntryId,
            Error: error,
            Position: position,
            QueueLength: queueLength,
            UpdatedAtUtc: updatedAtUtc);
    }

    private static bool TryReadMatchingJobEvent(
        RedisValue message,
        int membershipTypeId,
        long membershipId,
        out ReportJobEvent jobEvent)
    {
        try
        {
            var parsedEvent = JsonSerializer.Deserialize<ReportJobEvent>(message.ToString());
            if (parsedEvent is not null
                && parsedEvent.MembershipTypeId == membershipTypeId
                && parsedEvent.MembershipId == membershipId)
            {
                jobEvent = parsedEvent;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        jobEvent = new ReportJobEvent(0, 0, "", null, null, DateTimeOffset.MinValue);
        return false;
    }

    private static bool MatchesCrawlerJob(StreamEntry entry, int membershipTypeId, long membershipId)
    {
        var entryMembershipTypeId = entry.Values.FirstOrDefault(value => value.Name == "membershipTypeId").Value;
        var entryMembershipId = entry.Values.FirstOrDefault(value => value.Name == "membershipId").Value;

        return int.TryParse(entryMembershipTypeId.ToString(), out var parsedMembershipTypeId)
            && long.TryParse(entryMembershipId.ToString(), out var parsedMembershipId)
            && parsedMembershipTypeId == membershipTypeId
            && parsedMembershipId == membershipId;
    }

    private static bool TryValidateMembership(int membershipTypeId, long membershipId, out ProblemDetails problemDetails)
    {
        if (membershipTypeId <= 0)
        {
            problemDetails = new ProblemDetails
            {
                Title = "Invalid membership type id",
                Detail = "membershipTypeId must be a positive integer.",
                Status = StatusCodes.Status400BadRequest
            };
            return false;
        }

        if (membershipId <= 0)
        {
            problemDetails = new ProblemDetails
            {
                Title = "Invalid membership id",
                Detail = "membershipId must be a positive integer.",
                Status = StatusCodes.Status400BadRequest
            };
            return false;
        }

        problemDetails = new ProblemDetails();
        return true;
    }

    private static string GetDisplayName(DestinyLinkedProfilesResponse response, long membershipId)
    {
        var profile = response.Profiles?
            .FirstOrDefault(profile => profile.MembershipId == membershipId)
            ?? response.Profiles?.FirstOrDefault(profile => profile.IsCrossSavePrimary)
            ?? response.Profiles?.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(profile?.BungieGlobalDisplayName))
        {
            return profile.BungieGlobalDisplayName;
        }

        if (!string.IsNullOrWhiteSpace(profile?.DisplayName))
        {
            return profile.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(response.BnetMembership?.DisplayName))
        {
            return response.BnetMembership.DisplayName;
        }

        return membershipId.ToString();
    }
}
