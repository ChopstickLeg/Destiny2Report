namespace Destiny2Report.API.Features.Reports;

using D2Report.BungieClient;
using Destiny2Report.API.Features.Auth;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class ReportHandlers
{
    private const int AllMembershipTypes = 254;
    private const int StoryShareTokenBytes = 32;
    private static readonly TimeSpan ForegroundCrawlCooldown = TimeSpan.FromHours(6);
    private static readonly TimeSpan QueueScanFallbackInterval = TimeSpan.FromSeconds(5);
    private sealed record QueueAdmission(string StreamEntryId, string Status, DateTimeOffset UpdatedAtUtc);
    private const string QueueCrawlScript = """
        local currentStatus = redis.call('HGET', KEYS[2], 'status')
        if currentStatus == 'running' then
            return {
                redis.call('HGET', KEYS[2], 'streamEntryId'),
                currentStatus,
                redis.call('HGET', KEYS[2], 'updatedAtUtc')
            }
        end

        if currentStatus == 'queued' then
            local currentJobId = redis.call('HGET', KEYS[2], 'streamEntryId')
            if currentJobId and #redis.call('XRANGE', KEYS[1], currentJobId, currentJobId, 'COUNT', 1) > 0 then
                return {
                    currentJobId,
                    currentStatus,
                    redis.call('HGET', KEYS[2], 'updatedAtUtc')
                }
            end
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

    public static async Task<Ok<StoryVisualAssetsReport>> GetStoryVisualAssets(
        ICrawlerReadService crawlerService,
        CancellationToken cancellationToken)
    {
        var assets = await crawlerService
            .GetStoryVisualAssetsAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(assets);
    }

    public static async Task<Results<Ok<CreateStoryShareResponse>, UnauthorizedHttpResult, NotFound, BadRequest<ProblemDetails>, StatusCodeHttpResult>> CreateStoryShare(
        CreateStoryShareRequest request,
        HttpRequest httpRequest,
        IAuthSessionStore sessionStore,
        IBungieAuthService authService,
        TimeProvider timeProvider,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(request.MembershipTypeId, request.MembershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var session = await sessionStore.GetAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return TypedResults.Unauthorized();
        }

        SignedInPlayerResponse player;
        try
        {
            var refreshedSession = false;
            if (AuthSessionRefresh.IsRequired(session, timeProvider))
            {
                session = await AuthSessionRefresh.RefreshAsync(
                        httpRequest,
                        session,
                        authService,
                        sessionStore,
                        timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
                refreshedSession = true;
            }

            player = await authService.GetCurrentUserAsync(session.AccessToken, cancellationToken).ConfigureAwait(false);
            if (!player.SignedIn && !refreshedSession)
            {
                session = await AuthSessionRefresh.RefreshAsync(
                        httpRequest,
                        session,
                        authService,
                        sessionStore,
                        timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
                player = await authService.GetCurrentUserAsync(session.AccessToken, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (BungieAuthException ex) when (
            ex.Error is "invalid_oauth_request" or "bungie_session_expired"
            || ex.BungieStatusCode is System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
        {
            return TypedResults.Unauthorized();
        }
        catch (BungieAuthException)
        {
            return TypedResults.StatusCode(StatusCodes.Status502BadGateway);
        }

        if (!OwnsStoryMembership(player, request.MembershipTypeId, request.MembershipId))
        {
            return TypedResults.Unauthorized();
        }

        var report = await FindReportAsync(
                mongoDatabase,
                request.MembershipTypeId,
                request.MembershipId,
                cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return TypedResults.NotFound();
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(StoryShareTokenBytes));
        var share = new StoryShare
        {
            TokenHash = HashStoryShareToken(token),
            MembershipTypeId = request.MembershipTypeId,
            MembershipId = request.MembershipId,
            CreatedAtUtc = DateTime.UtcNow
        };

        await mongoDatabase.GetCollection<StoryShare>("story_shares")
            .InsertOneAsync(share, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new CreateStoryShareResponse(token));
    }

    public static async Task<Results<Ok<StoryShareIdentityResponse>, NotFound>> ResolveStoryShare(
        string token,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        if (!IsValidStoryShareToken(token))
        {
            return TypedResults.NotFound();
        }

        var shares = mongoDatabase.GetCollection<StoryShare>("story_shares");
        var share = await shares.Find(item => item.TokenHash == HashStoryShareToken(token))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return share is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new StoryShareIdentityResponse(share.MembershipTypeId, share.MembershipId));
    }

    internal static bool IsValidStoryShareToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 43)
        {
            return false;
        }

        try
        {
            return WebEncoders.Base64UrlDecode(token).Length == StoryShareTokenBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static string HashStoryShareToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    internal static bool OwnsStoryMembership(
        SignedInPlayerResponse player,
        int membershipTypeId,
        long membershipId)
    {
        return player.SignedIn && player.DestinyMemberships.Any(membership =>
            membership.MembershipType == membershipTypeId
            && membership.MembershipId == membershipId);
    }

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
        ICrawlGenerationStore generationStore,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(membershipTypeId, membershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var report = await generationStore.ReadReportAsync(membershipTypeId, membershipId, cancellationToken)
            .ConfigureAwait(false)
            ?? await FindReportAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
                .ConfigureAwait(false);

        return report is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(report);
    }

    public static async Task<Results<Ok<WeaponActivityModeAggregateReport>, NotFound, BadRequest<ProblemDetails>>> GetWeapons(
        int membershipTypeId,
        long membershipId,
        WeaponActivityMode activityMode,
        ICrawlerReadService crawlerService,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(membershipTypeId, membershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var response = await crawlerService
            .GetWeaponActivityModeReportAsync(membershipTypeId, membershipId, activityMode, cancellationToken)
            .ConfigureAwait(false);

        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
    }

    public static async Task<Results<Ok<DeathActivityModeAggregateReport>, NotFound, BadRequest<ProblemDetails>>> GetDeaths(
        int membershipTypeId,
        long membershipId,
        DeathActivityMode activityMode,
        ICrawlerReadService crawlerService,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(membershipTypeId, membershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var response = await crawlerService
            .GetDeathActivityModeReportAsync(membershipTypeId, membershipId, activityMode, cancellationToken)
            .ConfigureAwait(false);

        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
    }

    public static async Task<Results<Ok<ActivityPlaytimeAggregateReport>, NotFound, BadRequest<ProblemDetails>>> GetPlaytime(
        int membershipTypeId,
        long membershipId,
        ActivityPlaytimeMode activityMode,
        ICrawlerReadService crawlerService,
        CancellationToken cancellationToken)
    {
        if (!TryValidateMembership(membershipTypeId, membershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var response = await crawlerService
            .GetActivityPlaytimeReportAsync(membershipTypeId, membershipId, activityMode, cancellationToken)
            .ConfigureAwait(false);
        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
    }

    public static async Task<IResult> QueueCrawl(
        IReadOnlyList<ReportQueueRequest> requests,
        ICrawlerJobQueue crawlerJobQueue,
        IMongoDatabase mongoDatabase,
        HttpResponse httpResponse,
        CancellationToken cancellationToken)
    {
        if (!TryValidateQueueRequests(requests, out var memberships, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        System.Diagnostics.Activity.Current?.SetTag("destiny.membership_count", memberships.Count);

        foreach (var (membershipTypeId, membershipId) in memberships)
        {
            var retryAfter = await GetBatchCrawlRetryAfterAsync(
                    mongoDatabase,
                    membershipTypeId,
                    membershipId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (retryAfter is not null)
            {
                return BuildCrawlCooldownResponse(httpResponse, retryAfter.Value);
            }
        }

        var responses = new List<ReportQueueResponse>(memberships.Count);
        foreach (var (membershipTypeId, membershipId) in memberships)
        {
            responses.Add(await crawlerJobQueue.EnqueueAsync(
                    membershipTypeId,
                    membershipId,
                    forceFullCrawl: false,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return TypedResults.Accepted<IReadOnlyList<ReportQueueResponse>>((string?)null, responses);
    }

    private static async Task<ReportQueueResponse> QueueCrawlAsync(
        IDatabase redisDatabase,
        IMongoDatabase mongoDatabase,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var queuedAtUtc = DateTimeOffset.UtcNow;
        var existingStatus = await GetStoredQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existingStatus is not null && existingStatus.Status == DestinyReport.CrawlStateRunning)
        {
            return new ReportQueueResponse(
                JobId: existingStatus.StreamEntryId ?? "",
                MembershipTypeId: existingStatus.MembershipTypeId,
                MembershipId: existingStatus.MembershipId,
                Status: existingStatus.Status,
                QueuedAtUtc: existingStatus.UpdatedAtUtc);
        }

        if (existingStatus is not null
            && existingStatus.Status == DestinyReport.CrawlStateQueued
            && existingStatus.Position is not null)
        {
            await MarkReportAsForegroundQueuedAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
                .ConfigureAwait(false);
            return new ReportQueueResponse(
                JobId: existingStatus.StreamEntryId ?? "",
                MembershipTypeId: existingStatus.MembershipTypeId,
                MembershipId: existingStatus.MembershipId,
                Status: existingStatus.Status,
                QueuedAtUtc: existingStatus.UpdatedAtUtc);
        }

        var existingReport = await FindReportAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
            .ConfigureAwait(false);
        if (existingReport?.CrawlState == DestinyReport.CrawlStateRunning)
        {
            return new ReportQueueResponse(
                JobId: "",
                MembershipTypeId: membershipTypeId,
                MembershipId: membershipId,
                Status: DestinyReport.CrawlStateRunning,
                QueuedAtUtc: existingReport.StartedAtUtc ?? queuedAtUtc);
        }

        await UpsertForegroundQueuedReportAsync(mongoDatabase, membershipTypeId, membershipId, queuedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        QueueAdmission admission;
        try
        {
            admission = await EnqueueCrawlAtomicallyAsync(
                    redisDatabase,
                    membershipTypeId,
                    membershipId,
                    queuedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await MarkReportAsBackgroundQueuedAsync(
                    mongoDatabase,
                    membershipTypeId,
                    membershipId,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        var response = new ReportQueueResponse(
            JobId: admission.StreamEntryId,
            MembershipTypeId: membershipTypeId,
            MembershipId: membershipId,
            Status: admission.Status,
            QueuedAtUtc: admission.UpdatedAtUtc);

        return response;
    }

    private static async Task<TimeSpan?> GetBatchCrawlRetryAfterAsync(
        IMongoDatabase mongoDatabase,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var existingReport = await FindReportAsync(
                mongoDatabase,
                membershipTypeId,
                membershipId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingReport?.CrawlState is DestinyReport.CrawlStateQueued or DestinyReport.CrawlStateRunning)
        {
            return null;
        }

        var lastCrawledAtUtc = existingReport?.LastCrawledAtUtc;
        if (lastCrawledAtUtc is null && existingReport?.CrawlState == DestinyReport.CrawlStateCompleted)
        {
            lastCrawledAtUtc = existingReport.CrawledAt;
        }

        return GetForegroundCrawlRetryAfter(
            lastCrawledAtUtc,
            existingReport?.NeedsFullRecrawl == true,
            DateTimeOffset.UtcNow);
    }

    private static IResult BuildCrawlCooldownResponse(HttpResponse httpResponse, TimeSpan retryAfter)
    {
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        httpResponse.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return TypedResults.Problem(
            title: "Report crawl cooldown",
            detail: $"This profile was crawled recently. Try again in {FormatCooldown(retryAfter)}.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "crawl_cooldown",
                ["retryAfterSeconds"] = retryAfterSeconds
            });
    }

    private static TimeSpan? GetForegroundCrawlRetryAfter(
        DateTime? lastCrawledAtUtc,
        bool needsFullRecrawl,
        DateTimeOffset now)
    {
        if (lastCrawledAtUtc is null || needsFullRecrawl)
        {
            return null;
        }

        var lastCrawled = new DateTimeOffset(DateTime.SpecifyKind(lastCrawledAtUtc.Value, DateTimeKind.Utc));
        var retryAfter = lastCrawled.Add(ForegroundCrawlCooldown) - now;
        return retryAfter > TimeSpan.Zero ? retryAfter : null;
    }

    private static string FormatCooldown(TimeSpan retryAfter)
    {
        if (retryAfter.TotalHours >= 1)
        {
            var hours = (int)Math.Floor(retryAfter.TotalHours);
            var minutes = retryAfter.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes))}m";
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
        var redisStatus = await GetStoredQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        redisStatus ??= await GetQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var job = await FindCrawlJobAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
            .ConfigureAwait(false);
        var initialStatus = ReconcileQueueStatus(job, redisStatus);

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
                }

                if (DateTimeOffset.UtcNow < nextFallbackScanAt)
                {
                    continue;
                }

                nextFallbackScanAt = DateTimeOffset.UtcNow.Add(QueueScanFallbackInterval);

                var redisStatus = await GetStoredQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                redisStatus ??= await GetQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                var crawlJob = await FindCrawlJobAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
                    .ConfigureAwait(false);
                var status = ReconcileQueueStatus(crawlJob, redisStatus);
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
            .Set(report => report.QueuedAtUtc, queuedAtUtc.UtcDateTime)
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

    private static async Task MarkReportAsForegroundQueuedAsync(
        IMongoDatabase mongoDatabase,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var filter = Builders<DestinyReport>.Filter.Eq(item => item.PlatformId, membershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, membershipId)
            & Builders<DestinyReport>.Filter.Eq(item => item.CrawlState, DestinyReport.CrawlStateQueued);
        var update = Builders<DestinyReport>.Update.Set(item => item.QueuedInRedis, true);

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

    private static async Task<CrawlJob?> FindCrawlJobAsync(
        IMongoDatabase mongoDatabase,
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        var key = CrawlJob.CreatePlayerKey(membershipTypeId, membershipId);
        return await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs")
            .Find(job => job.PlayerKey == key)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static ReportQueueStatusResponse? ReconcileQueueStatus(
        CrawlJob? job,
        ReportQueueStatusResponse? redisStatus)
    {
        if (job is null)
        {
            return redisStatus;
        }

        var jobStatus = BuildQueueStatusFromJob(job);
        if (redisStatus is null
            || CrawlJob.IsTerminal(job.State)
            || IsTerminalCrawlState(redisStatus.Status)
            || !string.Equals(jobStatus.Status, redisStatus.Status, StringComparison.OrdinalIgnoreCase)
            || !job.DispatchedToRedis
            || (!string.IsNullOrWhiteSpace(job.StreamEntryId)
                && !string.Equals(job.StreamEntryId, redisStatus.StreamEntryId, StringComparison.Ordinal)))
        {
            return jobStatus;
        }

        return redisStatus;
    }

    private static ReportQueueStatusResponse BuildQueueStatusFromJob(CrawlJob job)
    {
        var publicState = job.State == CrawlJob.StateAwaitingFinalization ? CrawlJob.StateRunning : job.State;
        return BuildQueueStatus(
            job.MembershipTypeId,
            job.MembershipId,
            publicState,
            string.IsNullOrWhiteSpace(job.StreamEntryId) ? null : job.StreamEntryId,
            string.IsNullOrWhiteSpace(job.Error) ? null : job.Error,
            null,
            0,
            new DateTimeOffset(DateTime.SpecifyKind(job.UpdatedAtUtc, DateTimeKind.Utc)));
    }

    private static async Task<ReportQueueStatusResponse?> GetQueueStatusAsync(
        IDatabase redisDatabase,
        int membershipTypeId,
        long membershipId)
    {
        var entries = await LoadOrderedQueueEntriesAsync(redisDatabase).ConfigureAwait(false);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index].Entry;
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
        var progress = status == DestinyReport.CrawlStateRunning
            ? CrawlProgressSnapshot.FromFields(fields)
            : null;

        var updatedAtUtc = DateTimeOffset.TryParse(updatedAtUtcValue, out var parsedUpdatedAtUtc)
            ? parsedUpdatedAtUtc
            : DateTimeOffset.UtcNow;

        var storedStatus = BuildQueueStatus(
            membershipTypeId,
            membershipId,
            status,
            string.IsNullOrWhiteSpace(streamEntryId) ? null : streamEntryId,
            string.IsNullOrWhiteSpace(error) ? null : error,
            null,
            0,
            updatedAtUtc,
            progress);

        if (status != DestinyReport.CrawlStateQueued)
        {
            return storedStatus;
        }

        var entries = await LoadOrderedQueueEntriesAsync(redisDatabase).ConfigureAwait(false);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index].Entry;
            if (MatchesCrawlerJob(entry, membershipTypeId, membershipId))
            {
                return storedStatus with
                {
                    StreamEntryId = entry.Id.ToString(),
                    Position = index + 1,
                    QueueLength = entries.Length
                };
            }
        }

        return storedStatus;
    }

    private static async Task<(StreamEntry Entry, bool Priority)[]> LoadOrderedQueueEntriesAsync(IDatabase redisDatabase)
    {
        var priorityTask = redisDatabase.StreamRangeAsync(CrawlerQueue.PriorityStreamName);
        var normalTask = redisDatabase.StreamRangeAsync(CrawlerQueue.StreamName);
        await Task.WhenAll(priorityTask, normalTask).ConfigureAwait(false);
        return priorityTask.Result
            .OrderBy(entry => entry.Id)
            .Select(entry => (entry, true))
            .Concat(normalTask.Result.OrderBy(entry => entry.Id).Select(entry => (entry, false)))
            .ToArray();
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

    private static bool TryValidateQueueRequests(
        IReadOnlyList<ReportQueueRequest> requests,
        out IReadOnlyList<(int MembershipTypeId, long MembershipId)> memberships,
        out ProblemDetails problemDetails)
    {
        if (requests is null || requests.Count == 0)
        {
            memberships = [];
            problemDetails = new ProblemDetails
            {
                Title = "Missing memberships",
                Detail = "At least one membership must be supplied.",
                Status = StatusCodes.Status400BadRequest
            };
            return false;
        }

        var membershipsToQueue = new List<(int MembershipTypeId, long MembershipId)>(requests.Count);
        foreach (var request in requests)
        {
            if (!TryValidateMembership(request.MembershipTypeId, request.MembershipId, out problemDetails))
            {
                memberships = [];
                return false;
            }

            membershipsToQueue.Add((request.MembershipTypeId, request.MembershipId));
        }

        memberships = membershipsToQueue;
        problemDetails = new ProblemDetails();
        return true;
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
