using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Destiny2Report.API.Features.Crawler.Models;

namespace Destiny2Report.API.Features.PushNotifications;

public static class PushNotificationHandlers
{
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromDays(7);
    private const int MaxEndpointLength = 4096;
    private const int MaxKeyLength = 1024;

    public static Ok<PushNotificationConfigResponse> GetConfig(IOptions<WebPushOptions> options)
    {
        var value = options.Value;
        return TypedResults.Ok(new PushNotificationConfigResponse(
            value.Enabled,
            value.Enabled ? value.PublicKey : null));
    }

    public static async Task<Results<NoContent, BadRequest<ProblemDetails>, StatusCodeHttpResult>> Register(
        RegisterReportPushSubscriptionRequest request,
        IOptions<WebPushOptions> options,
        IMongoDatabase mongoDatabase,
        IReportPushNotificationService pushNotificationService,
        ILogger<ReportPushNotificationService> logger,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryValidate(request, out var problemDetails))
        {
            return TypedResults.BadRequest(problemDetails);
        }

        var endpointHash = ReportPushNotificationService.HashEndpoint(request.Endpoint);
        var now = DateTime.UtcNow;
        var subscriptions = mongoDatabase.GetCollection<ReportPushSubscription>(ReportPushNotificationService.CollectionName);
        var filter = Builders<ReportPushSubscription>.Filter.Eq(item => item.EndpointHash, endpointHash)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.MembershipTypeId, request.MembershipTypeId)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.MembershipId, request.MembershipId);
        var update = Builders<ReportPushSubscription>.Update
            .SetOnInsert(item => item.EndpointHash, endpointHash)
            .SetOnInsert(item => item.MembershipTypeId, request.MembershipTypeId)
            .SetOnInsert(item => item.MembershipId, request.MembershipId)
            .Set(item => item.Endpoint, request.Endpoint)
            .Set(item => item.P256dh, request.Keys.P256dh)
            .Set(item => item.Auth, request.Keys.Auth)
            .Set(item => item.State, ReportPushNotificationService.StateWaiting)
            .Set(item => item.CreatedAtUtc, now)
            .Set(item => item.ExpiresAtUtc, now.Add(SubscriptionLifetime))
            .Unset(item => item.DeliveryFailedAtUtc);

        await subscriptions.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true },
                cancellationToken)
            .ConfigureAwait(false);

        // Close the narrow race where the crawl completes while the browser is
        // granting permission and posting its subscription. Atomic delivery
        // claims in the sender prevent this check from producing duplicates.
        var completedReportFilter = Builders<DestinyReport>.Filter.Eq(
                item => item.PlatformId,
                request.MembershipTypeId)
            & Builders<DestinyReport>.Filter.Eq(item => item.PlayerMembershipId, request.MembershipId)
            & Builders<DestinyReport>.Filter.Eq(item => item.CrawlState, DestinyReport.CrawlStateCompleted);
        var reportIsAlreadyComplete = await mongoDatabase.GetCollection<DestinyReport>("destiny_reports")
            .Find(completedReportFilter)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (reportIsAlreadyComplete)
        {
            try
            {
                await pushNotificationService.NotifyReportCompletedAsync(
                        request.MembershipTypeId,
                        request.MembershipId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Stored a late push subscription for completed report {MembershipTypeId}/{MembershipId}, but immediate delivery failed.",
                    request.MembershipTypeId,
                    request.MembershipId);
            }
        }

        return TypedResults.NoContent();
    }

    public static async Task<Results<NoContent, BadRequest<ProblemDetails>>> Remove(
        RemoveReportPushSubscriptionRequest request,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        if (request.MembershipTypeId <= 0
            || request.MembershipId <= 0
            || !TryValidateEndpoint(request.Endpoint))
        {
            return TypedResults.BadRequest(InvalidSubscriptionProblem());
        }

        var endpointHash = ReportPushNotificationService.HashEndpoint(request.Endpoint);
        var subscriptions = mongoDatabase.GetCollection<ReportPushSubscription>(ReportPushNotificationService.CollectionName);
        var filter = Builders<ReportPushSubscription>.Filter.Eq(item => item.EndpointHash, endpointHash)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.MembershipTypeId, request.MembershipTypeId)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.MembershipId, request.MembershipId);

        await subscriptions.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    public static async Task<Results<Ok<ReportPushSubscriptionStatusResponse>, BadRequest<ProblemDetails>>> GetStatus(
        RemoveReportPushSubscriptionRequest request,
        IMongoDatabase mongoDatabase,
        CancellationToken cancellationToken)
    {
        if (request.MembershipTypeId <= 0
            || request.MembershipId <= 0
            || !TryValidateEndpoint(request.Endpoint))
        {
            return TypedResults.BadRequest(InvalidSubscriptionProblem());
        }

        var endpointHash = ReportPushNotificationService.HashEndpoint(request.Endpoint);
        var subscriptions = mongoDatabase.GetCollection<ReportPushSubscription>(ReportPushNotificationService.CollectionName);
        var filter = Builders<ReportPushSubscription>.Filter.Eq(item => item.EndpointHash, endpointHash)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.MembershipTypeId, request.MembershipTypeId)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.MembershipId, request.MembershipId)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.State, ReportPushNotificationService.StateWaiting);
        var registered = await subscriptions.Find(filter).AnyAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new ReportPushSubscriptionStatusResponse(registered));
    }

    private static bool TryValidate(
        RegisterReportPushSubscriptionRequest request,
        out ProblemDetails problemDetails)
    {
        var valid = request.MembershipTypeId > 0
            && request.MembershipId > 0
            && TryValidateEndpoint(request.Endpoint)
            && !string.IsNullOrWhiteSpace(request.Keys.P256dh)
            && request.Keys.P256dh.Length <= MaxKeyLength
            && !string.IsNullOrWhiteSpace(request.Keys.Auth)
            && request.Keys.Auth.Length <= MaxKeyLength;
        problemDetails = InvalidSubscriptionProblem();
        return valid;
    }

    private static bool TryValidateEndpoint(string endpoint)
    {
        return !string.IsNullOrWhiteSpace(endpoint)
            && endpoint.Length <= MaxEndpointLength
            && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;
    }

    private static ProblemDetails InvalidSubscriptionProblem() => new()
    {
        Title = "Invalid push subscription",
        Detail = "A valid report identity and HTTPS browser push subscription are required.",
        Status = StatusCodes.Status400BadRequest
    };
}
