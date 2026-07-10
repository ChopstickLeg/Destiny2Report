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
    private sealed record QueueAdmission(string StreamEntryId, string Status, DateTimeOffset UpdatedAtUtc);
    private const string QueueCrawlScript = """
        local currentStatus = redis.call('HGET', KEYS[2], 'status')
        if currentStatus == 'queued' or currentStatus == 'running' then
            return {
                redis.call('HGET', KEYS[2], 'streamEntryId'),
                currentStatus,
                redis.call('HGET', KEYS[2], 'updatedAtUtc')
            }
        end

        local jobId = redis.call('XADD', KEYS[1], '*',
            'membershipTypeId', ARGV[1],
            'membershipId', ARGV[2],
            'queuedAtUtc', ARGV[3])
        redis.call('HSET', KEYS[2],
            'membershipTypeId', ARGV[1],
            'membershipId', ARGV[2],
            'streamEntryId', jobId,
            'status', 'queued',
            'queuedAtUtc', ARGV[3],
            'updatedAtUtc', ARGV[3],
            'error', '',
            'progressPhase', '',
            'progressLabel', '',
            'progressCurrent', '',
            'progressTotal', '',
            'progressStartedAtUtc', '',
            'progressUpdatedAtUtc', '')
        redis.call('EXPIRE', KEYS[2], ARGV[4])
        return { jobId, 'queued', ARGV[3] }
        """;

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

    public static async Task<Results<Ok<WeaponActivityModeAggregateReport>, NotFound, BadRequest<ProblemDetails>>> GetWeapons(
        int membershipTypeId,
        long membershipId,
        WeaponActivityMode activityMode,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(membershipTypeId, membershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var storedActivityMode = ToStoredActivityMode(activityMode);
        var categories = mongoDatabase.GetCollection<WeaponCategoryAggregate>("weapon_category_aggregates");
        var weapons = mongoDatabase.GetCollection<WeaponAggregate>("weapon_aggregates");
        var categoryFilter = Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.OwnerMembershipType, membershipTypeId)
            & Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.OwnerMembershipId, membershipId)
            & Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.ActivityMode, storedActivityMode);
        var weaponFilter = Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipType, membershipTypeId)
            & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipId, membershipId)
            & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ActivityMode, storedActivityMode);

        var categoryAggregates = await categories
            .Find(categoryFilter)
            .SortByDescending(category => category.Kills)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (categoryAggregates.Count == 0)
        {
            return TypedResults.NotFound();
        }

        var weaponAggregates = await weapons
            .Find(weaponFilter)
            .SortBy(weapon => weapon.CategoryKey)
            .ThenByDescending(weapon => weapon.Kills)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var weaponsByClassModeAndCategory = weaponAggregates
            .GroupBy(weapon => (weapon.ClassName, weapon.SpecificActivityMode, weapon.CategoryKey))
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<WeaponAggregate>)group.ToList());
        var response = new WeaponActivityModeAggregateReport
        {
            ActivityMode = storedActivityMode,
            Classes = categoryAggregates
                .GroupBy(category => string.IsNullOrWhiteSpace(category.ClassName) ? "Unknown" : category.ClassName)
                .OrderByDescending(group => group.Sum(category => category.Kills))
                .Select(classGroup => new WeaponClassAggregateReport
                {
                    ClassName = classGroup.Key,
                    Modes = classGroup
                        .GroupBy(category => category.SpecificActivityMode)
                        .OrderByDescending(group => group.Sum(category => category.Kills))
                        .Select(modeGroup => new WeaponModeAggregateReport
                        {
                            SpecificActivityMode = CrawlerService.GetSpecificActivityModeName(modeGroup.Key),
                            Categories = modeGroup
                                .OrderByDescending(category => category.Kills)
                                .Select(category => new WeaponCategoryAggregateReport
                                {
                                    OwnerMembershipType = category.OwnerMembershipType,
                                    OwnerMembershipId = category.OwnerMembershipId,
                                    ActivityMode = category.ActivityMode,
                                    ClassName = classGroup.Key,
                                    SpecificActivityMode = CrawlerService.GetSpecificActivityModeName(category.SpecificActivityMode),
                                    CategoryKey = category.CategoryKey,
                                    CategoryName = category.CategoryName,
                                    Kills = category.Kills,
                                    Weapons = weaponsByClassModeAndCategory.GetValueOrDefault((category.ClassName, category.SpecificActivityMode, category.CategoryKey)) ?? []
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList()
        };

        return TypedResults.Ok(response);
    }

    public static async Task<Results<Accepted<ReportQueueResponse>, BadRequest<ProblemDetails>>> QueueCrawl(
        ReportQueueRequest request,
        IConnectionMultiplexer redis,
        IMongoDatabase mongoDatabase,
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

        if (existingStatus is not null && existingStatus.Status is DestinyReport.CrawlStateQueued or DestinyReport.CrawlStateRunning)
        {
            var existingResponse = new ReportQueueResponse(
                JobId: existingStatus.StreamEntryId ?? "",
                MembershipTypeId: existingStatus.MembershipTypeId,
                MembershipId: existingStatus.MembershipId,
                Status: existingStatus.Status,
                QueuedAtUtc: existingStatus.UpdatedAtUtc);

            return TypedResults.Accepted(QueueStatusLocation(request.MembershipTypeId, request.MembershipId), existingResponse);
        }

        var existingReport = await FindReportAsync(mongoDatabase, request.MembershipTypeId, request.MembershipId, cancellationToken)
            .ConfigureAwait(false);
        if (existingReport?.CrawlState == DestinyReport.CrawlStateRunning)
        {
            var existingResponse = new ReportQueueResponse(
                JobId: "",
                MembershipTypeId: request.MembershipTypeId,
                MembershipId: request.MembershipId,
                Status: DestinyReport.CrawlStateRunning,
                QueuedAtUtc: existingReport.StartedAtUtc ?? queuedAtUtc);

            return TypedResults.Accepted(QueueStatusLocation(request.MembershipTypeId, request.MembershipId), existingResponse);
        }

        await UpsertForegroundQueuedReportAsync(mongoDatabase, request.MembershipTypeId, request.MembershipId, queuedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        QueueAdmission admission;
        try
        {
            admission = await EnqueueCrawlAtomicallyAsync(
                    redisDatabase,
                    request.MembershipTypeId,
                    request.MembershipId,
                    queuedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await MarkReportAsBackgroundQueuedAsync(
                    mongoDatabase,
                    request.MembershipTypeId,
                    request.MembershipId,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        var response = new ReportQueueResponse(
            JobId: admission.StreamEntryId,
            MembershipTypeId: request.MembershipTypeId,
            MembershipId: request.MembershipId,
            Status: admission.Status,
            QueuedAtUtc: admission.UpdatedAtUtc);

        return TypedResults.Accepted(QueueStatusLocation(request.MembershipTypeId, request.MembershipId), response);
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

            initialStatus = BuildQueueStatusFromReport(report);
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
        if (IsTerminalCrawlState(initialStatus.Status))
        {
            yield return new SseItem<ReportQueueStatusResponse>(initialStatus, initialStatus.Status);
            yield break;
        }

        yield return new SseItem<ReportQueueStatusResponse>(initialStatus, "position");
        var subscriber = redis.GetSubscriber();
        var eventQueue = await subscriber.SubscribeAsync(RedisChannel.Literal(CrawlerQueue.EventsChannelName)).ConfigureAwait(false);
        using var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readEventTask = eventQueue.ReadAsync(subscriptionCancellation.Token).AsTask();
        var nextFallbackScanAt = DateTimeOffset.UtcNow.Add(QueueScanFallbackInterval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var fallbackDelayTask = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                var completedTask = await Task.WhenAny(readEventTask, fallbackDelayTask).ConfigureAwait(false);

                if (completedTask == readEventTask)
                {
                    var channelMessage = await readEventTask.ConfigureAwait(false);
                    readEventTask = eventQueue.ReadAsync(subscriptionCancellation.Token).AsTask();
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
                            jobEvent.UpdatedAtUtc,
                            jobEvent.Progress);

                        yield return new SseItem<ReportQueueStatusResponse>(eventStatus, jobEvent.Status);

                        if (IsTerminalCrawlState(jobEvent.Status))
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

                    if (IsTerminalCrawlState(status.Status))
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

                var reportStatus = BuildQueueStatusFromReport(report);
                yield return new SseItem<ReportQueueStatusResponse>(reportStatus, reportStatus.Status);
                if (IsTerminalCrawlState(reportStatus.Status))
                {
                    yield break;
                }
            }
        }
        finally
        {
            await subscriptionCancellation.CancelAsync().ConfigureAwait(false);
            await eventQueue.UnsubscribeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<QueueAdmission> EnqueueCrawlAtomicallyAsync(
        IDatabase redisDatabase,
        int membershipTypeId,
        long membershipId,
        DateTimeOffset queuedAtUtc,
        CancellationToken cancellationToken)
    {
        var queuedAtUtcValue = queuedAtUtc.ToString("O");
        var scriptResult = await redisDatabase.ScriptEvaluateAsync(
                QueueCrawlScript,
                [CrawlerQueue.StreamName, CrawlerQueue.JobStatusKey(membershipTypeId, membershipId)],
                [
                    membershipTypeId,
                    membershipId,
                    queuedAtUtcValue,
                    (long)CrawlerQueue.ActiveJobStatusTtl.TotalSeconds
                ])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = (RedisResult[]?)scriptResult;
        if (result is null || result.Length != 3)
        {
            throw new InvalidOperationException("Redis returned an invalid crawler queue admission response.");
        }

        var updatedAtUtc = DateTimeOffset.TryParse(result[2].ToString(), out var parsedUpdatedAtUtc)
            ? parsedUpdatedAtUtc
            : queuedAtUtc;
        return new QueueAdmission(result[0].ToString(), result[1].ToString(), updatedAtUtc);
    }

    private static async Task UpsertForegroundQueuedReportAsync(
        IMongoDatabase mongoDatabase,
        int membershipTypeId,
        long membershipId,
        DateTimeOffset queuedAtUtc,
        CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, membershipId);
        var update = Builders<DestinyReport>.Update
            .SetOnInsert(report => report.PlatformId, membershipTypeId)
            .SetOnInsert(report => report.PlayerMembershipId, membershipId)
            .Set(report => report.CrawlState, DestinyReport.CrawlStateQueued)
            .Set(report => report.QueuedInRedis, true)
            .Set(report => report.QueuedAtUtc, queuedAtUtc)
            .Set(report => report.LeaseExpiresAtUtc, null)
            .Set(report => report.LeaseOwner, "")
            .Set(report => report.CrawlError, "");

        await reports.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task MarkReportAsBackgroundQueuedAsync(
        IMongoDatabase mongoDatabase,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, membershipId)
            & Builders<DestinyReport>.Filter.Eq(item => item.CrawlState, DestinyReport.CrawlStateQueued);
        var update = Builders<DestinyReport>.Update.Set(item => item.QueuedInRedis, false);

        await reports.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static ReportQueueStatusResponse BuildQueueStatusFromReport(DestinyReport report)
    {
        var status = string.IsNullOrWhiteSpace(report.CrawlState)
            ? DestinyReport.CrawlStateCompleted
            : report.CrawlState;
        var updatedAtUtc = status switch
        {
            DestinyReport.CrawlStateQueued => report.QueuedAtUtc ?? report.CrawledAt,
            DestinyReport.CrawlStateRunning => report.StartedAtUtc ?? report.QueuedAtUtc ?? report.CrawledAt,
            DestinyReport.CrawlStateFailed => report.StartedAtUtc ?? report.QueuedAtUtc ?? report.CrawledAt,
            DestinyReport.CrawlStatePrivate => report.LastCrawledAtUtc ?? report.StartedAtUtc ?? report.QueuedAtUtc ?? report.CrawledAt,
            _ => report.LastCrawledAtUtc ?? report.CrawledAt
        };

        return BuildQueueStatus(
            report.PlatformId,
            report.PlayerMembershipId,
            status,
            null,
            string.IsNullOrWhiteSpace(report.CrawlError) ? null : report.CrawlError,
            null,
            0,
            updatedAtUtc);
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

    private static string ToStoredActivityMode(WeaponActivityMode activityMode)
    {
        return activityMode switch
        {
            WeaponActivityMode.PvP => "Crucible",
            WeaponActivityMode.PvE => "PvE",
            WeaponActivityMode.Gambit => "Gambit",
            _ => ""
        };
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
        var progress = BuildProgressSnapshot(fields);

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
            updatedAtUtc,
            progress);
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
        DateTimeOffset updatedAtUtc,
        CrawlProgressSnapshot? progress = null)
    {
        return new ReportQueueStatusResponse(
            MembershipTypeId: membershipTypeId,
            MembershipId: membershipId,
            Status: status,
            StreamEntryId: streamEntryId,
            Error: error,
            Position: position,
            QueueLength: queueLength,
            UpdatedAtUtc: updatedAtUtc,
            Progress: progress);
    }

    private static bool IsTerminalCrawlState(string status)
    {
        return status is DestinyReport.CrawlStateCompleted
            or DestinyReport.CrawlStateFailed
            or DestinyReport.CrawlStatePrivate;
    }


    private static CrawlProgressSnapshot? BuildProgressSnapshot(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("progressPhase", out var phase) || string.IsNullOrWhiteSpace(phase))
        {
            return null;
        }

        fields.TryGetValue("progressLabel", out var label);
        fields.TryGetValue("progressCurrent", out var currentValue);
        fields.TryGetValue("progressTotal", out var totalValue);
        fields.TryGetValue("progressStartedAtUtc", out var startedAtUtcValue);
        fields.TryGetValue("progressUpdatedAtUtc", out var progressUpdatedAtUtcValue);

        var current = long.TryParse(currentValue, out var parsedCurrent) ? parsedCurrent : (long?)null;
        var total = long.TryParse(totalValue, out var parsedTotal) ? parsedTotal : (long?)null;
        var startedAtUtc = DateTimeOffset.TryParse(startedAtUtcValue, out var parsedStartedAtUtc)
            ? parsedStartedAtUtc
            : DateTimeOffset.UtcNow;
        var updatedAtUtc = DateTimeOffset.TryParse(progressUpdatedAtUtcValue, out var parsedProgressUpdatedAtUtc)
            ? parsedProgressUpdatedAtUtc
            : startedAtUtc;

        return new CrawlProgressSnapshot(phase, label ?? phase, current, total, startedAtUtc, updatedAtUtc);
    }

    private static string QueueStatusLocation(int membershipTypeId, long membershipId)
    {
        return $"/api/reports/{membershipTypeId}/{membershipId}/queue";
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

        jobEvent = new ReportJobEvent(0, 0, "", null, null, DateTimeOffset.MinValue, null);
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
