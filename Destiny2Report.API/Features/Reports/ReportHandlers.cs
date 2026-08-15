namespace Destiny2Report.API.Features.Reports;

using D2Report.BungieClient;
using Destiny2Report.API.Features.Auth;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Destiny2Report.API.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
    // Redis pub/sub carries ordinary updates. This is only a low-frequency safety net
    // for a missed event or a browser/proxy that did not observe the final event.
    private static readonly TimeSpan QueueScanFallbackInterval = TimeSpan.FromMinutes(2);

    public static Ok<QueuePolicyResponse> GetQueuePolicy(IOptions<QueueAdmissionOptions> options) =>
        TypedResults.Ok(new QueuePolicyResponse(options.Value.Enabled));

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
        ReportQueueRequest request,
        ICrawlerJobQueue crawlerJobQueue,
        ITurnstileVerifier turnstile,
        IQueueAdmissionService queueAdmission,
        IMongoDatabase mongoDatabase,
        HttpResponse httpResponse,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var clientDiagnostics = ClientRateLimitPartition.GetDiagnostics(httpContext);
        System.Diagnostics.Activity.Current?.SetTag("destiny.client_ip", clientDiagnostics.PartitionKey);
        System.Diagnostics.Activity.Current?.SetTag("destiny.client_ip_source", clientDiagnostics.Source);

        if (!TryValidateMembership(request.MembershipTypeId, request.MembershipId, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        System.Diagnostics.Activity.Current?.SetTag("destiny.membership_count", 1);
        if (!await turnstile.VerifyAsync(
                request.TurnstileToken,
                clientDiagnostics.PartitionKey,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return BuildTurnstileFailureResponse();
        }

        var admissionIdentity = await queueAdmission.ResolveIdentityAsync(
                httpContext.Request,
                httpContext.Response,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admissionIdentity.Allowed)
        {
            return BuildQueueAdmissionFailureResponse(httpResponse, admissionIdentity.Failure, null);
        }

        if (admissionIdentity.BungieMembershipId is long bungieMembershipId)
        {
            System.Diagnostics.Activity.Current?.SetTag("destiny.bungie_membership_id", bungieMembershipId);
        }

        await using var targetLease = await queueAdmission.AcquireTargetLeaseAsync(
                admissionIdentity,
                request.MembershipTypeId,
                request.MembershipId,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetLease is null)
        {
            return BuildQueueAdmissionFailureResponse(
                httpResponse,
                QueueAdmissionFailure.AdmissionUnavailable,
                null);
        }

        var activeJob = await crawlerJobQueue.TryGetActiveAsync(
                request.MembershipTypeId,
                request.MembershipId,
                cancellationToken)
            .ConfigureAwait(false);
        if (activeJob is not null)
        {
            return TypedResults.Accepted((string?)null, activeJob);
        }

        var existingReport = await FindReportAsync(
                mongoDatabase,
                request.MembershipTypeId,
                request.MembershipId,
                cancellationToken)
            .ConfigureAwait(false);
        var retryAfter = GetBatchCrawlRetryAfter(existingReport);
        if (retryAfter is not null)
        {
            return BuildCrawlCooldownResponse(httpResponse, retryAfter.Value);
        }

        var admission = await queueAdmission.ReserveAsync(
                admissionIdentity,
                existingReport is null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.Allowed)
        {
            return BuildQueueAdmissionFailureResponse(httpResponse, admission.Failure, admission.RetryAfter);
        }

        var keepCharge = false;
        try
        {
            // A client disconnect after quota reservation must not leave the
            // durable-write outcome ambiguous or burn quota without a job.
            var enqueueResult = await crawlerJobQueue.EnqueueTrackedAsync(
                    request.MembershipTypeId,
                    request.MembershipId,
                    forceFullCrawl: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
            keepCharge = enqueueResult.CreatedNewJob;
            return TypedResults.Accepted((string?)null, enqueueResult.Response);
        }
        finally
        {
            await queueAdmission.CompleteAsync(
                    admission.Reservation,
                    keepCharge,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static IResult BuildTurnstileFailureResponse() => TypedResults.Problem(
        title: "Security verification failed",
        detail: "Complete the security check before requesting a report.",
        statusCode: StatusCodes.Status403Forbidden,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "turnstile_verification_failed"
        });

    private static TimeSpan? GetBatchCrawlRetryAfter(DestinyReport? existingReport)
    {
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

    private static IResult BuildQueueAdmissionFailureResponse(
        HttpResponse httpResponse,
        QueueAdmissionFailure failure,
        TimeSpan? retryAfter)
    {
        if (failure == QueueAdmissionFailure.AuthenticationRequired)
        {
            return TypedResults.Problem(
                title: "Sign in to queue this report",
                detail: "You need to sign in with Bungie to generate a new report or refresh an existing one. You can still view existing reports without signing in.",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "queue_authentication_required"
                });
        }

        if (failure == QueueAdmissionFailure.AccountBlocked)
        {
            return TypedResults.Problem(
                title: "Queue access blocked",
                detail: "This Bungie account is not allowed to request report crawls.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "queue_account_blocked"
                });
        }

        if (failure is QueueAdmissionFailure.AuthenticationUnavailable or QueueAdmissionFailure.AdmissionUnavailable)
        {
            return TypedResults.Problem(
                title: "Queue admission unavailable",
                detail: "The service could not verify queue access. Try again shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "queue_admission_unavailable"
                });
        }

        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((retryAfter ?? TimeSpan.FromMinutes(1)).TotalSeconds));
        httpResponse.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var retryAfterText = FormatCooldown(TimeSpan.FromSeconds(retryAfterSeconds));
        var (code, title, detail) = failure switch
        {
            QueueAdmissionFailure.AccountDailyLimit => (
                "queue_account_daily_limit",
                "Daily report limit reached",
                $"Your Bungie account has used its daily report-request allowance. You can queue another report in {retryAfterText}."),
            QueueAdmissionFailure.AccountNewReportDailyLimit => (
                "queue_account_new_report_daily_limit",
                "Daily new-report limit reached",
                $"Your Bungie account has used its daily allowance for players whose reports have not been generated before. You can queue another new report in {retryAfterText}."),
            QueueAdmissionFailure.GlobalHourlyLimit => (
                "queue_global_hourly_limit",
                "Report queue is temporarily full",
                $"The shared crawler has reached its site-wide hourly capacity. Your account is not blocked; please try again in {retryAfterText}."),
            QueueAdmissionFailure.GlobalNewReportDailyLimit => (
                "queue_global_new_report_daily_limit",
                "New-report queue is full for today",
                $"The shared crawler has reached its site-wide daily capacity for players without an existing report. Your account is not blocked; please try again in {retryAfterText}."),
            _ => (
                "queue_admission_denied",
                "Report cannot be queued right now",
                $"The report queue cannot accept this request right now. Please try again in {retryAfterText}.")
        };

        return TypedResults.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["retryAfterSeconds"] = retryAfterSeconds
            });
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
        QueueEventBroker queueEventBroker,
        QueueStreamMetrics queueStreamMetrics,
        IQueuePositionSnapshotService queuePositions,
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

        // The Redis status is updated alongside the published queue event. Avoid a
        // MongoDB read for every new SSE connection while that status is present.
        var initialStatus = redisStatus;

        if (initialStatus is null)
        {
            var job = await FindCrawlJobAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
                .ConfigureAwait(false);
            initialStatus = ReconcileQueueStatus(job, redisStatus);

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
        }

        initialStatus = await AddQueuePositionAsync(
                queuePositions,
                membershipTypeId,
                membershipId,
                initialStatus,
                cancellationToken)
            .ConfigureAwait(false);
        if (initialStatus is null)
        {
            return TypedResults.NotFound();
        }

        var events = StreamQueuePositionEvents(
            redisDatabase,
            mongoDatabase,
            queueEventBroker,
            queueStreamMetrics,
            queuePositions,
            membershipTypeId,
            membershipId,
            initialStatus,
            cancellationToken);

        return TypedResults.ServerSentEvents(events);
    }

    private static async IAsyncEnumerable<SseItem<ReportQueueStatusResponse>> StreamQueuePositionEvents(
        IDatabase redisDatabase,
        IMongoDatabase mongoDatabase,
        QueueEventBroker queueEventBroker,
        QueueStreamMetrics queueStreamMetrics,
        IQueuePositionSnapshotService queuePositions,
        int membershipTypeId,
        long membershipId,
        ReportQueueStatusResponse initialStatus,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var streamLease = queueStreamMetrics.TrackStream();
        if (IsTerminalCrawlState(initialStatus.Status))
        {
            yield return new SseItem<ReportQueueStatusResponse>(initialStatus, initialStatus.Status);
            yield break;
        }

        yield return new SseItem<ReportQueueStatusResponse>(initialStatus, "position");
        using var subscriptionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var eventSubscription = queueEventBroker.Subscribe();
        var readEventTask = eventSubscription.ReadAsync(subscriptionCancellation.Token).AsTask();
        var fallbackDelayTask = Task.Delay(QueueScanFallbackInterval, cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var completedTask = await Task.WhenAny(readEventTask, fallbackDelayTask).ConfigureAwait(false);

                if (completedTask == readEventTask)
                {
                    var channelMessage = await readEventTask.ConfigureAwait(false);
                    readEventTask = eventSubscription.ReadAsync(subscriptionCancellation.Token).AsTask();
                    if (TryReadMatchingJobEvent(channelMessage, membershipTypeId, membershipId, out var jobEvent))
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

                        eventStatus = await AddQueuePositionAsync(
                                queuePositions,
                                membershipTypeId,
                                membershipId,
                                eventStatus,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (eventStatus is null)
                        {
                            continue;
                        }

                        yield return new SseItem<ReportQueueStatusResponse>(eventStatus, jobEvent.Status);

                        if (IsTerminalCrawlState(jobEvent.Status))
                        {
                            yield break;
                        }
                    }
                }

                if (completedTask != fallbackDelayTask)
                {
                    continue;
                }

                fallbackDelayTask = Task.Delay(QueueScanFallbackInterval, cancellationToken);

                var redisStatus = await GetStoredQueueStatusAsync(redisDatabase, membershipTypeId, membershipId)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                var status = redisStatus;
                if (status is not null)
                {
                    status = await AddQueuePositionAsync(
                            queuePositions,
                            membershipTypeId,
                            membershipId,
                            status,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                if (status is not null)
                {
                    yield return new SseItem<ReportQueueStatusResponse>(status, status.Status);
                    if (IsTerminalCrawlState(status.Status))
                    {
                        yield break;
                    }
                    continue;
                }

                // Redis status is normally present for all active work. Only consult
                // MongoDB when it has expired or the queue event state was lost.
                var crawlJob = await FindCrawlJobAsync(mongoDatabase, membershipTypeId, membershipId, cancellationToken)
                    .ConfigureAwait(false);
                status = ReconcileQueueStatus(crawlJob, null);
                status = await AddQueuePositionAsync(
                        queuePositions,
                        membershipTypeId,
                        membershipId,
                        status,
                        cancellationToken)
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
        }
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

    private static async Task<ReportQueueStatusResponse?> AddQueuePositionAsync(
        IQueuePositionSnapshotService queuePositions,
        int membershipTypeId,
        long membershipId,
        ReportQueueStatusResponse? status,
        CancellationToken cancellationToken)
    {
        if (status?.Status != DestinyReport.CrawlStateQueued)
        {
            return status;
        }

        var position = await queuePositions
            .GetPositionAsync(membershipTypeId, membershipId, cancellationToken)
            .ConfigureAwait(false);
        return position is null
            ? status
            : status with { Position = position.Position, QueueLength = position.QueueLength };
    }

    internal static FilterDefinition<CrawlJob> BuildQueueCohortFilter(CrawlJob job) =>
        Builders<CrawlJob>.Filter.Eq(item => item.State, CrawlJob.StateQueued)
        & Builders<CrawlJob>.Filter.Eq(item => item.DispatchedToRedis, job.DispatchedToRedis);

    internal static FilterDefinition<CrawlJob> BuildJobsAheadFilter(CrawlJob job)
    {
        var filters = Builders<CrawlJob>.Filter;
        var priority = filters.Eq(item => item.IsPriority, true);
        var normal = filters.Eq(item => item.IsPriority, false)
            | filters.Exists(item => item.IsPriority, false);
        var earlier = filters.Lt(item => item.QueuedAtUtc, job.QueuedAtUtc)
            | (filters.Eq(item => item.QueuedAtUtc, job.QueuedAtUtc)
                & filters.Lt(item => item.PlayerKey, job.PlayerKey));

        return job.IsPriority ? priority & earlier : priority | (normal & earlier);
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
