using D2Report.BungieClient;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Destiny2Report.API.Features.PlayerSearch;

public static class PlayerSearchHandlers
{
    private const int CharactersComponent = 200;
    private const int SearchPage = 0;
    private const string BungieNetBaseUrl = "https://www.bungie.net";

    public static async Task<Results<Ok<PlayerSearchResponse>, NotFound, BadRequest<ProblemDetails>, ProblemHttpResult>> SearchPlayer(
        [FromBody] PlayerSearchRequest request,
        ID2ReportClient bungieClient,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayNamePrefix))
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Invalid display name prefix",
                Detail = "displayNamePrefix is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var searchResponse = await bungieClient
            .User_SearchByGlobalNamePostAsync(
                SearchPage,
                new UserSearchPrefixRequest { DisplayNamePrefix = request.DisplayNamePrefix.Trim() },
                cancellationToken)
            .ConfigureAwait(false);

        if (searchResponse.ErrorCode != 1)
        {
            return BungieProblem("SearchByGlobalNamePost", searchResponse);
        }

        var searchResult = searchResponse.Response.SearchResults?
            .Select(result => new
            {
                Result = result,
                Membership = SelectMembership(result.DestinyMemberships)
            })
            .FirstOrDefault(result => result.Membership is not null);

        if (searchResult?.Membership is null)
        {
            return TypedResults.NotFound();
        }

        var profileResponse = await bungieClient
            .Destiny2_GetProfileAsync(
                [CharactersComponent],
                searchResult.Membership.MembershipId,
                searchResult.Membership.MembershipType,
                cancellationToken)
            .ConfigureAwait(false);

        if (profileResponse.ErrorCode != 1)
        {
            return BungieProblem("GetProfile", profileResponse);
        }

        var lastPlayedCharacter = profileResponse.Response.Characters?.Data?.Values
            .OrderByDescending(character => character.DateLastPlayed)
            .FirstOrDefault();
        var displayName = !string.IsNullOrWhiteSpace(searchResult.Result.BungieGlobalDisplayName)
            ? searchResult.Result.BungieGlobalDisplayName
            : searchResult.Membership.DisplayName;
        var response = new PlayerSearchResponse(
            DisplayName: displayName,
            DisplayCode: searchResult.Result.BungieGlobalDisplayNameCode,
            MembershipId: searchResult.Membership.MembershipId,
            MembershipTypeId: searchResult.Membership.MembershipType,
            EmblemIconUrl: ToBungieUrl(lastPlayedCharacter?.EmblemPath));

        return TypedResults.Ok(response);
    }

    private static UserInfoCard? SelectMembership(IEnumerable<UserInfoCard>? memberships)
    {
        if (memberships is null)
        {
            return null;
        }

        var publicMemberships = memberships
            .Where(membership => membership.IsPublic && membership.MembershipId > 0 && membership.MembershipType > 0)
            .ToArray();

        return publicMemberships.FirstOrDefault(membership => membership.CrossSaveOverride == membership.MembershipType)
            ?? publicMemberships.FirstOrDefault(membership => membership.CrossSaveOverride <= 0)
            ?? publicMemberships.FirstOrDefault();
    }

    private static ProblemHttpResult BungieProblem(string operation, BungieResponse response)
    {
        return TypedResults.Problem(new ProblemDetails
        {
            Title = $"{operation} failed",
            Detail = response.Message,
            Status = StatusCodes.Status502BadGateway,
            Extensions =
            {
                ["bungieErrorCode"] = response.ErrorCode,
                ["bungieErrorStatus"] = response.ErrorStatus
            }
        });
    }

    private static string ToBungieUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return Uri.TryCreate(path, UriKind.Absolute, out _)
            ? path
            : $"{BungieNetBaseUrl}{path}";
    }
}
