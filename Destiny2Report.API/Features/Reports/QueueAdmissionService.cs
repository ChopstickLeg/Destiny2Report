using Destiny2Report.API.Features.Auth;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Destiny2Report.API.Features.Reports;

public sealed class QueueAdmissionOptions
{
    public const string SectionName = "QueueAdmission";

    public bool Enabled { get; init; }

    public int MaxRequestsPerAccountPerDay { get; init; } = 25;

    public int MaxNewReportsPerAccountPerDay { get; init; } = 5;

    public int MaxRequestsGloballyPerHour { get; init; } = 100;

    public int MaxNewReportsGloballyPerDay { get; init; } = 250;

    public string BlockedBungieMembershipIds { get; init; } = "";

    public bool IsBlocked(long bungieMembershipId) => BlockedBungieMembershipIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(value => long.TryParse(value, out var blockedId) && blockedId == bungieMembershipId);

    public bool HasValidBlockedBungieMembershipIds() => BlockedBungieMembershipIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(value => long.TryParse(value, out var blockedId) && blockedId > 0);
}

public enum QueueAdmissionFailure
{
    None,
    AuthenticationRequired,
    AuthenticationUnavailable,
    AccountBlocked,
    AccountDailyLimit,
    AccountNewReportDailyLimit,
    GlobalHourlyLimit,
    GlobalNewReportDailyLimit,
    AdmissionUnavailable
}

public sealed record QueueAdmissionIdentity(
    bool EnforcementEnabled,
    long? BungieMembershipId,
    QueueAdmissionFailure Failure = QueueAdmissionFailure.None)
{
    public bool Allowed => Failure == QueueAdmissionFailure.None;
}

public sealed record QueueAdmissionDecision(
    QueueAdmissionFailure Failure = QueueAdmissionFailure.None,
    TimeSpan? RetryAfter = null)
{
    public bool Allowed => Failure == QueueAdmissionFailure.None;
}

public interface IQueueAdmissionService
{
    Task<QueueAdmissionIdentity> ResolveIdentityAsync(
        HttpRequest request,
        HttpResponse response,
        CancellationToken cancellationToken);

    Task<QueueAdmissionDecision> ReserveAsync(
        QueueAdmissionIdentity identity,
        bool isNewReport,
        CancellationToken cancellationToken);
}

public interface IQueueAdmissionQuotaStore
{
    Task<QueueAdmissionDecision> ReserveAsync(
        long bungieMembershipId,
        bool isNewReport,
        CancellationToken cancellationToken);
}

