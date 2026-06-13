using Newtonsoft.Json.Linq;

namespace D2Report.BungieClient.RateLimiting;

public sealed class BungieClientRetryHandler : DelegatingHandler
{
    private const int DestinyThrottledByGameServerErrorCode = 1672;
    private const int MaxRetryAttempts = 5;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestContent = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            var retryRequest = CloneRequest(request, requestContent);
            var response = await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
            var errorCode = await TryReadBungieErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);

            if (errorCode != DestinyThrottledByGameServerErrorCode || attempt >= MaxRetryAttempts)
            {
                return response;
            }

            response.Dispose();
            retryRequest.Dispose();
            await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, byte[]? content)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (content is not null)
        {
            clone.Content = new ByteArrayContent(content);
            foreach (var header in request.Content!.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private static async Task<int?> TryReadBungieErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return null;
        }

        var content = response.Content;
        var headers = content.Headers.ToArray();
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var replacementContent = new ByteArrayContent(bytes);
        foreach (var header in headers)
        {
            replacementContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = replacementContent;

        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            var json = JObject.Parse(await replacementContent.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return json["ErrorCode"]?.Value<int>();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        var delayMilliseconds = InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(delayMilliseconds, MaxRetryDelay.TotalMilliseconds));
    }
}
