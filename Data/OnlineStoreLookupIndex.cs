using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace CustomQuestGuide;

/// <summary>
/// Target-scoped Online Store availability lookup for paid optional-item sources.
///
/// CQG checks FFXIV Collect only for the selected target when the resolver has
/// no current in-game acquisition route or when historical-event availability
/// needs a paid-store answer. Positive and negative answers are persisted so
/// normal redraws generate zero network traffic.
/// </summary>
public sealed class OnlineStoreLookupIndex : IDisposable
{
    public sealed record LookupState(
        bool Checked,
        bool IsAvailable,
        bool IsLoading,
        string SourceText,
        DateTime CheckedUtc)
    {
        public static LookupState Unknown { get; } =
            new(false, false, false, string.Empty, DateTime.MinValue);
    }

    private sealed record CacheEntry(
        uint ItemId,
        bool IsAvailable,
        string SourceText,
        DateTime CheckedUtc);

    private sealed record CacheDocument(
        int SchemaVersion,
        List<CacheEntry> Entries);

    private const int CacheSchemaVersion = 2;
    private const string CacheFileName = "online-store-lookup-cache.json";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly IPluginLog log;
    private readonly NetworkRequestTracker networkRequests;
    private readonly string cachePath;
    private readonly CancellationTokenSource disposeCts = new();
    private readonly ConcurrentDictionary<uint, CacheEntry> cache = new();
    private readonly ConcurrentDictionary<uint, byte> inFlight = new();
    private readonly ConcurrentDictionary<uint, DateTime> retryAfterUtc = new();
    private readonly object cacheWriteLock = new();

    public OnlineStoreLookupIndex(
        string configDirectory,
        IPluginLog log,
        NetworkRequestTracker networkRequests)
    {
        this.log = log;
        this.networkRequests = networkRequests;
        cachePath = Path.Combine(configDirectory, CacheFileName);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CustomQuestGuide/1.3.5");
        LoadCache();
    }

    public LookupState GetState(uint itemId)
    {
        if (inFlight.ContainsKey(itemId))
        {
            if (cache.TryGetValue(itemId, out var cachedWhileLoading))
            {
                return new LookupState(
                    true,
                    cachedWhileLoading.IsAvailable,
                    true,
                    cachedWhileLoading.SourceText,
                    cachedWhileLoading.CheckedUtc);
            }

            return new LookupState(false, false, true, string.Empty, DateTime.MinValue);
        }

        if (!cache.TryGetValue(itemId, out var entry))
            return LookupState.Unknown;

        return new LookupState(
            true,
            entry.IsAvailable,
            false,
            entry.SourceText,
            entry.CheckedUtc);
    }

    public void EnsureLookup(TargetEntry target)
    {
        if (target.ItemId == 0)
            return;

        if (cache.TryGetValue(target.ItemId, out var existing) &&
            DateTime.UtcNow - existing.CheckedUtc <= CacheMaxAge)
        {
            return;
        }

        if (retryAfterUtc.TryGetValue(target.ItemId, out var retryAfter) &&
            DateTime.UtcNow < retryAfter)
        {
            return;
        }

        if (!inFlight.TryAdd(target.ItemId, 0))
            return;

        _ = LookupAsync(target);
    }

