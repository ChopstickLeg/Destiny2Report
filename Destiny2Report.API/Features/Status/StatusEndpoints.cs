namespace Destiny2Report.API.Features.Status;

public static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatusEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/status", StatusHandlers.GetStatus)
            .WithName("GetStatus")
            .WithSummary("Returns basic service health information.");

        return api;
    }
}
