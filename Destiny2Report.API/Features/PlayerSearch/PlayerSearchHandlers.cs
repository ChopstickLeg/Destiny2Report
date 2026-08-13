using D2Report.BungieClient;
using Destiny2Report.API.Features.Reports;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Destiny2Report.API.Features.PlayerSearch;

public static class PlayerSearchHandlers
{
    private const int CharactersComponent = 200;
    private const int MaxSearchResults = 25;
    private const int FirstSearchPage = 0;
    private const string BungieNetBaseUrl = "https://www.bungie.net";

    public static async Task<Results<Ok<IReadOnlyList<PlayerSearchResponse>>, NotFound, BadRequest<ProblemDetails>, ProblemHttpResult>> SearchPlayer(
        [FromBody] PlayerSearchRequest request,
        ID2ReportClient bungieClient,
        IQueueTicketService queueTickets,
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

        var displayNamePrefix = request.DisplayNamePrefix.Trim();
        if (request.DisplayCode is < 0 or > 9999)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Invalid display code",
                Detail = "displayCode must be between 0 and 9999.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var searchPage = FirstSearchPage;
        UserSearchResponse? searchResponseBody;
        var matchingUsers = new List<UserSearchResponseDetail>();

        do
        {
            var searchResponse = await bungieClient
                .User_SearchByGlobalNamePostAsync(
                    searchPage,
                    new UserSearchPrefixRequest { DisplayNamePrefix = displayNamePrefix },
                    cancellationToken)
                .ConfigureAwait(false);

            if (searchResponse.ErrorCode != 1)
            {
                return BungieProblem("SearchByGlobalNamePost", searchResponse);
            }

            searchResponseBody = searchResponse.Response;
            matchingUsers.AddRange(searchResponseBody.SearchResults?
                .Where(result => request.DisplayCode is null
                    || result.BungieGlobalDisplayNameCode == request.DisplayCode)
                ?? []);

            searchPage++;
        }
        while (request.DisplayCode is not null
            && matchingUsers.Count == 0
            && searchResponseBody.HasMore);

        var searchResults = matchingUsers
            .SelectMany(result => SelectMemberships(result.DestinyMemberships)
                .Select(membership => new
                {
                    Result = result,
                    Membership = membership
                }))
            .ToArray();

        var bungieResultTasks = searchResults
            .Select(result => CreateBungieResponseAsync(result.Result, result.Membership, bungieClient, cancellationToken))
            .ToArray();

        var bungieResults = await Task.WhenAll(bungieResultTasks).ConfigureAwait(false);

        if (bungieResults.Length == 0)
        {
            return TypedResults.NotFound();
        }

        var resultsWithoutTickets = bungieResults
            .GroupBy(result => (result.MembershipId, result.MembershipTypeId))
            .Select(group => group.First())
            .Take(MaxSearchResults)
            .ToArray();
        var results = await Task.WhenAll(resultsWithoutTickets.Select(async result => result with
        {
            QueueTicket = await queueTickets.IssueAsync(
                    result.MembershipTypeId,
                    result.MembershipId,
                    cancellationToken)
                .ConfigureAwait(false)
        })).ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<PlayerSearchResponse>>(
            results);
    }

    private static async Task<PlayerSearchResponse> CreateBungieResponseAsync(
        UserSearchResponseDetail result,
        UserInfoCard membership,
        ID2ReportClient bungieClient,
        CancellationToken cancellationToken)
    {
        var emblemIconUrl = await GetEmblemIconUrlAsync(membership, bungieClient, cancellationToken).ConfigureAwait(false);
        var displayName = !string.IsNullOrWhiteSpace(result.BungieGlobalDisplayName)
            ? result.BungieGlobalDisplayName
            : membership.DisplayName;

        return new PlayerSearchResponse(
            displayName,
            result.BungieGlobalDisplayNameCode,
            membership.MembershipId,
            membership.MembershipType,
            emblemIconUrl);
    }

    private static async Task<string> GetEmblemIconUrlAsync(
        UserInfoCard membership,
        ID2ReportClient bungieClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var profileResponse = await bungieClient
                .Destiny2_GetProfileAsync(
                    [CharactersComponent],
                    membership.MembershipId,
                    membership.MembershipType,
                    cancellationToken)
                .ConfigureAwait(false);

            if (profileResponse.ErrorCode != 1)
            {
                return "";
            }

            var lastPlayedCharacter = profileResponse.Response.Characters?.Data?.Values
                .OrderByDescending(character => character.DateLastPlayed)
                .FirstOrDefault();
            return ToBungieUrl(lastPlayedCharacter?.EmblemPath);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return "";
        }
    }

    private static IReadOnlyList<UserInfoCard> SelectMemberships(IEnumerable<UserInfoCard>? memberships)
    {
        if (memberships is null)
        {
            return [];
        }

        var publicMemberships = memberships
            .Where(membership => membership.IsPublic && membership.MembershipId > 0 && membership.MembershipType > 0)
            .ToArray();

        var crossSavePrimary = publicMemberships
            .FirstOrDefault(membership => membership.CrossSaveOverride == membership.MembershipType);

        return crossSavePrimary is null ? publicMemberships : [crossSavePrimary];
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
