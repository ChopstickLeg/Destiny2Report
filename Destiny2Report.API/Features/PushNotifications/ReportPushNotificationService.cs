using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebPush;

namespace Destiny2Report.API.Features.PushNotifications;

public interface IReportPushNotificationService
{
    Task NotifyReportCompletedAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken);
}

public sealed class ReportPushNotificationService : IReportPushNotificationService, IDisposable
{
    internal const string CollectionName = "report_push_subscriptions";
    internal const string StateWaiting = "waiting";
    internal const string StateDelivering = "delivering";
    internal const string StateFailed = "failed";

    private readonly IMongoCollection<ReportPushSubscription> _subscriptions;
    private readonly WebPushOptions _options;
    private readonly ILogger<ReportPushNotificationService> _logger;
    private readonly WebPushClient _client = new();

    public ReportPushNotificationService(
        IMongoDatabase mongoDatabase,
        IOptions<WebPushOptions> options,
        ILogger<ReportPushNotificationService> logger)
    {
        _subscriptions = mongoDatabase.GetCollection<ReportPushSubscription>(CollectionName);
        _options = options.Value;
        _logger = logger;
    }

    public async Task NotifyReportCompletedAsync(
        int membershipTypeId,
        long membershipId,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var filter = Builders<ReportPushSubscription>.Filter.Eq(item => item.MembershipTypeId, membershipTypeId)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.MembershipId, membershipId)
            & Builders<ReportPushSubscription>.Filter.Eq(item => item.State, StateWaiting);
        var subscriptions = await _subscriptions.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (subscriptions.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = "Your Destiny report is ready",
            body = "The crawl is complete. Open the report to see the results.",
            url = $"/report/{membershipTypeId}/{membershipId}",
            tag = $"report-{membershipTypeId}-{membershipId}"
        });
        var vapidDetails = new VapidDetails(_options.Subject, _options.PublicKey, _options.PrivateKey);

        foreach (var storedSubscription in subscriptions)
        {
            var claimFilter = Builders<ReportPushSubscription>.Filter.Eq(item => item.Id, storedSubscription.Id)
                & Builders<ReportPushSubscription>.Filter.Eq(item => item.State, StateWaiting);
            var claimUpdate = Builders<ReportPushSubscription>.Update.Set(item => item.State, StateDelivering);
            var claimedSubscription = await _subscriptions.FindOneAndUpdateAsync(
                    claimFilter,
                    claimUpdate,
                    new FindOneAndUpdateOptions<ReportPushSubscription>
                    {
                        ReturnDocument = ReturnDocument.After
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (claimedSubscription is null)
            {
                continue;
            }

            try
            {
                var subscription = new PushSubscription(
                    claimedSubscription.Endpoint,
                    claimedSubscription.P256dh,
                    claimedSubscription.Auth);
                await _client.SendNotificationAsync(subscription, payload, vapidDetails, cancellationToken)
                    .ConfigureAwait(false);

                await _subscriptions.DeleteOneAsync(
                        item => item.Id == claimedSubscription.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                await _subscriptions.DeleteOneAsync(
                        item => item.Id == claimedSubscription.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Removed expired Web Push endpoint for report {MembershipTypeId}/{MembershipId}.",
                    membershipTypeId,
                    membershipId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var update = Builders<ReportPushSubscription>.Update
                    .Set(item => item.State, StateFailed)
                    .Set(item => item.DeliveryFailedAtUtc, DateTime.UtcNow);
                await _subscriptions.UpdateOneAsync(
                        item => item.Id == claimedSubscription.Id,
                        update,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogWarning(
                    ex,
                    "Could not deliver Web Push notification for report {MembershipTypeId}/{MembershipId}.",
                    membershipTypeId,
                    membershipId);
            }
        }
    }

    internal static string HashEndpoint(string endpoint)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
