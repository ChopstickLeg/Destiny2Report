using Newtonsoft.Json.Linq;

namespace D2Report.BungieClient;

public static class BungieResponseValidation
{
    private const int DestinyPrivacyRestrictionErrorCode = 1665;

    public static T EnsureSuccess<TResponse, T>(
        this TResponse response,
        Func<TResponse, T> getPayload,
        string operation)
        where TResponse : BungieResponse
    {
        if (response.ErrorCode != 1)
        {
            throw new InvalidOperationException($"{operation} failed with Bungie error code {response.ErrorCode}: {response.Message}");
        }

        return getPayload(response) ?? throw new InvalidOperationException($"{operation} returned an empty response.");
    }

    public static bool IsPrivacyRestriction(this BungieResponse response)
    {
        return response.ErrorCode == DestinyPrivacyRestrictionErrorCode
            || ContainsPrivacyMarker(response.ErrorStatus)
            || ContainsPrivacyMarker(response.Message);
    }

    public static bool IsPrivacyRestriction(this ApiException exception)
    {
        if (ContainsPrivacyMarker(exception.Response) || ContainsPrivacyMarker(exception.Message))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(exception.Response))
        {
            return false;
        }

        try
        {
            var payload = JObject.Parse(exception.Response);
            return payload["ErrorCode"]?.Value<int>() == DestinyPrivacyRestrictionErrorCode
                || ContainsPrivacyMarker(payload["ErrorStatus"]?.Value<string>())
                || ContainsPrivacyMarker(payload["Message"]?.Value<string>());
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsPrivacyMarker(string? value)
    {
        return value?.Contains("privacy", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("private", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("not public", StringComparison.OrdinalIgnoreCase) == true;
    }
}
