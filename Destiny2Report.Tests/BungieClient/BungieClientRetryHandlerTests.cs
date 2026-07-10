using D2Report.BungieClient.RateLimiting;

namespace Destiny2Report.Tests.BungieClient;

public sealed class BungieClientRetryHandlerTests
{
    [Fact]
    public async Task GetProfile404RetriesTwice()
    {
        var innerHandler = new SequenceHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        using var invoker = CreateInvoker(innerHandler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://www.bungie.net/Platform/Destiny2/3/Profile/123/?components=100"),
            CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(3, innerHandler.RequestCount);
    }

    [Fact]
    public async Task NonGetProfile404DoesNotRetry()
    {
        var innerHandler = new SequenceHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        using var invoker = CreateInvoker(innerHandler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://www.bungie.net/Platform/Destiny2/3/Profile/123/Character/456/?components=100"),
            CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);
    }

    [Fact]
    public async Task GetProfile404ReturnsSuccessfulRetryResponse()
    {
        var innerHandler = new SequenceHandler(attempt => new HttpResponseMessage(
            attempt == 1
                ? System.Net.HttpStatusCode.NotFound
                : System.Net.HttpStatusCode.OK));
        using var invoker = CreateInvoker(innerHandler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "Destiny2/3/Profile/123/?components=100"),
            CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);
    }


    [Theory]
    [InlineData(503)]
    [InlineData(524)]
    public async Task RetryableHttpStatusReturnsSuccessfulRetryResponse(int statusCode)
    {
        var innerHandler = new SequenceHandler(attempt => new HttpResponseMessage(
            attempt == 1
                ? (System.Net.HttpStatusCode)statusCode
                : System.Net.HttpStatusCode.OK));
        using var invoker = CreateInvoker(innerHandler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://www.bungie.net/Platform/Destiny2/Stats/PostGameCarnageReport/123/"),
            CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task RequestTimeoutRetries()
    {
        var innerHandler = new AsyncSequenceHandler(attempt =>
        {
            if (attempt == 1)
            {
                throw new TaskCanceledException("The request timed out.");
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        });
        using var invoker = CreateInvoker(innerHandler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://www.bungie.net/Platform/Destiny2/Stats/PostGameCarnageReport/123/"),
            CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);
    }

    private static HttpMessageInvoker CreateInvoker(HttpMessageHandler innerHandler)
    {
        return new HttpMessageInvoker(new BungieClientRetryHandler
        {
            InnerHandler = innerHandler
        });
    }

    private sealed class SequenceHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(RequestCount));
        }
    }

    private sealed class AsyncSequenceHandler(Func<int, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return responseFactory(RequestCount);
        }
    }
}
