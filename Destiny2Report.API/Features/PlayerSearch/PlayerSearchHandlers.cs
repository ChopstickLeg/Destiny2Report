using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MongoDB.Driver.Search;

namespace Destiny2Report.API.Features.PlayerSearch;

public static class PlayerSearchHandlers
{
    private const int CharactersComponent = 200;
    private const int SearchPage = 0;
    private const string FullDisplayNameSearchIndex = "player-full-display-name";
    private const string BungieNetBaseUrl = "https://www.bungie.net";

    public static async Task<Results<Ok<IReadOnlyList<PlayerSearchResponse>>, NotFound, BadRequest<ProblemDetails>, ProblemHttpResult>> SearchPlayer(
        [FromBody] PlayerSearchRequest request,
        ID2ReportClient bungieClient,
        IMongoDatabase mongoDatabase,
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
        var searchResponse = await bungieClient
            .User_SearchByGlobalNamePostAsync(
                SearchPage,
                new UserSearchPrefixRequest { DisplayNamePrefix = displayNamePrefix },
                cancellationToken)
            .ConfigureAwait(false);

        if (searchResponse.ErrorCode != 1)
        {
            return BungieProblem("SearchByGlobalNamePost", searchResponse);
        }

        var searchResults = searchResponse.Response.SearchResults?
            .Select(result => new
            {
                Result = result,
                Membership = SelectMembership(result.DestinyMemberships)
            })
            .Where(result => result.Membership is not null)
            .ToArray()
            ?? [];

        var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
        var fullDisplayNameSearch = Builders<DestinyReport>.Search.Text(
            report => report.FullDisplayName,
            displayNamePrefix,
            new SearchFuzzyOptions { MaxEdits = 2, PrefixLength = 1 });
        var reportSearchTask = reports
            .Aggregate()
            .Search(fullDisplayNameSearch, new SearchOptions<DestinyReport> { IndexName = FullDisplayNameSearchIndex })
            .ToListAsync(cancellationToken);
        var bungieResultTasks = searchResults
            .Select(result => CreateBungieResponseAsync(result.Result, result.Membership!, bungieClient, cancellationToken))
            .ToArray();

        var bungieResults = await Task.WhenAll(bungieResultTasks).ConfigureAwait(false);
        var reportResults = await reportSearchTask.ConfigureAwait(false);

        if (bungieResults.Length == 0 && reportResults.Count == 0)
        {
            return TypedResults.NotFound();
        }

        var resultsByMembership = bungieResults
            .GroupBy(result => (result.MembershipId, result.MembershipTypeId))
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var report in reportResults)
        {
            var key = (report.PlayerMembershipId, report.PlatformId);
            var reportEmblemUrl = report.MostUsedEmblems.FirstOrDefault()?.IconUrl ?? "";
            var reportResponse = new PlayerSearchResponse(
                report.DisplayName,
                report.DisplayCode,
                report.PlayerMembershipId,
                report.PlatformId,
                reportEmblemUrl);

            if (resultsByMembership.TryGetValue(key, out var bungieResult) && string.IsNullOrWhiteSpace(reportEmblemUrl))
            {
                reportResponse = reportResponse with { EmblemIconUrl = bungieResult.EmblemIconUrl };
            }

            resultsByMembership[key] = reportResponse;
        }

        return TypedResults.Ok<IReadOnlyList<PlayerSearchResponse>>(resultsByMembership.Values.ToArray());
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
