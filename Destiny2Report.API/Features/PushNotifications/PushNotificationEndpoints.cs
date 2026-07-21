using Destiny2Report.API.RateLimiting;

namespace Destiny2Report.API.Features.PushNotifications;

public static class PushNotificationEndpoints
{
    public static RouteGroupBuilder MapPushNotificationEndpoints(this RouteGroupBuilder api)
    {
        var push = api.MapGroup("/push-notifications")
            .WithTags("Push Notifications");

        push.MapGet("/config", PushNotificationHandlers.GetConfig)
            .WithName("GetPushNotificationConfig")
            .WithSummary("Returns browser Web Push availability and the public VAPID key.");

        push.MapPost("/subscriptions", PushNotificationHandlers.Register)
            .WithName("RegisterReportPushSubscription")
            .WithSummary("Registers this browser for a one-time report completion notification.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        push.MapPost("/subscriptions/status", PushNotificationHandlers.GetStatus)
            .WithName("GetReportPushSubscriptionStatus")
            .WithSummary("Checks whether this browser is waiting for a report completion notification.");

        push.MapPost("/subscriptions/remove", PushNotificationHandlers.Remove)
            .WithName("RemoveReportPushSubscription")
            .WithSummary("Stops this browser from receiving a report completion notification.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        return api;
    }
}
