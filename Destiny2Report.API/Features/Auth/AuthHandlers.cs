using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Destiny2Report.API.Features.Auth;

public static class AuthHandlers
{
    public static async Task<Results<Ok<BungieOAuthTokenResponse>, BadRequest<ProblemDetails>, StatusCodeHttpResult>> ExchangeBungieCode(
        BungieOAuthCodeRequest request,
        IBungieAuthService authService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await authService.ExchangeCodeAsync(request, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(response);
        }
        catch (BungieAuthException ex) when (ex.Error is "invalid_oauth_request" or "bungie_oauth_exchange_failed")
        {
            return TypedResults.BadRequest(ToProblemDetails(ex, StatusCodes.Status400BadRequest));
        }
        catch (BungieAuthException ex) when (ex.Error is "bungie_oauth_not_configured")
        {
            return TypedResults.BadRequest(ToProblemDetails(ex, StatusCodes.Status400BadRequest));
        }
    }

    public static async Task<Results<Ok<SignedInPlayerResponse>, StatusCodeHttpResult>> WhoAmI(
        HttpRequest request,
        IBungieAuthService authService,
        CancellationToken cancellationToken)
    {
        var accessToken = ReadBearerToken(request);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return TypedResults.Ok(new SignedInPlayerResponse(false, null, [], null));
        }

        try
        {
            var response = await authService.GetCurrentUserAsync(accessToken, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(response);
        }
        catch (BungieAuthException)
        {
            return TypedResults.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private static string? ReadBearerToken(HttpRequest request)
    {
        const string bearerPrefix = "Bearer ";
        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[bearerPrefix.Length..].Trim()
            : null;
    }

    private static ProblemDetails ToProblemDetails(BungieAuthException exception, int statusCode)
    {
        return new ProblemDetails
        {
            Title = exception.Error,
            Detail = exception.Message,
            Status = statusCode
        };
    }
}
