using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;
using ReportPlayer = Destiny2Report.API.Features.Crawler.Models.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private async Task<ManifestContext> GetManifestAsync(CancellationToken cancellationToken)
    {
        var manifest = await cache.GetOrCreateAsync(
                "bungie:destiny2:manifest",
                async ct =>
                {
                    var response = await bungieClient.Destiny2_GetDestinyManifestAsync(ct).ConfigureAwait(false);
                    return EnsureSuccess(response, item => item.Response, "GetDestinyManifest");
                },
                new HybridCacheEntryOptions
                {
                    Expiration = ManifestCacheDuration,
                    LocalCacheExpiration = ManifestCacheDuration
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ManifestContext(manifest, this);
    }

    private async Task<JObject> GetManifestTableAsync(DestinyManifest manifest, string tableName, CancellationToken cancellationToken)
    {
        var path = manifest.JsonWorldComponentContentPaths["en"][tableName];
        var cacheKey = $"bungie:destiny2:manifest:{manifest.Version}:{tableName}";
        var json = await cache.GetOrCreateAsync(
                cacheKey,
                async ct =>
                {
                    var httpClient = httpClientFactory.CreateClient();
                    return await httpClient.GetStringAsync(new Uri($"{BungieNetBaseUrl}{path}"), ct).ConfigureAwait(false);
                },
                new HybridCacheEntryOptions
                {
                    Expiration = ManifestTableCacheDuration,
                    LocalCacheExpiration = ManifestTableCacheDuration
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return JObject.Parse(json);
    }

    private static T EnsureSuccess<TResponse, T>(TResponse response, Func<TResponse, T> getPayload, string operation)
        where TResponse : BungieResponse
    {
        return response.EnsureSuccess(getPayload, operation);
    }

    private static bool IsPrivateProfileResponse(BungieResponse response)
    {
        return response.IsPrivacyRestriction();
    }

    private static bool IsPrivateProfileException(Exception exception)
    {
        if (exception is PrivatePlayerUnavailableException)
        {
            return true;
        }

        if (exception is ApiException apiException)
        {
            return IsPrivateProfileApiException(apiException);
        }

        return false;
    }

    private static bool IsPrivateProfileApiException(ApiException exception)
    {
        return exception.IsPrivacyRestriction();
    }

    private static JObject? GetDefinition(JObject table, long hash)
    {
        if (table[hash.ToString()] is JObject definition)
        {
            return definition;
        }

        if (hash is >= int.MinValue and <= int.MaxValue)
        {
            var unsignedHash = unchecked((uint)(int)hash).ToString();
            if (table[unsignedHash] is JObject unsignedDefinition)
            {
                return unsignedDefinition;
            }
        }

        if (hash is > int.MaxValue and <= uint.MaxValue)
        {
            var signedHash = unchecked((int)(uint)hash).ToString();
            if (table[signedHash] is JObject signedDefinition)
            {
                return signedDefinition;
            }
        }

        return null;
    }

    private static bool TryGetHashValue<T>(
        IDictionary<string, T> values,
        long hash,
        out T? value)
    {
        if (values.TryGetValue(hash.ToString(), out value))
        {
            return true;
        }

        if (hash is >= int.MinValue and <= int.MaxValue)
        {
            return values.TryGetValue(unchecked((uint)(int)hash).ToString(), out value);
        }

        if (hash is > int.MaxValue and <= uint.MaxValue)
        {
            return values.TryGetValue(unchecked((int)(uint)hash).ToString(), out value);
        }

        value = default;
        return false;
    }

    private static JObject? TryGetJObject(IDictionary<string, object> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value as JObject ?? JObject.FromObject(value);
    }

    private static string BungieUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{BungieNetBaseUrl}{path}";
    }
}
