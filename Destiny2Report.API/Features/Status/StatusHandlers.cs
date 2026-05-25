namespace Destiny2Report.API.Features.Status;

public static class StatusHandlers
{
    public static IResult GetStatus(IHostEnvironment environment)
    {
        var response = new StatusResponse(
            Status: "ok",
            Environment: environment.EnvironmentName,
            ServerTimeUtc: DateTimeOffset.UtcNow);

        return TypedResults.Ok(response);
    }
}
