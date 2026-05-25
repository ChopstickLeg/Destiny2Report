namespace Destiny2Report.API.Features.Status;

public sealed record StatusResponse(
    string Status,
    string Environment,
    DateTimeOffset ServerTimeUtc);
