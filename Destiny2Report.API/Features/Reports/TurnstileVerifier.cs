using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Destiny2Report.API.Features.Reports;

public sealed class TurnstileOptions
{
    public const string SectionName = "Turnstile";
    public const string QueueAction = "queue_report";

    public string SecretKey { get; init; } = "";

    public string[] AllowedHostnames { get; init; } = [];
}

public interface ITurnstileVerifier
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken);
}

public sealed class TurnstileVerifier(
    HttpClient httpClient,
    IOptions<TurnstileOptions> options,
    ILogger<TurnstileVerifier> logger) : ITurnstileVerifier
{
    private const string SiteverifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly TurnstileOptions options = options.Value;

    public async Task<bool> VerifyAsync(
        string? token,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
        {
            return false;
        }

        var form = new Dictionary<string, string>
        {
            ["secret"] = options.SecretKey,
            ["response"] = token,
            ["idempotency_key"] = Guid.NewGuid().ToString()
        };
        if (IPAddress.TryParse(remoteIp, out var parsedRemoteIp))
        {
            form["remoteip"] = parsedRemoteIp.ToString();
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var content = new FormUrlEncodedContent(form);
                using var response = await httpClient
                    .PostAsync(SiteverifyUrl, content, cancellationToken)
                    .ConfigureAwait(false);
                if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                {
                    logger.LogWarning(
                        "Turnstile Siteverify returned transient HTTP {StatusCode}; retrying attempt {NextAttempt} of {MaxAttempts}.",
                        (int)response.StatusCode,
                        attempt + 1,
                        MaxAttempts);
                    await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Turnstile Siteverify returned HTTP {StatusCode}.",
                        (int)response.StatusCode);
                    return false;
                }

                var result = await response.Content
                    .ReadFromJsonAsync<TurnstileSiteverifyResponse>(cancellationToken)
                    .ConfigureAwait(false);
                var valid = result is not null
                    && result.Success
                    && string.Equals(result.Action, TurnstileOptions.QueueAction, StringComparison.Ordinal)
                    && options.AllowedHostnames.Contains(result.Hostname ?? "", StringComparer.OrdinalIgnoreCase);
                if (!valid)
                {
                    logger.LogWarning(
                        "Turnstile verification failed for action {Action} and hostname {Hostname}; errors: {ErrorCodes}.",
                        result?.Action,
                        result?.Hostname,
                        string.Join(",", result?.ErrorCodes ?? []));
                }

                return valid;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception) when (attempt < MaxAttempts)
            {
                logger.LogWarning(
                    exception,
                    "Turnstile Siteverify request failed; retrying attempt {NextAttempt} of {MaxAttempts}.",
                    attempt + 1,
                    MaxAttempts);
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (attempt < MaxAttempts)
            {
                logger.LogWarning(
                    exception,
                    "Turnstile Siteverify request timed out; retrying attempt {NextAttempt} of {MaxAttempts}.",
                    attempt + 1,
                    MaxAttempts);
                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Turnstile verification could not be completed.");
                return false;
            }
        }

        return false;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(InitialRetryDelay * Math.Pow(2, attempt - 1), cancellationToken);
}

internal sealed record TurnstileSiteverifyResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("hostname")] string? Hostname,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
