using System.Globalization;

namespace Destiny2Report.API.RateLimiting;

public sealed class EnsureRetryAfterHeaderMiddleware(RequestDelegate next)
{
    public const int DefaultRetryAfterSeconds = 60;

    public async Task InvokeAsync(HttpContext httpContext)
    {
        httpContext.Response.OnStarting(static state =>
        {
            var context = (HttpContext)state;
            EnsureHeader(context.Response);

            return Task.CompletedTask;
        }, httpContext);

        await next(httpContext);

        if (!httpContext.Response.HasStarted)
        {
            EnsureHeader(httpContext.Response);
        }
    }

    private static void EnsureHeader(HttpResponse response)
    {
        if (response.StatusCode == StatusCodes.Status429TooManyRequests
            && !response.Headers.ContainsKey("Retry-After"))
        {
            response.Headers.RetryAfter =
                DefaultRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }
    }
}
