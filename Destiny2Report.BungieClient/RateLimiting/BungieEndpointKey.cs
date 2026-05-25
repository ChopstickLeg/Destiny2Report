using System.Text.RegularExpressions;

namespace D2Report.BungieClient.RateLimiting;

public static partial class BungieEndpointKey
{
    public static string FromRequest(HttpRequestMessage request)
    {
        var method = request.Method.Method.ToUpperInvariant();
        var path = request.RequestUri?.GetComponents(UriComponents.Path, UriFormat.Unescaped) ?? string.Empty;

        return $"{method} {NormalizePath(path)}";
    }

    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var segments = path
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < segments.Length; i++)
        {
            if (IsRouteValue(segments[i]))
            {
                segments[i] = "{value}";
            }
        }

        return string.Join('/', segments);
    }

    private static bool IsRouteValue(string segment)
    {
        return long.TryParse(segment, out _)
            || Guid.TryParse(segment, out _)
            || HashLikeValue().IsMatch(segment);
    }

    [GeneratedRegex("^[0-9a-f]{8,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HashLikeValue();
}
