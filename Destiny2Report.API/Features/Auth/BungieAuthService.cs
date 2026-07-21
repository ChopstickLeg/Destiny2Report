using D2Report.BungieClient;
using Destiny2Report.API.Bungie;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Destiny2Report.API.Features.Auth;

public interface IBungieAuthService
{
    Task<BungieOAuthTokenResponse> ExchangeCodeAsync(
        BungieOAuthCodeRequest request,
        CancellationToken cancellationToken);

    Task<BungieOAuthTokenResponse> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken);

    Task<SignedInPlayerResponse> GetCurrentUserAsync(
        string accessToken,
        CancellationToken cancellationToken);
}

public sealed class BungieAuthService(
    HttpClient httpClient,
    IOptions<BungieClientOptions> options) : IBungieAuthService
{
    private const string TokenEndpoint = "App/OAuth/Token/";
    private const string CurrentUserEndpoint = "User/GetMembershipsForCurrentUser/";
    private const int BungieSuccessErrorCode = 1;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<BungieOAuthTokenResponse> ExchangeCodeAsync(
        BungieOAuthCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new BungieAuthException("invalid_oauth_request", "The OAuth code is required.");
        }

        var bungieOptions = options.Value;
        if (string.IsNullOrWhiteSpace(bungieOptions.ClientId)
            || string.IsNullOrWhiteSpace(bungieOptions.ClientSecret))
        {
            throw new BungieAuthException(
                "bungie_oauth_not_configured",
                "Bungie:ClientId and Bungie:ClientSecret must be configured.");
        }

        return await RequestTokensAsync(
            BuildTokenRequestForm(request, bungieOptions.ClientId),
            bungieOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BungieOAuthTokenResponse> RefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new BungieAuthException("invalid_oauth_request", "The OAuth refresh token is required.");
        }

        var bungieOptions = options.Value;
        if (string.IsNullOrWhiteSpace(bungieOptions.ClientId)
            || string.IsNullOrWhiteSpace(bungieOptions.ClientSecret))
        {
            throw new BungieAuthException(
                "bungie_oauth_not_configured",
                "Bungie:ClientId and Bungie:ClientSecret must be configured.");
        }

        return await RequestTokensAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = bungieOptions.ClientId
            },
            bungieOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<BungieOAuthTokenResponse> RequestTokensAsync(
        Dictionary<string, string> form,
        BungieClientOptions bungieOptions,
        CancellationToken cancellationToken)
    {
        using var formContent = new FormUrlEncodedContent(form);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = formContent
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{bungieOptions.ClientId}:{bungieOptions.ClientSecret}")));

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new BungieAuthException(
                "bungie_oauth_exchange_failed",
                "Bungie rejected the OAuth code exchange.",
                response.StatusCode,
                responseBody);
        }

        var tokenResponse = JsonSerializer.Deserialize<BungieOAuthTokenResponse>(responseBody, JsonSerializerOptions);
        return tokenResponse ?? throw new BungieAuthException(
            "bungie_oauth_exchange_failed",
            "Bungie returned an empty OAuth token response.");
    }

    public async Task<SignedInPlayerResponse> GetCurrentUserAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return SignedOut();
        }

        var bungieOptions = options.Value;
        if (string.IsNullOrWhiteSpace(bungieOptions.ApiKey))
        {
            throw new BungieAuthException(
                "bungie_api_key_not_configured",
                "Bungie:ApiKey must be configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, CurrentUserEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-API-Key", bungieOptions.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return SignedOut();
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new BungieAuthException(
                "bungie_whoami_failed",
                "Bungie rejected the current user lookup.",
                response.StatusCode,
                responseBody);
        }

        var bungieResponse = JsonSerializer.Deserialize<BungieApiResponse<UserMembershipData>>(responseBody, JsonSerializerOptions);
        if (bungieResponse is null)
        {
            throw new BungieAuthException("bungie_whoami_failed", "Bungie returned an empty current user response.");
        }

        if (bungieResponse.ErrorCode != BungieSuccessErrorCode)
        {
            return SignedOut();
        }

        return ToSignedInPlayerResponse(bungieResponse.Response);
    }

    private static Dictionary<string, string> BuildTokenRequestForm(
        BungieOAuthCodeRequest request,
        string clientId)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = request.Code,
            ["client_id"] = clientId
        };

        if (!string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            form["redirect_uri"] = request.RedirectUri;
        }

        return form;
    }

    private static SignedInPlayerResponse ToSignedInPlayerResponse(UserMembershipData? data)
    {
        if (data is null)
        {
            return SignedOut();
        }

        var memberships = data.DestinyMemberships?
            .Select(membership => new DestinyMembershipResponse(
                MembershipType: membership.MembershipType,
                MembershipId: membership.MembershipId,
                DisplayName: membership.DisplayName,
                BungieGlobalDisplayName: membership.BungieGlobalDisplayName,
                BungieGlobalDisplayNameCode: membership.BungieGlobalDisplayNameCode,
                IconPath: membership.IconPath,
                CrossSaveOverride: membership.CrossSaveOverride,
                ApplicableMembershipTypes: [.. membership.ApplicableMembershipTypes ?? []],
                IsPublic: membership.IsPublic))
            .ToArray() ?? [];

        var primaryMembership = memberships.FirstOrDefault(membership => membership.MembershipId == data.PrimaryMembershipId)
            ?? memberships.FirstOrDefault();

        var bungieUser = data.BungieNetUser is null
            ? null
            : new BungieNetUserResponse(
                MembershipId: data.BungieNetUser.MembershipId,
                UniqueName: data.BungieNetUser.UniqueName,
                DisplayName: data.BungieNetUser.DisplayName,
                ProfilePicturePath: data.BungieNetUser.ProfilePicturePath,
                CachedBungieGlobalDisplayName: data.BungieNetUser.CachedBungieGlobalDisplayName,
                CachedBungieGlobalDisplayNameCode: data.BungieNetUser.CachedBungieGlobalDisplayNameCode);

        return new SignedInPlayerResponse(
            SignedIn: true,
            BungieNetUser: bungieUser,
            DestinyMemberships: memberships,
            PrimaryDestinyMembership: primaryMembership);
    }

    private static SignedInPlayerResponse SignedOut()
    {
        return new SignedInPlayerResponse(
            SignedIn: false,
            BungieNetUser: null,
            DestinyMemberships: [],
            PrimaryDestinyMembership: null);
    }
}

public sealed class BungieAuthException(
    string error,
    string message,
    System.Net.HttpStatusCode? bungieStatusCode = null,
    string? bungieResponseBody = null) : Exception(message)
{
    public string Error { get; } = error;

    public System.Net.HttpStatusCode? BungieStatusCode { get; } = bungieStatusCode;

    public string? BungieResponseBody { get; } = bungieResponseBody;
}