public sealed class QueueAdmissionService(
    IOptions<QueueAdmissionOptions> options,
    IAuthSessionStore sessionStore,
    IBungieAuthService authService,
    IQueueAdmissionQuotaStore quotaStore,
    TimeProvider timeProvider,
    ILogger<QueueAdmissionService> logger) : IQueueAdmissionService
{
    public async Task<QueueAdmissionIdentity> ResolveIdentityAsync(
        HttpRequest request,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return new QueueAdmissionIdentity(false, null);
        }

        var session = await sessionStore.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return AuthenticationRequired();
        }

        try
        {
            var refreshedSession = false;
            if (AuthSessionRefresh.IsRequired(session, timeProvider))
            {
                session = await AuthSessionRefresh.RefreshAsync(
                        request,
                        session,
                        authService,
                        sessionStore,
                        timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
                refreshedSession = true;
            }

            var player = await authService.GetCurrentUserAsync(session.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            if (!player.SignedIn && !refreshedSession)
            {
                session = await AuthSessionRefresh.RefreshAsync(
                        request,
                        session,
                        authService,
                        sessionStore,
                        timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
                player = await authService.GetCurrentUserAsync(session.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!player.SignedIn || player.BungieNetUser is null)
            {
                await sessionStore.DeleteAsync(request, response, cancellationToken).ConfigureAwait(false);
                return AuthenticationRequired();
            }

            var bungieMembershipId = player.BungieNetUser.MembershipId;
            if (options.Value.IsBlocked(bungieMembershipId))
            {
                logger.LogWarning(
                    "Blocked Bungie account {BungieMembershipId} attempted to queue a report.",
                    bungieMembershipId);
                return new QueueAdmissionIdentity(true, bungieMembershipId, QueueAdmissionFailure.AccountBlocked);
            }

            logger.LogInformation(
                "Queue admission identified Bungie account {BungieMembershipId}.",
                bungieMembershipId);
            return new QueueAdmissionIdentity(true, bungieMembershipId);
        }
        catch (BungieAuthException ex) when (
            ex.Error is "invalid_oauth_request" or "bungie_session_expired"
            || ex.BungieStatusCode is System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
        {
            await sessionStore.DeleteAsync(request, response, cancellationToken).ConfigureAwait(false);
            return AuthenticationRequired();
        }
        catch (BungieAuthException)
        {
            return new QueueAdmissionIdentity(true, null, QueueAdmissionFailure.AuthenticationUnavailable);
        }
    }

    public Task<QueueAdmissionDecision> ReserveAsync(
        QueueAdmissionIdentity identity,
        bool isNewReport,
        CancellationToken cancellationToken)
    {
        if (!identity.EnforcementEnabled)
        {
            return Task.FromResult(new QueueAdmissionDecision());
        }

        return identity.BungieMembershipId is long bungieMembershipId
            ? quotaStore.ReserveAsync(bungieMembershipId, isNewReport, cancellationToken)
            : Task.FromResult(new QueueAdmissionDecision(QueueAdmissionFailure.AuthenticationRequired));
    }

    private static QueueAdmissionIdentity AuthenticationRequired() =>
        new(true, null, QueueAdmissionFailure.AuthenticationRequired);
}

public sealed class RedisQueueAdmissionQuotaStore(
    IConnectionMultiplexer redis,
    IOptions<QueueAdmissionOptions> options,
    TimeProvider timeProvider,
    ILogger<RedisQueueAdmissionQuotaStore> logger) : IQueueAdmissionQuotaStore
{
    private const string KeyPrefix = "{queue-admission}:";
    private const string ReserveScript = """
        local account_limit = tonumber(ARGV[1])
        local account_new_limit = tonumber(ARGV[2])
        local global_hour_limit = tonumber(ARGV[3])
        local global_new_limit = tonumber(ARGV[4])
        local is_new = tonumber(ARGV[5])
        local hour_ttl = tonumber(ARGV[6])
        local day_ttl = tonumber(ARGV[7])

        local function at_limit(key, limit)
            if limit <= 0 then return false end
            return tonumber(redis.call('GET', key) or '0') >= limit
        end

        if at_limit(KEYS[1], account_limit) then return 1 end
        if is_new == 1 and at_limit(KEYS[2], account_new_limit) then return 2 end
        if at_limit(KEYS[3], global_hour_limit) then return 3 end
        if is_new == 1 and at_limit(KEYS[4], global_new_limit) then return 4 end

        local function increment(key, ttl)
            local value = redis.call('INCR', key)
            if value == 1 then redis.call('EXPIRE', key, ttl) end
        end

        increment(KEYS[1], day_ttl)
        if is_new == 1 then increment(KEYS[2], day_ttl) end
        increment(KEYS[3], hour_ttl)
        if is_new == 1 then increment(KEYS[4], day_ttl) end
        return 0
        """;

    public async Task<QueueAdmissionDecision> ReserveAsync(
        long bungieMembershipId,
        bool isNewReport,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hourStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var nextHour = hourStart.AddHours(1);
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var nextDay = dayStart.AddDays(1);
        var hourBucket = hourStart.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture);
        var dayBucket = dayStart.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var hourTtl = Math.Max(60, (long)Math.Ceiling((nextHour - now).TotalSeconds) + 60);
        var dayTtl = Math.Max(60, (long)Math.Ceiling((nextDay - now).TotalSeconds) + 60);

        var keys = new RedisKey[]
        {
            $"{KeyPrefix}account:{bungieMembershipId}:requests:{dayBucket}",
            $"{KeyPrefix}account:{bungieMembershipId}:new:{dayBucket}",
            $"{KeyPrefix}global:requests:{hourBucket}",
            $"{KeyPrefix}global:new:{dayBucket}"
        };
        var values = new RedisValue[]
        {
            options.Value.MaxRequestsPerAccountPerDay,
            options.Value.MaxNewReportsPerAccountPerDay,
            options.Value.MaxRequestsGloballyPerHour,
            options.Value.MaxNewReportsGloballyPerDay,
            isNewReport ? 1 : 0,
            hourTtl,
            dayTtl
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await redis.GetDatabase()
                .ScriptEvaluateAsync(ReserveScript, keys, values)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var code = (int)result;
            return code switch
            {
                0 => new QueueAdmissionDecision(),
                1 => new QueueAdmissionDecision(QueueAdmissionFailure.AccountDailyLimit, nextDay - now),
                2 => new QueueAdmissionDecision(QueueAdmissionFailure.AccountNewReportDailyLimit, nextDay - now),
                3 => new QueueAdmissionDecision(QueueAdmissionFailure.GlobalHourlyLimit, nextHour - now),
                4 => new QueueAdmissionDecision(QueueAdmissionFailure.GlobalNewReportDailyLimit, nextDay - now),
                _ => Unavailable()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Queue admission quota reservation failed.");
            return Unavailable();
        }
    }

    private static QueueAdmissionDecision Unavailable() =>
        new(QueueAdmissionFailure.AdmissionUnavailable);
}
