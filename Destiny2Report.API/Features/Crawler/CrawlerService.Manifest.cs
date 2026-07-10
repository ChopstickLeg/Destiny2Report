using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Crawler.Models.Bungie;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Newtonsoft.Json;
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

    private async Task<Dictionary<string, TDefinition>> GetManifestTableAsync<TDefinition>(DestinyManifest manifest, string tableName, CancellationToken cancellationToken)
    {
        var json = await GetManifestTableJsonAsync(manifest, tableName, cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<Dictionary<string, TDefinition>>(json)
            ?? throw new JsonSerializationException($"The {tableName} manifest table was empty or invalid.");
    }

    private async Task<string> GetManifestTableJsonAsync(DestinyManifest manifest, string tableName, CancellationToken cancellationToken)
    {
        var path = manifest.JsonWorldComponentContentPaths["en"][tableName];
        var cacheKey = $"bungie:destiny2:manifest:{manifest.Version}:{tableName}";
        return await cache.GetOrCreateAsync(
                cacheKey,
                async ct =>
                {
                    var httpClient = httpClientFactory.CreateClient();
                    return await httpClient.GetStringAsync(new Uri($"{BungieNetBaseUrl}{path}"), ct).ConfigureAwait(false);
                },
                    new HybridCacheEntryOptions
                    {
                        Expiration = ManifestTableCacheDuration,
                        // The inventory table is very large; the compact weapon cache retains only the fields we need.
                        LocalCacheExpiration = tableName == InventoryItemDefinitionType
                            ? TimeSpan.FromMinutes(1)
                            : ManifestTableCacheDuration
                    },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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

    private static TDefinition? GetDefinition<TDefinition>(IReadOnlyDictionary<string, TDefinition> table, long hash)
    {
        return TryGetHashValue(table, hash, out TDefinition? definition) ? definition : default;
    }

    private static bool TryGetHashValue<T>(
        IReadOnlyDictionary<string, T> values,
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

    private static T? GetDefinitionProperty<T>(IDictionary<string, object> source, string key)
        where T : class
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value as T ?? (value as JToken)?.ToObject<T>();
    }

    private static string? GetDefinitionString(IDictionary<string, object> source, string key)
    {
        return source.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static int GetDefinitionInt32(IDictionary<string, object> source, string key)
    {
        return source.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var result) ? result : 0;
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
