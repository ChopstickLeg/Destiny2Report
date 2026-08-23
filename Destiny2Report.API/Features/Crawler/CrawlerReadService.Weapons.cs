using System.Collections.Concurrent;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Crawler.Models.Bungie;
using Newtonsoft.Json;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerReadService
{
    private const long GrenadeKillsReferenceId = -1;
    private const long MeleeKillsReferenceId = -2;
    private const long SuperKillsReferenceId = -3;
    private const long UnknownKillsReferenceId = -4;
    private const string AbilityCategoryName = "Abilities";
    private const string AbilityCategoryKey = "ABILITIES";
    private const string UnknownWeaponCategoryName = "Unknown";
    private const int WeaponItemType = 3;
    private static readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<long, WeaponDefinitionSummary>>>> WeaponDefinitionCaches = new();

    public async Task WarmReportReadModelsAsync(CancellationToken cancellationToken)
    {
        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        await GetWeaponDefinitionCacheAsync(manifest.Manifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WeaponActivityModeAggregateReport?> GetWeaponActivityModeReportAsync(
        int membershipTypeId,
        long membershipId,
        WeaponActivityMode activityMode,
        CancellationToken cancellationToken)
    {
        var storedActivityMode = activityMode switch
        {
            WeaponActivityMode.PvP => "Crucible",
            WeaponActivityMode.PvE => "PvE",
            WeaponActivityMode.Gambit => "Gambit",
            _ => ""
        };
        var filter = Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipType, membershipTypeId)
            & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipId, membershipId)
            & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ActivityMode, storedActivityMode);
        var aggregates = await mongoDatabase.GetCollection<WeaponAggregate>("weapon_aggregates")
            .Find(filter)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (aggregates.Count == 0) return null;

        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await GetInventoryItemSummariesAsync(manifest, aggregates.Select(item => item.WeaponHash), cancellationToken)
            .ConfigureAwait(false);
        var resolved = aggregates.Select(weapon =>
        {
            definitions.TryGetValue(weapon.WeaponHash, out var definition);
            var (categoryName, categoryKey) = WeaponCategory(weapon.WeaponHash, definition);
            return new
            {
                Weapon = weapon,
                Name = definition?.Name ?? SyntheticWeaponName(weapon.WeaponHash),
                IconUrl = definition?.IconUrl ?? "",
                CategoryName = categoryName,
                CategoryKey = categoryKey
            };
        });

        return new WeaponActivityModeAggregateReport
        {
            ActivityMode = storedActivityMode,
            Classes = resolved
                .GroupBy(item => string.IsNullOrWhiteSpace(item.Weapon.ClassName) ? "Unknown" : item.Weapon.ClassName)
                .OrderByDescending(group => group.Sum(item => item.Weapon.Kills))
                .Select(classGroup => new WeaponClassAggregateReport
                {
                    ClassName = classGroup.Key,
                    Modes = classGroup
                        .GroupBy(item => item.Weapon.SpecificActivityMode)
                        .OrderByDescending(group => group.Sum(item => item.Weapon.Kills))
                        .Select(modeGroup => new WeaponModeAggregateReport
                        {
                            SpecificActivityModeId = modeGroup.Key,
                            SpecificActivityMode = GetSpecificActivityModeName(modeGroup.Key),
                            Categories = modeGroup
                                .GroupBy(item => (item.CategoryKey, item.CategoryName))
                                .OrderByDescending(group => group.Sum(item => item.Weapon.Kills))
                                .Select(categoryGroup => new WeaponCategoryAggregateReport
                                {
                                    OwnerMembershipType = membershipTypeId,
                                    OwnerMembershipId = membershipId,
                                    ActivityMode = storedActivityMode,
                                    ClassName = classGroup.Key,
                                    SpecificActivityMode = GetSpecificActivityModeName(modeGroup.Key),
                                    CategoryKey = categoryGroup.Key.CategoryKey,
                                    CategoryName = categoryGroup.Key.CategoryName,
                                    Kills = categoryGroup.Sum(item => item.Weapon.Kills),
                                    Weapons = categoryGroup
                                        .OrderByDescending(item => item.Weapon.Kills)
                                        .ThenBy(item => item.Name, StringComparer.Ordinal)
                                        .Select(item => new WeaponAggregateDetailReport
                                        {
                                            OwnerMembershipType = item.Weapon.OwnerMembershipType,
                                            OwnerMembershipId = item.Weapon.OwnerMembershipId,
                                            ActivityMode = item.Weapon.ActivityMode,
                                            WeaponKey = NormalizeWeaponKey(item.Name),
                                            WeaponName = item.Name,
                                            ReferenceId = item.Weapon.WeaponHash,
                                            IconUrl = item.IconUrl,
                                            CategoryKey = item.CategoryKey,
                                            CategoryName = item.CategoryName,
                                            Kills = item.Weapon.Kills
                                        })
                                        .ToList()
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private async Task<Dictionary<long, WeaponDefinitionSummary>> GetInventoryItemSummariesAsync(
        ManifestContext manifest,
        IEnumerable<long> itemHashes,
        CancellationToken cancellationToken)
    {
        var definitions = await GetWeaponDefinitionCacheAsync(manifest.Manifest, cancellationToken).ConfigureAwait(false);
        return itemHashes.Distinct()
            .Where(definitions.ContainsKey)
            .ToDictionary(hash => hash, hash => definitions[hash]);
    }

    private async Task<IReadOnlyDictionary<long, WeaponDefinitionSummary>> GetWeaponDefinitionCacheAsync(
        D2Report.BungieClient.DestinyManifest manifest,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{manifest.Version}:{InventoryItemDefinitionType}";
        var lazyCache = WeaponDefinitionCaches.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<IReadOnlyDictionary<long, WeaponDefinitionSummary>>>(
                async () => ParseWeaponDefinitions(await GetManifestTableJsonAsync(manifest, InventoryItemDefinitionType, CancellationToken.None).ConfigureAwait(false)),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazyCache.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            WeaponDefinitionCaches.TryRemove(KeyValuePair.Create(cacheKey, lazyCache));
            throw;
        }
    }

    private static IReadOnlyDictionary<long, WeaponDefinitionSummary> ParseWeaponDefinitions(string json)
    {
        var definitions = new Dictionary<long, WeaponDefinitionSummary>();
        using var stringReader = new StringReader(json);
        using var reader = new JsonTextReader(stringReader);
        var serializer = JsonSerializer.CreateDefault();
        if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
            throw new JsonReaderException("The DestinyInventoryItemDefinition manifest table must be a JSON object.");

        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.PropertyName
                || !long.TryParse(reader.Value?.ToString(), out var itemHash)
                || !reader.Read()) continue;
            if (reader.TokenType != JsonToken.StartObject)
            {
                reader.Skip();
                continue;
            }
            var definition = serializer.Deserialize<ManifestInventoryItemDefinition>(reader);
            if (definition?.ItemType == WeaponItemType)
                definitions[itemHash] = ToWeaponDefinitionSummary(definition, itemHash);
        }
        return definitions;
    }

    private static string ToUnsignedHashIdentifier(long hash) =>
        unchecked((uint)hash).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string NormalizeWeaponKey(string weaponName) => weaponName.Trim().ToUpperInvariant();

    private static string SyntheticWeaponName(long referenceId) => referenceId switch
    {
        GrenadeKillsReferenceId => "Grenade",
        MeleeKillsReferenceId => "Melee",
        SuperKillsReferenceId => "Super",
        UnknownKillsReferenceId => "Unknown",
        _ => referenceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private static (string CategoryName, string CategoryKey) WeaponCategory(long referenceId, WeaponDefinitionSummary? definition)
    {
        if (referenceId is GrenadeKillsReferenceId or MeleeKillsReferenceId or SuperKillsReferenceId)
            return (AbilityCategoryName, AbilityCategoryKey);
        var categoryName = string.IsNullOrWhiteSpace(definition?.CategoryName) ? UnknownWeaponCategoryName : definition.CategoryName;
        var categoryKey = string.IsNullOrWhiteSpace(definition?.CategoryKey) ? NormalizeWeaponKey(categoryName) : definition.CategoryKey;
        return (categoryName, categoryKey);
    }

    private static WeaponDefinitionSummary ToWeaponDefinitionSummary(ManifestInventoryItemDefinition definition, long itemHash)
    {
        var categoryName = string.IsNullOrWhiteSpace(definition.ItemTypeDisplayName)
            ? WeaponSubTypeName(definition.ItemSubType)
            : definition.ItemTypeDisplayName;
        return new WeaponDefinitionSummary(
            definition.DisplayProperties?.Name ?? ToUnsignedHashIdentifier(itemHash),
            BungieUrl(definition.DisplayProperties?.Icon),
            categoryName,
            NormalizeWeaponKey(categoryName))
        {
            TierType = definition.Inventory?.TierType ?? 0,
            DamageType = definition.DefaultDamageType
        };
    }

    private static string WeaponSubTypeName(object? itemSubType)
    {
        if (itemSubType is null || !int.TryParse(itemSubType.ToString(), out var value)) return UnknownWeaponCategoryName;
        return value switch
        {
            6 => "Auto Rifle",
            7 => "Shotgun",
            8 => "Machine Gun",
            9 => "Hand Cannon",
            10 => "Rocket Launcher",
            11 or 21 => "Fusion Rifle",
            12 => "Sniper Rifle",
            13 => "Pulse Rifle",
            14 => "Scout Rifle",
            16 => "Sidearm",
            17 => "Sword",
            22 => "Grenade Launcher",
            23 => "Submachine Gun",
            24 => "Trace Rifle",
            30 => "Bow",
            32 => "Glaive",
            _ => UnknownWeaponCategoryName
        };
    }
}
