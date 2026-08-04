using Destiny2Report.API.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Destiny2Report.Tests.RateLimiting;

public sealed class EnsureRetryAfterHeaderMiddlewareTests
{
    [Fact]
    public async Task AddsDefaultRetryAfterToAny429Response()
    {
        var context = new DefaultHttpContext();
        var middleware = new EnsureRetryAfterHeaderMiddleware(httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal("60", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task PreservesEndpointSpecificRetryAfter()
    {
        var context = new DefaultHttpContext();
        var middleware = new EnsureRetryAfterHeaderMiddleware(httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers.RetryAfter = "19800";
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal("19800", context.Response.Headers.RetryAfter);
    }
}
