namespace Destiny2Report.API.Features.PlayerSearch;

public sealed record PlayerSearchResponse(
    string DisplayName,
    int? DisplayCode,
    long MembershipId,
    int MembershipTypeId,
    string EmblemIconUrl,
    string QueueTicket = "");
