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
/// Generic last-line source resolver backed by Garland Tools item documents.
///
/// This is used both as a last-line resolver and as a specificity-enrichment
/// layer for items whose built-in source is truthful but generic.
///
/// Garland's item document is item-specific and can expose vendors, trade shops,
/// gathering nodes, reduction, crafting, instances, leves, drops, ventures,
/// gardening and voyage relationships.
///
/// Positive results and confirmed misses are cached locally so CQG does not
/// hammer the service on every start.
/// </summary>
public sealed class GarlandSourceIndex : IDisposable
{
    private const string CacheFileName = "garland-source-cache.json";
    private const int CacheSchemaVersion = 3;
    private static readonly TimeSpan CacheFreshness = TimeSpan.FromHours(24);

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly IPluginLog log;
    private readonly NetworkRequestTracker networkRequests;
    private readonly string cachePath;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource disposeCts = new();

    private readonly ConcurrentDictionary<uint, List<SourceEntry>> sources = new();

    // Cache both positive hits and confirmed misses. Without this, broad-system
    // and curated-fallback items that Garland does not know are requested again
    // on every startup even while the cache is fresh.
    private readonly ConcurrentDictionary<uint, byte> checkedItemIds = new();
    private readonly ConcurrentDictionary<uint, byte> inFlightItemIds = new();
    private readonly ConcurrentDictionary<uint, DateTime> retryAfterUtc = new();
    private readonly ConcurrentQueue<uint> targetRequestQueue = new();
    private readonly ConcurrentDictionary<uint, byte> queuedItemIds = new();
    private int targetQueueProcessorRunning;
    private DateTime nextTargetRequestUtc = DateTime.MinValue;

    private DateTime cacheWrittenUtc = DateTime.MinValue;

    public bool IsLoading { get; private set; }
    public bool IsLoaded { get; private set; }
    public int RequestedCount { get; private set; }

    private int completedCount;
    public int CompletedCount => completedCount;

    public int ResolvedItemCount => sources.Count;
    public int CachedCheckedCount => checkedItemIds.Count;
    public int CachedMissCount => Math.Max(0, checkedItemIds.Count - sources.Count);
    public bool CacheFilePresent => File.Exists(cachePath);
    public DateTime CacheWrittenUtc => cacheWrittenUtc;
    public bool CacheIsFresh => cacheWrittenUtc != DateTime.MinValue &&
                                DateTime.UtcNow - cacheWrittenUtc < CacheFreshness;
    public string Status { get; private set; } = "Not loaded";

    public GarlandSourceIndex(string configDirectory, IPluginLog log, NetworkRequestTracker networkRequests)
    {
        this.log = log;
        this.networkRequests = networkRequests;
        cachePath = Path.Combine(configDirectory, CacheFileName);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CustomQuestGuide/1.1.13");

        LoadCache();
    }

    public IReadOnlyList<SourceEntry> GetSources(uint itemId)
    {
        if (!sources.TryGetValue(itemId, out var list))
            return [];

        lock (list)
            return list.ToArray();
    }

    public bool HasSource(uint itemId) =>
        sources.TryGetValue(itemId, out var list) && list.Count > 0;

    public bool IsChecked(uint itemId) =>
        itemId != 0 && checkedItemIds.ContainsKey(itemId);

    public bool IsCachedMiss(uint itemId) =>
        IsChecked(itemId) && !HasSource(itemId);

    /// <summary>
    /// Requests Garland enrichment for one actively-selected item only. Cached
    /// hits/misses are reused and transient failures are backed off. This is the
    /// normal CQG network path; bulk RefreshAsync is retained for diagnostics but
    /// is never called automatically by startup or /cqg.
    /// </summary>
    public void EnsureRequested(uint itemId)
    {
        if (itemId == 0)
            return;

        var cacheFresh = cacheWrittenUtc != DateTime.MinValue &&
                         DateTime.UtcNow - cacheWrittenUtc < CacheFreshness;
        if (cacheFresh && checkedItemIds.ContainsKey(itemId))
        {
            networkRequests.CacheHit("Garland item");
            return;
        }

        if (retryAfterUtc.TryGetValue(itemId, out var retryAfter) && DateTime.UtcNow < retryAfter)
        {
            networkRequests.Suppressed("Garland item");
            return;
        }

        if (inFlightItemIds.ContainsKey(itemId) || !queuedItemIds.TryAdd(itemId, 0))
            return;

        networkRequests.CacheMiss("Garland item");
        targetRequestQueue.Enqueue(itemId);
        StartTargetQueueProcessor();
    }