    public bool ClearCache()
    {
        cache.Clear();

        try
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
            return true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not delete CQG Online Store lookup cache.");
            return false;
        }
    }

    private async Task LookupAsync(TargetEntry target)
    {
        try
        {
            var foundRecord = false;
            foreach (var endpoint in GetCandidateEndpoints(target))
            {
                var endpointResult = await SearchEndpointAsync(endpoint, target, disposeCts.Token);
                foundRecord |= endpointResult.FoundRecord;

                if (endpointResult.StoreSource is not null)
                {
                    Store(target.ItemId, true, endpointResult.StoreSource);
                    return;
                }

                // For a category with one authoritative FFXIV Collect endpoint,
                // an exact record with no Online Store source is already a valid
                // negative answer; do not continue into unrelated collections.
                if (endpointResult.FoundRecord && IsAuthoritativeEndpoint(target, endpoint))
                    break;
            }

            Store(
                target.ItemId,
                false,
                foundRecord
                    ? "No current Online Store source listed by FFXIV Collect."
                    : "No matching Online Store listing found in collectible source data.");
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            // Plugin is shutting down; do not turn cancellation into a cached miss.
        }
        catch (Exception ex)
        {
            // Transient failures are deliberately not cached as a negative result.
            // Back off before another redraw is allowed to schedule a retry.
            retryAfterUtc[target.ItemId] = DateTime.UtcNow.AddMinutes(5);
            log.Debug(ex, $"CQG Online Store lookup failed for {target.Name} ({target.ItemId}).");
        }
        finally
        {
            inFlight.TryRemove(target.ItemId, out _);
        }
    }

    private async Task<(bool FoundRecord, string? StoreSource)> SearchEndpointAsync(
        string endpoint,
        TargetEntry target,
        CancellationToken cancellationToken)
    {
        var url = $"https://ffxivcollect.com/api/{endpoint}?limit=5000";
        networkRequests.Request("FFXIV Collect Online Store");

        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            networkRequests.Failure("FFXIV Collect Online Store");
            throw new HttpRequestException($"FFXIV Collect Online Store lookup failed: {endpoint} -> {(int)response.StatusCode}");
        }

        networkRequests.Success("FFXIV Collect Online Store");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        JsonElement results;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            results = document.RootElement;
        }
        else if (!document.RootElement.TryGetProperty("results", out results) ||
                 results.ValueKind != JsonValueKind.Array)
        {
            return (false, null);
        }

        foreach (var record in results.EnumerateArray())
        {
            if (!RecordMatchesTarget(record, target))
                continue;

            if (!record.TryGetProperty("sources", out var sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                return (true, null);
            }

            foreach (var source in sources.EnumerateArray())
            {
                var type = ReadString(source, "type") ?? string.Empty;
                var text = ReadString(source, "text") ?? string.Empty;
                var combined = $"{type} {text}";

                if (combined.Contains("Online Store", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("Mog Station", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("Mogstation", StringComparison.OrdinalIgnoreCase))
                {
                    // Outfit records describe a paid bundle whose individual pieces
                    // may not have standalone store listings. Preserve the bundle
                    // name so a piece such as The Warden's Thighboots can correctly
                    // present "The Warden's Attire" as the paid acquisition route.
                    var parentName = ReadString(record, "name")?.Trim() ?? string.Empty;
                    if (endpoint.Equals("outfits", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(parentName) &&
                        !NormalizeAlias(parentName).Equals(NormalizeAlias(target.Name), StringComparison.Ordinal))
                    {
                        return (true, parentName);
                    }

                    return (true, string.IsNullOrWhiteSpace(text) ? "FINAL FANTASY XIV Online Store" : text.Trim());
                }
            }

            return (true, null);
        }

        return (false, null);
    }

    private static bool RecordMatchesTarget(JsonElement record, TargetEntry target)
    {
        var normalizedTarget = NormalizeAlias(target.Name);

        if (TryReadUInt(record, "item_id", out var itemId) && itemId == target.ItemId)
            return true;

        foreach (var property in new[] { "name", "item_name", "itemName" })
        {
            if (record.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                NormalizeAlias(value.GetString() ?? string.Empty) == normalizedTarget)
            {
                return true;
            }
        }

        if (record.TryGetProperty("item", out var nestedItem) && ItemElementMatches(nestedItem, target, normalizedTarget))
            return true;

        // Outfit records can represent a store bundle while the selected target
        // is one individual gear piece contained by that bundle. Match those
        // child item records so the bundle becomes a first-class store source.
        if (record.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ItemElementMatches(item, target, normalizedTarget))
                    return true;
            }
        }

        return false;
    }

    private static bool ItemElementMatches(JsonElement item, TargetEntry target, string normalizedTarget)
    {
        if (item.ValueKind == JsonValueKind.String)
            return NormalizeAlias(item.GetString() ?? string.Empty) == normalizedTarget;

        if (item.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var idProperty in new[] { "item_id", "id" })
        {
            if (TryReadUInt(item, idProperty, out var nestedId) && nestedId == target.ItemId)
                return true;
        }

        foreach (var nameProperty in new[] { "name", "item_name", "itemName" })
        {
            if (item.TryGetProperty(nameProperty, out var nestedName) &&
                nestedName.ValueKind == JsonValueKind.String &&
                NormalizeAlias(nestedName.GetString() ?? string.Empty) == normalizedTarget)
            {
                return true;
            }
        }

        if (item.TryGetProperty("item", out var deeperItem))
            return ItemElementMatches(deeperItem, target, normalizedTarget);

        return false;
    }

    private static IReadOnlyList<string> GetCandidateEndpoints(TargetEntry target)
    {
        return target.Category switch
        {
            "Mounts" => ["mounts"],
            "Minions" => ["minions"],
            "Orchestrion Rolls" => ["orchestrions"],
            "Triple Triad Cards" => ["cards"],
            "Hairstyles" => ["hairstyles"],
            "Fashion Accessories" => ["fashions"],
            "Facewear" => ["facewear"],
            "Outfits" => ["outfits", "armoires"],
            "Armor" or "Weapons" or "Accessories" => ["outfits", "armoires"],
            _ => ["mounts", "minions", "orchestrions", "cards", "outfits", "bardings", "hairstyles", "fashions", "facewear", "frames", "armoires"],
        };
    }

    private static bool IsAuthoritativeEndpoint(TargetEntry target, string endpoint) =>
        (target.Category, endpoint) switch
        {
            ("Mounts", "mounts") => true,
            ("Minions", "minions") => true,
            ("Orchestrion Rolls", "orchestrions") => true,
            ("Triple Triad Cards", "cards") => true,
            ("Hairstyles", "hairstyles") => true,
            ("Fashion Accessories", "fashions") => true,
            ("Facewear", "facewear") => true,
            _ => false,
        };

    private void Store(uint itemId, bool isAvailable, string sourceText)
    {
        cache[itemId] = new CacheEntry(itemId, isAvailable, sourceText, DateTime.UtcNow);
        retryAfterUtc.TryRemove(itemId, out _);
        SaveCache();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;

            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(cachePath));
            if (document is null || document.SchemaVersion != CacheSchemaVersion)
                return;

            foreach (var entry in document.Entries)
                cache[entry.ItemId] = entry;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not load CQG Online Store lookup cache.");
        }
    }

    private void SaveCache()
    {
        try
        {
            lock (cacheWriteLock)
            {
                var document = new CacheDocument(
                    CacheSchemaVersion,
                    cache.Values.OrderBy(entry => entry.ItemId).ToList());

                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllText(
                    cachePath,
                    JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not save CQG Online Store lookup cache.");
        }
    }

    private static string NormalizeAlias(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    private static bool TryReadUInt(JsonElement element, string property, out uint value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var raw))
            return false;

        if (raw.ValueKind == JsonValueKind.Number)
            return raw.TryGetUInt32(out value);

        if (raw.ValueKind == JsonValueKind.String)
            return uint.TryParse(raw.GetString(), out value);

        return false;
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
        http.Dispose();
    }
}
