using Newtonsoft.Json.Linq;

namespace D2Report.BungieClient.RateLimiting;

public sealed class BungieClientRetryHandler : DelegatingHandler
{
    private const int DestinyThrottledByGameServerErrorCode = 1672;
    private const int BungieTimeoutErrorCode = 1688;
    private const int MaxRetryAttempts = 5;
    private const int MaxGetProfileNotFoundRetryAttempts = 2;
    private const long MaxInspectableErrorPayloadBytes = 64 * 1024;

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
            HttpResponseMessage? response = null;

            try
            {
                response = await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                var errorCode = await TryReadBungieErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);

                if (!ShouldRetry(request, response, errorCode, attempt))
                {
                    return response;
                }
            }
            catch (Exception ex) when (ShouldRetryException(ex, cancellationToken, attempt))
            {
            }

            response?.Dispose();
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
        if (content.Headers.ContentLength is > MaxInspectableErrorPayloadBytes)
        {
            return null;
        }

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

    private static bool IsRetryableBungieErrorCode(int? errorCode)
    {
        return errorCode is DestinyThrottledByGameServerErrorCode
            or BungieTimeoutErrorCode;
    }

    private static bool IsRetryableHttpStatusCode(System.Net.HttpStatusCode statusCode)
    {
        return statusCode is System.Net.HttpStatusCode.ServiceUnavailable
            or (System.Net.HttpStatusCode)524;
    }

    private static bool ShouldRetryException(Exception exception, CancellationToken cancellationToken, int attempt)
    {
        return !cancellationToken.IsCancellationRequested
            && attempt < MaxRetryAttempts
            && exception is HttpRequestException or TaskCanceledException or TimeoutException;
    }

    private static bool ShouldRetry(
        HttpRequestMessage request,
        HttpResponseMessage response,
        int? errorCode,
        int attempt)
    {
        if (IsRetryableBungieErrorCode(errorCode) || IsRetryableHttpStatusCode(response.StatusCode))
        {
            return attempt < MaxRetryAttempts;
        }

        return response.StatusCode == System.Net.HttpStatusCode.NotFound
            && attempt < MaxGetProfileNotFoundRetryAttempts
            && IsGetProfileRequest(request.RequestUri);
    }

    private static bool IsGetProfileRequest(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return false;
        }

        var path = requestUri.IsAbsoluteUri
            ? requestUri.AbsolutePath
            : requestUri.OriginalString.Split('?', 2)[0];
        var segments = path
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index <= segments.Length - 4; index++)
        {
            if (segments[index].Equals("Destiny2", StringComparison.OrdinalIgnoreCase)
                && segments[index + 2].Equals("Profile", StringComparison.OrdinalIgnoreCase))
            {
                return index + 4 == segments.Length;
            }
        }

        return false;
    }
}
