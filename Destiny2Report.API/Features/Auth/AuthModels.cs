using System.Text.Json.Serialization;

namespace Destiny2Report.API.Features.Auth;

public sealed record BungieOAuthCodeRequest(
    string Code,
    string? RedirectUri);

public sealed record BungieOAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_expires_in")] int? RefreshExpiresIn,
    [property: JsonPropertyName("membership_id")] string? MembershipId);

public sealed record SignedInPlayerResponse(
    bool SignedIn,
    BungieNetUserResponse? BungieNetUser,
    IReadOnlyCollection<DestinyMembershipResponse> DestinyMemberships,
    DestinyMembershipResponse? PrimaryDestinyMembership,
    bool IsAdmin = false);

public sealed record BungieNetUserResponse(
    long MembershipId,
    string? UniqueName,
    string? DisplayName,
    string? ProfilePicturePath,
    string? CachedBungieGlobalDisplayName,
    int? CachedBungieGlobalDisplayNameCode);

public sealed record DestinyMembershipResponse(
    int MembershipType,
    long MembershipId,
    string? DisplayName,
    string? BungieGlobalDisplayName,
    int? BungieGlobalDisplayNameCode,
    string? IconPath,
    int CrossSaveOverride,
    IReadOnlyCollection<int> ApplicableMembershipTypes,
    bool IsPublic);

internal sealed record BungieApiResponse<TResponse>(
    TResponse? Response,
    int ErrorCode,
    int ThrottleSeconds,
    string? ErrorStatus,
    string? Message,
    IDictionary<string, string>? MessageData,
    string? DetailedErrorTrace);
