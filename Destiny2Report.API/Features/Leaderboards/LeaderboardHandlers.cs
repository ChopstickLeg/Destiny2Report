using Microsoft.AspNetCore.Mvc;

namespace Destiny2Report.API.Features.Leaderboards;

public static class LeaderboardHandlers
{
    public static async Task<IResult> GetCatalog(ILeaderboardService service, CancellationToken cancellationToken)
        => TypedResults.Ok(await service.GetCatalogAsync(cancellationToken).ConfigureAwait(false));

    public static async Task<IResult> GetBoard(
        string metricKey,
        int offset,
        int limit,
        ILeaderboardService service,
        CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset);
        limit = limit <= 0 ? 50 : Math.Min(limit, 50);
        if (offset >= LeaderboardBoard.MaximumEntries)
        {
            return TypedResults.BadRequest(new ProblemDetails { Title = "Invalid leaderboard page", Detail = "offset must be less than 1000.", Status = 400 });
        }

        var catalog = await service.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (!catalog.IsReady)
        {
            var problem = new ProblemDetails
            {
                Title = "Leaderboards are warming up",
                Detail = $"{catalog.CompletedPlayerCount} of {catalog.MinimumCompletedPlayers} required players have completed crawls.",
                Status = StatusCodes.Status409Conflict
            };
            problem.Extensions["completedPlayerCount"] = catalog.CompletedPlayerCount;
            problem.Extensions["minimumCompletedPlayers"] = catalog.MinimumCompletedPlayers;
            return TypedResults.Conflict(problem);
        }

        var board = await service.GetBoardAsync(metricKey, cancellationToken).ConfigureAwait(false);
        if (board is null) return TypedResults.NotFound();

        var ranked = LeaderboardRanking.Rank(board.Entries);
        var page = ranked.Skip(offset).Take(limit).ToArray();
        return TypedResults.Ok(new LeaderboardPageResponse(
            board.MetricKey, board.Category, board.Title, board.Description, board.Unit,
            offset, limit, board.Entries.Count, board.UpdatedAtUtc, board.IsRepairing, page));
    }

}