    private void StartTargetQueueProcessor()
    {
        if (Interlocked.CompareExchange(ref targetQueueProcessorRunning, 1, 0) != 0)
            return;

        _ = ProcessTargetQueueAsync();
    }

    private async Task ProcessTargetQueueAsync()
    {
        try
        {
            while (!disposeCts.IsCancellationRequested && targetRequestQueue.TryDequeue(out var itemId))
            {
                queuedItemIds.TryRemove(itemId, out _);

                if (checkedItemIds.ContainsKey(itemId) && CacheIsFresh)
                {
                    networkRequests.CacheHit("Garland item");
                    continue;
                }

                if (retryAfterUtc.TryGetValue(itemId, out var retryAfter) && DateTime.UtcNow < retryAfter)
                {
                    networkRequests.Suppressed("Garland item");
                    continue;
                }

                var wait = nextTargetRequestUtc - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, disposeCts.Token);

                if (!inFlightItemIds.TryAdd(itemId, 0))
                    continue;

                nextTargetRequestUtc = DateTime.UtcNow.AddSeconds(2);
                await ResolveOneAsync(itemId);
            }
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref targetQueueProcessorRunning, 0);

            // Close the race where another target was queued after the loop
            // observed an empty queue but before the processor flag was reset.
            if (!targetRequestQueue.IsEmpty && !disposeCts.IsCancellationRequested)
                StartTargetQueueProcessor();
        }
    }

    private async Task ResolveOneAsync(uint itemId)
    {
        try
        {
            var resolved = await LoadItemAsync(itemId, disposeCts.Token);
            if (resolved is null)
            {
                retryAfterUtc[itemId] = DateTime.UtcNow.AddMinutes(5);
                return;
            }

            if (resolved.Count > 0)
                sources[itemId] = resolved;
            else
                sources.TryRemove(itemId, out _);

            checkedItemIds[itemId] = 0;
            retryAfterUtc.TryRemove(itemId, out _);
            cacheWrittenUtc = DateTime.UtcNow;
            SaveCache();
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            retryAfterUtc[itemId] = DateTime.UtcNow.AddMinutes(5);
            log.Debug(ex, $"Garland lookup failed for item {itemId}.");
        }
        finally
        {
            inFlightItemIds.TryRemove(itemId, out _);
        }
    }

    public async Task RefreshAsync(IEnumerable<uint> itemIds, bool force = false)
    {
        if (!await refreshGate.WaitAsync(0))
            return;

        var requested = itemIds
            .Where(id => id != 0)
            .Distinct()
            .ToArray();

        if (requested.Length == 0)
        {
            IsLoaded = true;
            Status = $"Ready ({sources.Count:N0} cached item source records)";
            refreshGate.Release();
            return;
        }

        var cacheFresh =
            !force &&
            cacheWrittenUtc != DateTime.MinValue &&
            DateTime.UtcNow - cacheWrittenUtc < CacheFreshness;

        var queue = cacheFresh
            ? requested.Where(id => !checkedItemIds.ContainsKey(id)).ToArray()
            : requested;

        RequestedCount = queue.Length;
        completedCount = 0;

        if (queue.Length == 0)
        {
            IsLoaded = true;
            Status = $"Ready ({sources.Count:N0} cached item source records)";
            refreshGate.Release();
            return;
        }

        IsLoading = true;
        Status = $"Garland fallback: 0 / {queue.Length:N0}";

        try
        {
            using var gate = new SemaphoreSlim(1);

            var tasks = queue.Select(async itemId =>
            {
                await gate.WaitAsync(disposeCts.Token);

                try
                {
                    var resolved = await LoadItemAsync(itemId, disposeCts.Token);

                    if (resolved is not null)
                    {
                        if (resolved.Count > 0)
                            sources[itemId] = resolved;

                        // Record only successful item-document lookups. Transient
                        // HTTP/schema failures must not become 24-hour negatives.
                        checkedItemIds[itemId] = 0;
                    }
                }
                catch (Exception ex)
                {
                    log.Debug(ex, $"Garland lookup failed for item {itemId}.");
                }
                finally
                {
                    var done = Interlocked.Increment(ref completedCount);
                    Status =
                        $"Garland fallback: {done:N0} / {queue.Length:N0} " +
                        $"({sources.Count:N0} resolved)";
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);

            IsLoaded = true;
            cacheWrittenUtc = DateTime.UtcNow;
            SaveCache();

            Status =
                $"Ready ({sources.Count:N0} Garland item source records; " +
                $"{queue.Length:N0} checked this run)";
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            Status = "Garland fallback stopped";
        }
        catch (Exception ex)
        {
            Status = $"Garland fallback failed: {ex.Message}";
            log.Warning(ex, "Could not refresh Garland Tools source data.");
        }
        finally
        {
            IsLoading = false;
            refreshGate.Release();
        }
    }

    private async Task<List<SourceEntry>?> LoadItemAsync(uint itemId, CancellationToken cancellationToken)
    {
        // Garland's item-doc endpoint is a single comprehensive item request.
        var url =
            $"https://www.garlandtools.org/db/doc/item/en/3/{itemId}.json";

        networkRequests.Request("Garland item");
        using var response = await http.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            networkRequests.Failure("Garland item");
            // A 404 is an authoritative "Garland has no item document" miss;
            // cache it. Other HTTP failures may be transient and should retry.
            if ((int)response.StatusCode == 429)
                retryAfterUtc[itemId] = DateTime.UtcNow.AddMinutes(30);
            return (int)response.StatusCode == 404 ? [] : null;
        }
        networkRequests.Success("Garland item");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream);

        if (!document.RootElement.TryGetProperty("item", out var item) ||
            item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var partialNames = BuildPartialNameMap(document.RootElement);
        var result = new List<SourceEntry>();

        // Crafting.
        if (HasContent(item, "craft"))
        {
            AddUnique(
                result,
                new SourceEntry(
                    "Crafting",
                    "Crafted",
                    "Garland Tools",
                    Priority: 60));
        }

        // Ordinary vendors.
        foreach (var id in ReadIdArray(item, "vendors"))
        {
            var name = LookupPartialName(partialNames, "npc", id);

            AddUnique(
                result,
                new SourceEntry(
                    "Vendor",
                    name is null ? "Vendor" : $"Vendor: {name}",
                    "Garland Tools",
                    Priority: 60));
        }

        // Special currency / item trades. Garland exposes the shop name,
        // participating NPC IDs, and listing costs directly in the item document.
        // Prefer that detail over a generic "currency shop" label.
        if (item.TryGetProperty("tradeShops", out var tradeShops) &&
            tradeShops.ValueKind == JsonValueKind.Array)
        {
            foreach (var shop in tradeShops.EnumerateArray())
            {
                if (shop.ValueKind != JsonValueKind.Object)
                    continue;

                var shopName = ReadString(shop, "shop") ?? "Special / Currency Shop";
                var npcNames = ReadIdsFromElement(shop, "npcs")
                    .Select(id => LookupPartialName(partialNames, "npc", id))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToArray();

                var detail = DescribeTradeShopCost(shop, itemId, partialNames);

                if (npcNames.Length > 0)
                {
                    // Emit one source per NPC. Combining multiple contacts into
                    // one display string makes downstream location lookup treat
                    // "NPC A, NPC B" as a single nonexistent character.
                    foreach (var npcName in npcNames)
                    {
                        AddUnique(
                            result,
                            new SourceEntry(
                                "Vendor",
                                $"{shopName} — {npcName}",
                                string.IsNullOrWhiteSpace(detail)
                                    ? "Garland Tools trade-shop data"
                                    : detail,
                                Priority: 28,
                                SpecificityHint: "Named item-specific"));
                    }
                }
                else
                {
                    AddUnique(
                        result,
                        new SourceEntry(
                            "Vendor",
                            shopName,
                            string.IsNullOrWhiteSpace(detail)
                                ? "Garland Tools trade-shop data"
                                : detail,
                            Priority: 42));
                }
            }
        }
        else if (HasContent(item, "tradeShops"))
        {
            AddUnique(
                result,
                new SourceEntry(
                    "Vendor",
                    "Special / Currency Shop",
                    "Garland Tools",
                    Priority: 60));
        }

        // Gathering nodes.
        foreach (var id in ReadIdArray(item, "nodes"))
        {
            var name = LookupPartialName(partialNames, "node", id);

            AddUnique(
                result,
                new SourceEntry(
                    "Gathering",
                    name is null ? "Gathering Node" : $"Gathering: {name}",
                    "Garland Tools",
                    Priority: 60));
        }

        // Fishing / spearfishing locations. Garland's item documents have
        // changed shape over time, so probe the known relation names and only
        // emit a source when the referenced partial is explicitly a fishing
        // spot. Unknown numeric relations are ignored rather than guessed.
        foreach (var property in new[]
                 {
                     "fishingSpots", "fishSpots", "fishing",
                     "spearfishingSpots", "spearfishing", "spots"
                 })
        {
            foreach (var id in ReadIdArray(item, property))
            {
                var name = LookupPartialNameAny(
                    partialNames,
                    id,
                    "fishing", "fishingspot", "fishingSpot",
                    "fishing_spot", "spearfishing", "spearfishingspot");

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                AddUnique(
                    result,
                    new SourceEntry(
                        "Fishing",
                        $"Fishing: {name}",
                        "Garland Tools fishing data",
                        Priority: 58));
            }
        }

        // Aetherial reduction.
        foreach (var id in ReadIdArray(item, "reducedFrom"))
        {
            var name = LookupPartialName(partialNames, "item", id);

            AddUnique(
                result,
                new SourceEntry(
                    "Aetherial Reduction",
                    name is null
                        ? "Aetherial Reduction"
                        : $"Aetherial Reduction: {name}",
                    "Garland Tools",
                    Priority: 60));
        }

        // Instances. Garland's relationship is item-specific, so even when a
        // referenced instance name is unavailable we can safely say duty drop.
        var instanceIds = ReadIdArray(item, "instances").ToArray();

        foreach (var id in instanceIds)
        {
            var name =
                LookupPartialName(partialNames, "instance", id) ??
                LookupPartialName(partialNames, "duty", id);

            AddUnique(
                result,
                new SourceEntry(
                    "Duty",
                    name is null ? "Duty / Instance Drop" : $"Duty: {name}",
                    "Garland Tools",
                    Priority: 60));
        }

        // Monster drops. Garland's mob module adds item -> mob references,
        // so use the referenced partial name when the document provides it.
        var namedDrop = false;
        foreach (var id in ReadIdArray(item, "drops"))
        {
            var name = LookupPartialNameAny(
                partialNames,
                id,
                "mob", "bnpc", "battleNpc", "battlenpc", "npc");

            if (string.IsNullOrWhiteSpace(name))
                continue;

            namedDrop = true;
            AddUnique(
                result,
                new SourceEntry(
                    "Monster",
                    $"Monster: {name}",
                    "Garland Tools mob-drop data",
                    Priority: 58));
        }

        if (!namedDrop && HasContent(item, "drops"))
        {
            AddUnique(
                result,
                new SourceEntry(
                    "Monster",
                    "Monster Drop",
                    "Garland Tools",
                    Priority: 65));
        }

        // Leve rewards.
        var namedLeve = false;
        foreach (var id in ReadIdArray(item, "leves"))
        {
            var name = LookupPartialNameAny(
                partialNames,
                id,
                "leve", "levequest", "guildleve");

            if (string.IsNullOrWhiteSpace(name))
                continue;

            namedLeve = true;
            AddUnique(
                result,
                new SourceEntry(
                    "Levequest",
                    $"Levequest: {name}",
                    "Garland Tools leve data",
                    Priority: 58));
        }

        if (!namedLeve && HasContent(item, "leves"))
        {
            AddUnique(
                result,
                new SourceEntry(
                    "Levequest",
                    "Levequest Reward",
                    "Garland Tools",
                    Priority: 60));
        }

        // Other documented/supplemental acquisition families. Probe for named
        // relations first and fall back to truthful system-level labels.
        AddNamedOrPresenceSource(
            item, result, partialNames,
            new[] { "ventures", "venture" },
            "Retainer Venture", "Retainer Venture",
            new[] { "venture", "retainerventure", "task" });

        AddNamedOrPresenceSource(
            item, result, partialNames,
            new[] { "gardening", "garden", "seeds", "grow" },
            "Gardening", "Gardening",
            new[] { "item", "gardening", "seed" });

        AddNamedOrPresenceSource(
            item, result, partialNames,
            new[] { "treasure", "maps", "treasureMaps" },
            "Treasure Hunt", "Treasure Hunt",
            new[] { "item", "treasure", "map" });

        AddNamedOrPresenceSource(
            item, result, partialNames,
            new[] { "voyages", "airship", "submarine" },
            "Voyage", "Airship / Submarine Voyage",
            new[] { "voyage", "airship", "submarine", "sector" });

        AddNamedOrPresenceSource(
            item, result, partialNames,
            new[] { "desynth", "desynthesis", "desynths" },
            "Desynthesis", "Desynthesis",
            new[] { "item" });

        return result;
    }

    private static Dictionary<(string Type, ulong Id), string> BuildPartialNameMap(
        JsonElement root)
    {
        var result = new Dictionary<(string Type, ulong Id), string>();

        if (!root.TryGetProperty("partials", out var partials) ||
            partials.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var partial in partials.EnumerateArray())
        {
            var type = ReadString(partial, "type");
            if (string.IsNullOrWhiteSpace(type))
                continue;

            ulong id = 0;

            if (partial.TryGetProperty("id", out var idValue))
            {
                if (idValue.ValueKind == JsonValueKind.Number)
                    idValue.TryGetUInt64(out id);
                else if (idValue.ValueKind == JsonValueKind.String)
                    ulong.TryParse(idValue.GetString(), out id);
            }

            if (id == 0 ||
                !partial.TryGetProperty("obj", out var obj) ||
                obj.ValueKind != JsonValueKind.Object)
                continue;

            var name =
                ReadString(obj, "n") ??
                ReadString(obj, "name");

            if (!string.IsNullOrWhiteSpace(name))
                result[(type.Trim().ToLowerInvariant(), id)] = name.Trim();
        }

        return result;
    }

    private static string? LookupPartialName(
        IReadOnlyDictionary<(string Type, ulong Id), string> map,
        string type,
        ulong id) =>
        map.TryGetValue((type.ToLowerInvariant(), id), out var name)
            ? name
            : null;

    private static string? LookupPartialNameAny(
        IReadOnlyDictionary<(string Type, ulong Id), string> map,
        ulong id,
        params string[] types)
    {
        foreach (var type in types)
        {
            var name = LookupPartialName(map, type, id);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static IEnumerable<ulong> ReadIdArray(
        JsonElement item,
        string property)
    {
        if (!item.TryGetProperty(property, out var value))
            yield break;

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Number &&
                    entry.TryGetUInt64(out var numeric) &&
                    numeric != 0)
                {
                    yield return numeric;
                    continue;
                }

                if (entry.ValueKind == JsonValueKind.String &&
                    ulong.TryParse(entry.GetString(), out var textId) &&
                    textId != 0)
                {
                    yield return textId;
                    continue;
                }

                if (entry.ValueKind == JsonValueKind.Object)
                {
                    foreach (var candidate in new[] { "id", "i", "item", "instance" })
                    {
                        if (!entry.TryGetProperty(candidate, out var nested))
                            continue;

                        if (nested.ValueKind == JsonValueKind.Number &&
                            nested.TryGetUInt64(out var nestedId) &&
                            nestedId != 0)
                        {
                            yield return nestedId;
                            break;
                        }

                        if (nested.ValueKind == JsonValueKind.String &&
                            ulong.TryParse(nested.GetString(), out nestedId) &&
                            nestedId != 0)
                        {
                            yield return nestedId;
                            break;
                        }
                    }
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Number &&
                 value.TryGetUInt64(out var single) &&
                 single != 0)
        {
            yield return single;
        }
    }


    private static IEnumerable<ulong> ReadIdsFromElement(
        JsonElement obj,
        string property)
    {
        if (!obj.TryGetProperty(property, out var value))
            yield break;

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray())
            {
                ulong id = 0;

                if (entry.ValueKind == JsonValueKind.Number)
                    entry.TryGetUInt64(out id);
                else if (entry.ValueKind == JsonValueKind.String)
                    ulong.TryParse(entry.GetString(), out id);
                else if (entry.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "id", "i", "item" })
                    {
                        if (!entry.TryGetProperty(key, out var nested))
                            continue;

                        if (nested.ValueKind == JsonValueKind.Number)
                            nested.TryGetUInt64(out id);
                        else if (nested.ValueKind == JsonValueKind.String)
                            ulong.TryParse(nested.GetString(), out id);

                        if (id != 0)
                            break;
                    }
                }

                if (id != 0)
                    yield return id;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number &&
                 value.TryGetUInt64(out var single) &&
                 single != 0)
        {
            yield return single;
        }
    }

    private static string? DescribeTradeShopCost(
        JsonElement shop,
        uint itemId,
        IReadOnlyDictionary<(string Type, ulong Id), string> partialNames)
    {
        if (!shop.TryGetProperty("listings", out var listings) ||
            listings.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var listing in listings.EnumerateArray())
        {
            if (listing.ValueKind != JsonValueKind.Object)
                continue;

            var receivesTarget =
                ReadListingEntries(listing, "item")
                    .Any(entry => entry.Id == itemId);

            if (!receivesTarget)
                continue;

            var costs = ReadListingEntries(listing, "currency")
                .Select(entry =>
                {
                    var name =
                        LookupPartialName(partialNames, "item", entry.Id) ??
                        $"Item #{entry.Id}";

                    return $"{Math.Max(1u, entry.Amount):N0} {name}";
                })
                .ToArray();

            if (costs.Length > 0)
                return $"{string.Join(" + ", costs)} | Garland Tools trade-shop data";
        }

        return null;
    }

    private static IEnumerable<(uint Id, uint Amount)> ReadListingEntries(
        JsonElement listing,
        string property)
    {
        if (!listing.TryGetProperty(property, out var values) ||
            values.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var entry in values.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            uint id = 0;
            uint amount = 1;

            if (entry.TryGetProperty("id", out var idValue))
            {
                if (idValue.ValueKind == JsonValueKind.Number)
                    idValue.TryGetUInt32(out id);
                else if (idValue.ValueKind == JsonValueKind.String)
                    uint.TryParse(idValue.GetString(), out id);
            }

            if (entry.TryGetProperty("amount", out var amountValue))
            {
                if (amountValue.ValueKind == JsonValueKind.Number)
                    amountValue.TryGetUInt32(out amount);
                else if (amountValue.ValueKind == JsonValueKind.String)
                    uint.TryParse(amountValue.GetString(), out amount);
            }

            if (id != 0)
                yield return (id, amount == 0 ? 1u : amount);
        }
    }

    private static void AddNamedOrPresenceSource(
        JsonElement item,
        List<SourceEntry> sources,
        IReadOnlyDictionary<(string Type, ulong Id), string> partialNames,
        IReadOnlyList<string> properties,
        string kind,
        string fallbackName,
        IReadOnlyList<string> partialTypes)
    {
        var sawContent = false;
        var sawName = false;

        foreach (var property in properties)
        {
            if (!HasContent(item, property))
                continue;

            sawContent = true;

            foreach (var id in ReadIdArray(item, property))
            {
                var name = LookupPartialNameAny(
                    partialNames,
                    id,
                    partialTypes.ToArray());

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                sawName = true;
                AddUnique(
                    sources,
                    new SourceEntry(
                        kind,
                        $"{fallbackName}: {name}",
                        "Garland Tools",
                        Priority: 58));
            }
        }

        if (sawContent && !sawName)
        {
            AddUnique(
                sources,
                new SourceEntry(
                    kind,
                    fallbackName,
                    "Garland Tools",
                    Priority: 65));
        }
    }

    private static bool HasContent(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Number => true,
            JsonValueKind.True => true,
            _ => false,
        };
    }

    private static void AddPresenceSource(
        JsonElement item,
        List<SourceEntry> sources,
        string property,
        string kind,
        string name)
    {
        if (!HasContent(item, property))
            return;

        AddUnique(
            sources,
            new SourceEntry(
                kind,
                name,
                "Garland Tools",
                Priority: 65));
    }

    private static void AddUnique(
        List<SourceEntry> list,
        SourceEntry source)
    {
        if (list.Any(existing =>
                string.Equals(existing.Kind, source.Kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
            return;

        list.Add(source);
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));

            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number ||
                !schemaVersion.TryGetInt32(out var parsedSchemaVersion) ||
                parsedSchemaVersion != CacheSchemaVersion)
            {
                // Parser/enrichment behavior changed. Ignore the old positive and
                // negative cache once so affected items can be re-read with the
                // current specificity rules.
                return;
            }

            if (document.RootElement.TryGetProperty("writtenUtc", out var written) &&
                DateTime.TryParse(written.GetString(), out var parsed))
            {
                cacheWrittenUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            if (document.RootElement.TryGetProperty("checked", out var checkedIds) &&
                checkedIds.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in checkedIds.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Number &&
                        value.TryGetUInt32(out var checkedId) &&
                        checkedId != 0)
                    {
                        checkedItemIds[checkedId] = 0;
                    }
                }
            }

            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Object)
                return;

            foreach (var property in items.EnumerateObject())
            {
                if (!uint.TryParse(property.Name, out var itemId) ||
                    property.Value.ValueKind != JsonValueKind.Array)
                    continue;

                var list = new List<SourceEntry>();

                foreach (var source in property.Value.EnumerateArray())
                {
                    var kind = ReadString(source, "kind");
                    var name = ReadString(source, "name");
                    var detail = ReadString(source, "detail");

                    if (string.IsNullOrWhiteSpace(kind) ||
                        string.IsNullOrWhiteSpace(name))
                        continue;

                    list.Add(new SourceEntry(
                        kind,
                        name,
                        detail,
                        Priority: 60));
                }

                if (list.Count > 0)
                {
                    sources[itemId] = list;
                    // Backward compatibility with pre-v0.36 caches that did not
                    // persist an explicit checked-ID list.
                    checkedItemIds[itemId] = 0;
                }
            }

            if (sources.Count > 0)
            {
                IsLoaded = true;
                Status = $"Ready ({sources.Count:N0} cached Garland source records)";
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not load CQG Garland source cache.");
        }
    }

    private void SaveCache()
    {
        try
        {
            var items = sources.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value.Select(source => new
                {
                    kind = source.Kind,
                    name = source.Name,
                    detail = source.Detail,
                }).ToArray());

            var payload = new
            {
                schemaVersion = CacheSchemaVersion,
                writtenUtc = cacheWrittenUtc.ToString("O"),
                @checked = checkedItemIds.Keys.OrderBy(id => id).ToArray(),
                items,
            };

            File.WriteAllText(
                cachePath,
                JsonSerializer.Serialize(
                    payload,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not save CQG Garland source cache.");
        }
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    public bool ClearCache()
    {
        if (IsLoading || inFlightItemIds.Count > 0)
            return false;

        sources.Clear();
        checkedItemIds.Clear();
        retryAfterUtc.Clear();
        nextTargetRequestUtc = DateTime.MinValue;
        cacheWrittenUtc = DateTime.MinValue;
        IsLoaded = false;
        RequestedCount = 0;
        completedCount = 0;
        Status = "Not loaded";

        try
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not delete CQG Garland source cache.");
        }

        return true;
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        http.Dispose();
    }
}
