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
using Lumina.Excel.Sheets;

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
/// Positive results and confirmed misses are persisted locally. Validated cache
/// entries are reused indefinitely until the local FFXIV data fingerprint changes;
/// stale entries remain immediately usable while the selected item revalidates.
/// </summary>
public sealed class GarlandSourceIndex : IDisposable
{
    private const string CacheFileName = "garland-source-cache.json";
    private const int CacheSchemaVersion = 14;
    private const int MaxMonsterEnrichmentAttempts = 3;
    private static readonly TimeSpan NoChangesNoticeDuration = TimeSpan.FromSeconds(10);

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly NetworkRequestTracker networkRequests;
    private readonly string cachePath;
    private readonly string currentGameDataFingerprint;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource disposeCts = new();

    private readonly ConcurrentDictionary<uint, List<SourceEntry>> sources = new();
    // Stable per-item mutation gates prevent concurrent monster workers from
    // overwriting one another when they publish live enrichment for the same item.
    private readonly ConcurrentDictionary<uint, object> sourceMutationGates = new();

    // Cache both positive hits and confirmed misses. Without this, broad-system
    // and curated-fallback items that Garland does not know would be requested
    // repeatedly even though the provider already returned a definitive answer.
    private readonly ConcurrentDictionary<uint, byte> checkedItemIds = new();
    // Network-derived cache entries are associated with the local game-data
    // fingerprint against which they were last validated. Cached data remains
    // usable even when this fingerprint changes; selection of that item queues
    // one revalidation request instead of discarding the cached answer.
    private readonly ConcurrentDictionary<uint, string> validatedGameDataFingerprints = new();

    // Monster metadata is globally owned by the monster, never by an item.
    // Item documents only discover provider identities; this dedicated cache is
    // consulted first for every monster and persisted separately from item data.
    private readonly MonsterMetadataCache monsterCache;

    // Network work is scheduled by stable identity instead of being embedded in
    // the item parser. Item discovery and monster enrichment are independent
    // pipelines: an item document can finish immediately while slow mob metadata
    // continues in the background and merges into every interested item.
    private readonly KeyedWorkScheduler<uint> itemWork;
    private readonly KeyedWorkScheduler<ulong> monsterWork;
    // Item IDs waiting on each monster are tracked only for target-scoped status.
    // Monster facts themselves live exclusively in MonsterMetadataCache.
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<uint, byte>> monsterSubscribers = new();
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<ulong, byte>> pendingMonsterIdsByItem = new();
    private readonly ConcurrentDictionary<ulong, DateTime> monsterRetryAfterUtc = new();
    private readonly ConcurrentDictionary<ulong, int> monsterAttemptCounts = new();

    private readonly ConcurrentDictionary<uint, DateTime> noChangesNoticeUntilUtc = new();
    private readonly ConcurrentDictionary<uint, DateTime> retryAfterUtc = new();

    private readonly object cacheWriteLock = new();
    private DateTime cacheWrittenUtc = DateTime.MinValue;

    public bool IsLoading { get; private set; }
    public bool IsLoaded { get; private set; }
    public int RequestedCount { get; private set; }

    private int completedCount;
    public int CompletedCount => completedCount;

    public int ResolvedItemCount => sources.Count;
    public int PendingItemRequestCount => itemWork.PendingCount;
    public int PendingMonsterRequestCount => monsterWork.PendingCount;
    public int CachedCheckedCount => checkedItemIds.Count;
    public int CachedMissCount => Math.Max(0, checkedItemIds.Count - sources.Count);
    public int CachedMonsterCount => monsterCache.Count;
    public bool CacheFilePresent => File.Exists(cachePath);
    public bool MonsterCacheFilePresent => monsterCache.FilePresent;
    public DateTime CacheWrittenUtc => cacheWrittenUtc;
    public bool CacheIsFresh => checkedItemIds.Keys.All(IsValidatedForCurrentGameData);
    public string Status { get; private set; } = "Not loaded";

    public GarlandSourceIndex(
        IDataManager data,
        string configDirectory,
        string currentGameDataFingerprint,
        IPluginLog log,
        NetworkRequestTracker networkRequests)
    {
        this.data = data;
        this.currentGameDataFingerprint = currentGameDataFingerprint;
        this.log = log;
        this.networkRequests = networkRequests;
        cachePath = Path.Combine(configDirectory, CacheFileName);
        monsterCache = new MonsterMetadataCache(configDirectory, log);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CustomQuestGuide/1.3.4.20");

        // Keep item documents conservative and serial. Monster metadata is much
        // smaller and independent, so allow a few concurrent requests while
        // pacing request starts. This removes the old N * 1-second blocking chain
        // without turning one selected item into an uncontrolled request burst.
        itemWork = new KeyedWorkScheduler<uint>(
            maxConcurrency: 1,
            minimumStartInterval: TimeSpan.FromSeconds(2),
            ProcessItemWorkAsync,
            disposeCts.Token);
        // Monster enrichment is intentionally sequential. The item document
        // publishes the complete monster list first; then this worker walks that
        // list one monster at a time, cache-first, persisting each successful
        // result before continuing to the next monster.
        monsterWork = new KeyedWorkScheduler<ulong>(
            maxConcurrency: 1,
            minimumStartInterval: TimeSpan.FromMilliseconds(300),
            ProcessMonsterWorkAsync,
            disposeCts.Token);

        LoadCache();
    }

    public IReadOnlyList<SourceEntry> GetSources(uint itemId)
    {
        if (itemId == 0)
            return [];

        var gate = sourceMutationGates.GetOrAdd(itemId, _ => new object());
        lock (gate)
        {
            return sources.TryGetValue(itemId, out var list)
                ? list.ToArray()
                : [];
        }
    }

    public bool HasSource(uint itemId) =>
        sources.TryGetValue(itemId, out var list) && list.Count > 0;

    public bool IsChecked(uint itemId) =>
        itemId != 0 && checkedItemIds.ContainsKey(itemId);

    public bool IsCachedMiss(uint itemId) =>
        IsChecked(itemId) && !HasSource(itemId);

    /// <summary>
    /// True while the selected item's Garland item document is queued or running.
    /// Monster-document enrichment is intentionally tracked separately so a long
    /// list of optional creature metadata never makes the core item resolution
    /// appear blocked.
    /// </summary>
    public bool IsRequestInFlight(uint itemId) =>
        itemId != 0 && itemWork.IsPending(itemId);

    /// <summary>
    /// True while one or more dedicated mob documents are queued/running for this
    /// item. This is target-scoped even though mob requests themselves are shared
    /// globally by exact mob identity.
    /// </summary>
    public bool IsMonsterEnrichmentInFlight(uint itemId) =>
        itemId != 0 &&
        pendingMonsterIdsByItem.TryGetValue(itemId, out var mobs) &&
        !mobs.IsEmpty;

    /// <summary>
    /// True while the selected item's known monster metadata still belongs to
    /// the bounded monster-enrichment lifecycle, including transient retry
    /// backoff between requests. This is intentionally side-effect free and is
    /// used only to keep player-facing status stable while a retryable lookup is
    /// still in progress conceptually.
    /// </summary>
    public bool HasRetryableMonsterEnrichment(
        uint itemId,
        IEnumerable<SourceEntry>? candidates)
    {
        if (itemId == 0)
            return false;

        if (IsMonsterEnrichmentInFlight(itemId))
            return true;

        foreach (var source in candidates ?? Array.Empty<SourceEntry>())
        {
            if (!SourceClassification.IsMonster(source))
                continue;

            ulong mobId;
            if (!TryGetGarlandMobId(source.EnrichmentKey, out mobId))
            {
                if (source.MonsterBaseId is not uint fallbackBaseId || fallbackBaseId == 0 ||
                    source.MonsterNameId is not uint fallbackNameId || fallbackNameId == 0 ||
                    !TryComposeGarlandMobId(fallbackBaseId, fallbackNameId, out mobId))
                {
                    continue;
                }
            }

            if (IsMobValidatedForCurrentGameData(mobId))
                continue;

            var attempts = monsterAttemptCounts.TryGetValue(mobId, out var count) ? count : 0;
            if (attempts < MaxMonsterEnrichmentAttempts)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the remaining lifetime of the transient "no new data" notice
    /// after a successful revalidation produced the same cached source data.
    /// </summary>
    public bool TryGetNoChangesNotice(uint itemId, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (itemId == 0 || !noChangesNoticeUntilUtc.TryGetValue(itemId, out var untilUtc))
            return false;

        remaining = untilUtc - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
            return true;

        noChangesNoticeUntilUtc.TryRemove(itemId, out _);
        remaining = TimeSpan.Zero;
        return false;
    }

    public bool IsValidatedForCurrentGameData(uint itemId) =>
        itemId != 0 &&
        validatedGameDataFingerprints.TryGetValue(itemId, out var fingerprint) &&
        string.Equals(fingerprint, currentGameDataFingerprint, StringComparison.Ordinal);

    /// <summary>
    /// Returns a cached Garland mob location for the exact BNpcBase/BNpcName
    /// identity. Mob metadata is cached globally so every item that drops from
    /// the same creature can reuse the same location without another request.
    /// </summary>
    public bool TryGetCachedMobLocation(
        uint bnpcBaseId,
        uint bnpcNameId,
        out MonsterSpawnLocation location)
    {
        location = default!;
        if (!TryComposeGarlandMobId(bnpcBaseId, bnpcNameId, out var mobId))
            return false;

        if (!monsterCache.TryGetLocation(mobId, out var cachedLocation))
            return false;

        location = cachedLocation;
        return true;
    }

    /// <summary>
    /// Returns a cached Garland mob location addressed by Garland's own provider
    /// transaction key. Prefer this overload whenever a source carries an
    /// EnrichmentKey; normalized BNpc identity must not be used to reconstruct
    /// which provider document was originally referenced by the item document.
    /// </summary>
    public bool TryGetCachedMobLocation(
        string? enrichmentKey,
        out MonsterSpawnLocation location)
    {
        location = default!;
        if (!TryGetGarlandMobId(enrichmentKey, out var mobId) ||
            !monsterCache.TryGetLocation(mobId, out var cachedLocation))
        {
            return false;
        }

        location = cachedLocation;
        return true;
    }

    /// <summary>
    /// True after the dedicated Garland mob document has been checked against
    /// the current game-data fingerprint. A validated mob may legitimately have
    /// no overworld coordinate (for example a duty-only enemy); callers can use
    /// this to distinguish "not fetched yet" from "fetched, no location".
    /// </summary>
    public bool IsMobMetadataValidated(uint bnpcBaseId, uint bnpcNameId)
    {
        return TryComposeGarlandMobId(bnpcBaseId, bnpcNameId, out var mobId) &&
               IsMobValidatedForCurrentGameData(mobId);
    }

    /// <summary>
    /// Provider-key overload for current resolver paths. This deliberately keeps
    /// provider transaction identity independent from normalized game identity.
    /// </summary>
    public bool IsMobMetadataValidated(string? enrichmentKey)
    {
        return TryGetGarlandMobId(enrichmentKey, out var mobId) &&
               IsMobValidatedForCurrentGameData(mobId);
    }

    /// <summary>
    /// Requests Garland discovery for one actively-selected item. The item
    /// document is the discovery boundary: it identifies source families and
    /// exact mob identities, then returns. Dedicated monster documents are queued
    /// independently and never hold this request open.
    /// </summary>
    public void EnsureRequested(uint itemId, bool requireAdditionalEnrichment = false)
    {
        if (itemId == 0)
            return;

        if (!requireAdditionalEnrichment &&
            checkedItemIds.ContainsKey(itemId) &&
            IsValidatedForCurrentGameData(itemId))
        {
            networkRequests.CacheHit("Garland item");
            return;
        }

        if (retryAfterUtc.TryGetValue(itemId, out var retryAfter) && DateTime.UtcNow < retryAfter)
        {
            networkRequests.Suppressed("Garland item");
            return;
        }

        if (itemWork.TryQueue(itemId))
            networkRequests.CacheMiss("Garland item");
    }

    private async Task ProcessItemWorkAsync(uint itemId, CancellationToken cancellationToken)
    {
        if (retryAfterUtc.TryGetValue(itemId, out var retryAfter) && DateTime.UtcNow < retryAfter)
        {
            networkRequests.Suppressed("Garland item");
            return;
        }

        await ResolveOneAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveOneAsync(uint itemId, CancellationToken cancellationToken)
    {
        try
        {
            // Capture the pre-request state before LoadItemAsync starts publishing
            // incremental discoveries. Otherwise the final comparison would be
            // against our own live updates and every revalidation would appear
            // unchanged even when it actually improved the cached answer.
            var hadCachedResult = checkedItemIds.ContainsKey(itemId);
            var oldSources = GetSources(itemId);

            var resolved = await LoadItemAsync(itemId, cancellationToken);
            if (resolved is null)
            {
                retryAfterUtc[itemId] = DateTime.UtcNow.AddMinutes(5);
                return;
            }

            var changed = !SourceListsEquivalent(oldSources, resolved);

            var sourceGate = sourceMutationGates.GetOrAdd(itemId, _ => new object());
            lock (sourceGate)
            {
                if (resolved.Count > 0)
                    sources[itemId] = resolved;
                else
                    sources.TryRemove(itemId, out _);
            }

            checkedItemIds[itemId] = 0;
            validatedGameDataFingerprints[itemId] = currentGameDataFingerprint;
            retryAfterUtc.TryRemove(itemId, out _);
            cacheWrittenUtc = DateTime.UtcNow;

            // Only show the transient confirmation for an actual revalidation of
            // previously cached network data. First-time lookups simply populate
            // the cache and render the newly-resolved result.
            if (hadCachedResult && !changed)
                noChangesNoticeUntilUtc[itemId] = DateTime.UtcNow.Add(NoChangesNoticeDuration);
            else
                noChangesNoticeUntilUtc.TryRemove(itemId, out _);

            SaveCache();

            // Discovery is now committed. The selected-target family policy will
            // queue only monster rows that remain incomplete after local/static
            // enrichment, avoiding unnecessary mob requests for already-complete
            // monsters.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            retryAfterUtc[itemId] = DateTime.UtcNow.AddMinutes(5);
            log.Debug(ex, $"Garland lookup failed for item {itemId}.");
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

        var queue = force
            ? requested
            : requested.Where(id => !IsValidatedForCurrentGameData(id)).ToArray();

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
                        {
                            var sourceGate = sourceMutationGates.GetOrAdd(itemId, _ => new object());
                            lock (sourceGate)
                                sources[itemId] = resolved;
                        }

                        // Record only successful item-document lookups. Transient
                        // HTTP/schema failures must not become persisted negatives.
                        checkedItemIds[itemId] = 0;
                        validatedGameDataFingerprints[itemId] = currentGameDataFingerprint;
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
        var partialMobLevels = BuildPartialMobLevelMap(document.RootElement);
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

        // Monster drops are discovered from the item document, but enrichment is
        // deliberately NOT awaited here. The item request owns source discovery;
        // the independent monster pipeline owns dedicated mob documents. This is
        // the critical boundary that keeps an item with dozens of possible mobs
        // from taking dozens of seconds to "resolve".
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

            var embeddedLevel = LookupPartialMobLevel(partialMobLevels, id);
            if (embeddedLevel.HasValue)
                RememberMobLevel(id, embeddedLevel.Value);

            uint? bnpcNameId = null;
            uint? bnpcBaseId = null;
            if (TrySplitGarlandMobId(id, out var splitBaseId, out var splitNameId))
            {
                bnpcBaseId = splitBaseId;
                bnpcNameId = splitNameId;
            }

            var bestKnownLevel = monsterCache.TryGetLevel(id, out var cachedLevel)
                ? cachedLevel
                : embeddedLevel;

            IReadOnlyList<MonsterSpawnLocation> cachedLocations = Array.Empty<MonsterSpawnLocation>();
            if (monsterCache.TryGetLocation(id, out var cachedLocation))
                cachedLocations = new[] { cachedLocation };

            var source = new SourceEntry(
                "Monster",
                $"Monster: {name}",
                "Garland Tools mob-drop data",
                Priority: 58,
                MonsterLevel: bestKnownLevel,
                MonsterLocations: cachedLocations,
                MonsterNameId: bnpcNameId,
                MonsterBaseId: bnpcBaseId,
                // Preserve the provider document identity separately from game
                // identity. Normalization may replace/complete BNpcBase/BNpcName,
                // but the Garland mob endpoint must still be addressed by the raw
                // relationship ID emitted by the item document.
                EnrichmentKey: BuildGarlandMobEnrichmentKey(id),
                // FATE context is item-scoped; never import legacy/global FATE
                // guesses from the monster metadata cache.
                MonsterFateNames: Array.Empty<string>());

            AddUnique(result, source);

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

    /// <summary>
    /// Garland mob IDs are a stable composite of BNpcBase and BNpcName:
    /// base * 10,000,000,000 + name. Preserve both halves so local
    /// LuminaSupplemental MobSpawn data can be joined directly.
    /// </summary>
    private static bool TryComposeGarlandMobId(
        uint bnpcBaseId,
        uint bnpcNameId,
        out ulong mobId)
    {
        const ulong factor = 10_000_000_000UL;
        if (bnpcBaseId == 0 || bnpcNameId == 0)
        {
            mobId = 0;
            return false;
        }

        mobId = (ulong)bnpcBaseId * factor + bnpcNameId;
        return true;
    }

    private static bool TrySplitGarlandMobId(ulong mobId, out uint bnpcBaseId, out uint bnpcNameId)
    {
        const ulong factor = 10_000_000_000UL;
        var basePart = mobId / factor;
        var namePart = mobId % factor;

        if (basePart == 0 || basePart > uint.MaxValue || namePart == 0 || namePart > uint.MaxValue)
        {
            bnpcBaseId = 0;
            bnpcNameId = 0;
            return false;
        }

        bnpcBaseId = (uint)basePart;
        bnpcNameId = (uint)namePart;
        return true;
    }

    private const string GarlandMobEnrichmentPrefix = "garland:mob:";

    private static string BuildGarlandMobEnrichmentKey(ulong mobId) =>
        $"{GarlandMobEnrichmentPrefix}{mobId}";

    private static bool TryGetGarlandMobId(string? enrichmentKey, out ulong mobId)
    {
        mobId = 0;
        if (string.IsNullOrWhiteSpace(enrichmentKey) ||
            !enrichmentKey.StartsWith(GarlandMobEnrichmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ulong.TryParse(
                   enrichmentKey[GarlandMobEnrichmentPrefix.Length..],
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out mobId) &&
               mobId != 0;
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

    private void RememberMobLevel(ulong mobId, uint level)
    {
        if (mobId == 0 || level == 0)
            return;

        // An item-document mob partial can provide a useful level hint, but it
        // does not prove that the full monster acquisition answer is complete.
        // In particular, coords/zoneid live on the dedicated mob document.
        // Cache the hint without marking the mob metadata as validated so the
        // completion pipeline can still fetch the missing location exactly once.
        monsterCache.PutLevelHint(mobId, level);
    }

    private bool IsMobValidatedForCurrentGameData(ulong mobId) =>
        monsterCache.IsValidated(mobId, currentGameDataFingerprint);

    private sealed record GarlandMobMetadata(
        uint? Level,
        MonsterSpawnLocation? Location,
        IReadOnlyList<string> FateNames,
        bool Validated);

    /// <summary>
    /// Re-queues only missing monster metadata already discovered for an item.
    /// This is deliberately separate from EnsureRequested(): retrying a mob must
    /// never re-download the parent item document just to rediscover the same mob
    /// identities.
    /// </summary>
    public bool EnsureMonsterEnrichment(
        uint itemId,
        IEnumerable<SourceEntry>? candidates = null)
    {
        if (itemId == 0)
            return false;

        // The source-family policy may supply the post-merge incomplete monster
        // view. This prevents the provider from fetching metadata for monsters
        // already complete from local/static data. Falling back to Garland's own
        // rows keeps the method useful for diagnostics/internal callers.
        var workCandidates = (candidates ?? GetSources(itemId)
                .Where(SourceClassification.IsMonster))
            .Where(SourceClassification.IsMonster)
            .OrderBy(source => source.MonsterLevel ?? uint.MaxValue)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ownsMissingMetadata = false;
        foreach (var source in workCandidates)
        {
            // The provider lookup identity and the game's BNpc identity are related
            // facts, but they are not interchangeable. The item document gives CQG
            // the exact Garland mob-document key. Resolver normalization can later
            // fill or refine BNpcBase/BNpcName from local data; recomposing a network
            // key from that normalized identity can address a different/nonexistent
            // Garland document and was the v1.3.4.19 regression.
            ulong mobId;
            if (!TryGetGarlandMobId(source.EnrichmentKey, out mobId))
            {
                if (source.MonsterBaseId is not uint fallbackBaseId || fallbackBaseId == 0 ||
                    source.MonsterNameId is not uint fallbackNameId || fallbackNameId == 0 ||
                    !TryComposeGarlandMobId(fallbackBaseId, fallbackNameId, out mobId))
                {
                    continue;
                }
            }

            if (IsMobValidatedForCurrentGameData(mobId))
                continue;

            // Once the provider document identity is known, the monster pipeline
            // owns the remaining work even while that mob is in transient backoff.
            // The parent item document cannot improve an already-known provider key.
            ownsMissingMetadata = true;
            if ((monsterAttemptCounts.TryGetValue(mobId, out var attempts) &&
                 attempts >= MaxMonsterEnrichmentAttempts) ||
                (monsterRetryAfterUtc.TryGetValue(mobId, out var retryAfter) &&
                 DateTime.UtcNow < retryAfter))
            {
                continue;
            }

            QueueMonsterEnrichment(itemId, mobId);
        }

        return ownsMissingMetadata || IsMonsterEnrichmentInFlight(itemId);
    }

    private bool QueueMonsterEnrichment(uint itemId, ulong mobId)
    {
        if (itemId == 0 || mobId == 0)
            return false;

        // Cache-first is the entire fast path. The renderer/resolver reads the
        // global monster cache directly, so no per-item copy is necessary.
        if (IsMobValidatedForCurrentGameData(mobId))
            return true;

        if (monsterAttemptCounts.TryGetValue(mobId, out var attempts) &&
            attempts >= MaxMonsterEnrichmentAttempts)
            return false;

        if (monsterRetryAfterUtc.TryGetValue(mobId, out var retryAfter) &&
            DateTime.UtcNow < retryAfter)
            return false;

        var subscribers = monsterSubscribers.GetOrAdd(
            mobId,
            _ => new ConcurrentDictionary<uint, byte>());
        subscribers[itemId] = 0;

        var itemPending = pendingMonsterIdsByItem.GetOrAdd(
            itemId,
            _ => new ConcurrentDictionary<ulong, byte>());
        itemPending[mobId] = 0;

        if (monsterWork.TryQueue(mobId) || monsterWork.IsPending(mobId))
            return true;

        subscribers.TryRemove(itemId, out _);
        itemPending.TryRemove(mobId, out _);
        if (itemPending.IsEmpty)
            pendingMonsterIdsByItem.TryRemove(itemId, out _);
        return false;
    }

    private async Task ProcessMonsterWorkAsync(ulong mobId, CancellationToken cancellationToken)
    {
        GarlandMobMetadata metadata;
        try
        {
            if (monsterRetryAfterUtc.TryGetValue(mobId, out var retryAfter) &&
                DateTime.UtcNow < retryAfter)
            {
                metadata = new GarlandMobMetadata(
                    monsterCache.TryGetLevel(mobId, out var backoffLevel) ? backoffLevel : null,
                    monsterCache.TryGetLocation(mobId, out var backoffLocation) ? backoffLocation : null,
                    Array.Empty<string>(),
                    Validated: false);
            }
            else
            {
                metadata = await FetchMobMetadataAsync(mobId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Debug(ex, $"Unexpected Garland monster enrichment failure for mob {mobId}.");
            metadata = new GarlandMobMetadata(
                monsterCache.TryGetLevel(mobId, out var level) ? level : null,
                monsterCache.TryGetLocation(mobId, out var location) ? location : null,
                Array.Empty<string>(),
                Validated: false);
        }

        // The successful fetch has already been committed to the global monster
        // cache before we reach this point. Clear target-scoped pending state;
        // the normal resolver reads that cache on the next UI frame and the newly
        // available level/location appears without copying it into an item record.
        if (monsterSubscribers.TryRemove(mobId, out var subscribers))
        {
            foreach (var itemId in subscribers.Keys)
                RemovePendingMonster(itemId, mobId);
        }

        if (metadata.Validated)
            monsterRetryAfterUtc.TryRemove(mobId, out _);
    }

    private void RemovePendingMonster(uint itemId, ulong mobId)
    {
        if (!pendingMonsterIdsByItem.TryGetValue(itemId, out var pending))
            return;

        pending.TryRemove(mobId, out _);
        if (pending.IsEmpty)
            pendingMonsterIdsByItem.TryRemove(itemId, out _);
    }

    /// <summary>
    /// Fetches one dedicated Garland mob document. Scheduling, de-duplication,
    /// and request pacing are owned by monsterWork; this method owns only the
    /// provider transaction and cache mutation.
    /// </summary>
    private async Task<GarlandMobMetadata> FetchMobMetadataAsync(
        ulong mobId,
        CancellationToken cancellationToken)
    {
        if (mobId == 0)
            return new GarlandMobMetadata(null, null, Array.Empty<string>(), Validated: false);

        if (IsMobValidatedForCurrentGameData(mobId))
        {
            monsterCache.TryGet(mobId, out var current);
            return new GarlandMobMetadata(
                current?.Level,
                current?.Location,
                Array.Empty<string>(),
                Validated: true);
        }

        var staleLevel = monsterCache.TryGetLevel(mobId, out var existingLevel)
            ? existingLevel
            : (uint?)null;
        var staleLocation = monsterCache.TryGetLocation(mobId, out var existingLocation)
            ? existingLocation
            : null;

        try
        {
            var attempt = monsterAttemptCounts.AddOrUpdate(mobId, 1, (_, current) => current + 1);
            var url = $"https://www.garlandtools.org/db/doc/mob/en/3/{mobId}.json";

            networkRequests.Request("Garland mob");
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                networkRequests.Failure("Garland mob");
                monsterRetryAfterUtc[mobId] = (int)response.StatusCode == 429
                    ? DateTime.UtcNow.AddMinutes(30)
                    : DateTime.UtcNow.AddSeconds(attempt <= 1 ? 4 : 10);
                return new GarlandMobMetadata(staleLevel, staleLocation, Array.Empty<string>(), Validated: false);
            }

            networkRequests.Success("Garland mob");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = document.RootElement;
            var mob = root.TryGetProperty("mob", out var mobElement) &&
                      mobElement.ValueKind == JsonValueKind.Object
                ? mobElement
                : root;

            var level = FindLowestMobLevel(mob);
            var location = TryReadGarlandMobLocation(mob);

            // Do not infer FATE membership from Garland mob-document partials.
            // The bounded debug probe demonstrated that this is not a reliable
            // relationship source. FATE context is now derived conservatively
            // from item-scoped LuminaSupplemental FateItem + MobDrop evidence.
            var fateNames = Array.Empty<string>();

            // Commit this monster before the worker moves to the next one. The
            // cache is global by monster identity, so every future item gets an
            // immediate cache hit for this same creature.
            monsterCache.PutValidated(
                mobId,
                level ?? staleLevel,
                location,
                fateNames,
                currentGameDataFingerprint);

            // A successful document check validates the provider response even
            // when it contains no overworld coordinate (duty-only mobs are valid).
            monsterRetryAfterUtc.TryRemove(mobId, out _);
            monsterAttemptCounts.TryRemove(mobId, out _);

            return new GarlandMobMetadata(
                level,
                location,
                fateNames,
                Validated: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            networkRequests.Failure("Garland mob");
            var attempts = monsterAttemptCounts.TryGetValue(mobId, out var currentAttempt)
                ? currentAttempt
                : 1;
            monsterRetryAfterUtc[mobId] = DateTime.UtcNow.AddSeconds(attempts <= 1 ? 4 : 10);
            log.Debug(ex, $"Garland mob metadata lookup failed for mob {mobId}.");
            return new GarlandMobMetadata(staleLevel, staleLocation, Array.Empty<string>(), Validated: false);
        }
    }

    private MonsterSpawnLocation? TryReadGarlandMobLocation(JsonElement mob)
    {
        if (!TryReadUInt32(mob, "zoneid", out var placeNameId) || placeNameId == 0 ||
            !mob.TryGetProperty("coords", out var coords) ||
            coords.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = coords.EnumerateArray().Take(2).ToArray();
        if (values.Length < 2 ||
            !TryReadFloat(values[0], out var x) ||
            !TryReadFloat(values[1], out var y))
        {
            return null;
        }

        try
        {
            var place = data.GetExcelSheet<PlaceName>().GetRow(placeNameId);
            var zoneName = place.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(zoneName))
                return null;

            uint territoryTypeId = 0;
            foreach (var territory in data.GetExcelSheet<TerritoryType>())
            {
                if (territory.RowId == 0)
                    continue;

                if (ReflectionUtil.GetNestedRowId(territory, "PlaceName") == placeNameId)
                {
                    territoryTypeId = territory.RowId;
                    break;
                }
            }

            return new MonsterSpawnLocation(territoryTypeId, zoneName, x, y, IsFallback: true);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadFloat(JsonElement value, out float result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out result))
            return true;

        if (value.ValueKind == JsonValueKind.String &&
            float.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    private static Dictionary<ulong, uint> BuildPartialMobLevelMap(JsonElement root)
    {
        var result = new Dictionary<ulong, uint>();

        if (!root.TryGetProperty("partials", out var partials) ||
            partials.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var partial in partials.EnumerateArray())
        {
            var type = ReadString(partial, "type")?.Trim();
            if (string.IsNullOrWhiteSpace(type) ||
                !(type.Equals("mob", StringComparison.OrdinalIgnoreCase) ||
                  type.Equals("bnpc", StringComparison.OrdinalIgnoreCase) ||
                  type.Equals("battleNpc", StringComparison.OrdinalIgnoreCase) ||
                  type.Equals("battlenpc", StringComparison.OrdinalIgnoreCase) ||
                  type.Equals("npc", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!TryReadUInt64(partial, "id", out var id) || id == 0 ||
                !partial.TryGetProperty("obj", out var obj) ||
                obj.ValueKind != JsonValueKind.Object)
                continue;

            // Garland's compact documents have used several mob-level shapes
            // over time. Resolve the lowest explicit level from the mob object
            // (including nested location/range data) and fall back to the outer
            // partial if necessary. Do not inspect arbitrary numbers: the helper
            // only accepts fields whose names explicitly mean level.
            var level = FindLowestMobLevel(obj) ?? FindLowestMobLevel(partial);
            if (level.HasValue)
                result[id] = level.Value;
        }

        return result;
    }

    private static uint? FindLowestMobLevel(JsonElement element)
    {
        uint? lowest = null;

        void Consider(uint value)
        {
            if (value == 0 || value > 999)
                return;

            if (!lowest.HasValue || value < lowest.Value)
                lowest = value;
        }

        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in node.EnumerateObject())
                {
                    var key = new string(property.Name
                        .Where(char.IsLetterOrDigit)
                        .Select(char.ToLowerInvariant)
                        .ToArray());

                    var isLevelField = key is "l" or "lvl" or "level" or
                        "minlevel" or "levelmin" or "minimumlevel" or
                        "levels" or "levelrange";

                    if (isLevelField)
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number &&
                            property.Value.TryGetUInt32(out var numeric))
                        {
                            Consider(numeric);
                        }
                        else if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var text = property.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                var digits = string.Empty;
                                foreach (var c in text)
                                {
                                    if (char.IsDigit(c))
                                    {
                                        digits += c;
                                    }
                                    else if (digits.Length > 0)
                                    {
                                        if (uint.TryParse(digits, out var parsed))
                                            Consider(parsed);
                                        digits = string.Empty;
                                    }
                                }

                                if (digits.Length > 0 && uint.TryParse(digits, out var trailingParsed))
                                    Consider(trailingParsed);
                            }
                        }
                    }

                    // Level information may live inside Garland location/spawn
                    // objects, so recurse after checking the property's own key.
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Walk(property.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in node.EnumerateArray())
                    Walk(child);
            }
        }

        Walk(element);
        return lowest;
    }

    private static uint? LookupPartialMobLevel(
        IReadOnlyDictionary<ulong, uint> levels,
        ulong id) =>
        levels.TryGetValue(id, out var level) && level != 0 ? level : null;

    private static bool TryReadUInt64(JsonElement element, string property, out ulong value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var token))
            return false;

        return token.ValueKind switch
        {
            JsonValueKind.Number => token.TryGetUInt64(out value),
            JsonValueKind.String => ulong.TryParse(token.GetString(), out value),
            _ => false,
        };
    }

    private static bool TryReadUInt32(JsonElement element, string property, out uint value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var token))
            return false;

        return token.ValueKind switch
        {
            JsonValueKind.Number => token.TryGetUInt32(out value),
            JsonValueKind.String => uint.TryParse(token.GetString(), out value),
            _ => false,
        };
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
        var index = FindMatchingSourceIndex(list, source);
        if (index < 0)
        {
            list.Add(source);
            return;
        }

        list[index] = MergeSourceEntry(list[index], source);
    }

    private static int FindMatchingSourceIndex(IReadOnlyList<SourceEntry> list, SourceEntry source)
    {
        var sourceIsMonster = string.Equals(source.Kind, "Monster", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < list.Count; i++)
        {
            var existing = list[i];
            if (!string.Equals(existing.Kind, source.Kind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (sourceIsMonster)
            {
                // Provider/document identity is the strongest de-duplication key.
                // If two Garland rows carry different raw mob keys they are distinct
                // monsters even when their localized display names are identical.
                var existingHasProviderKey = !string.IsNullOrWhiteSpace(existing.EnrichmentKey);
                var sourceHasProviderKey = !string.IsNullOrWhiteSpace(source.EnrichmentKey);
                if (existingHasProviderKey && sourceHasProviderKey)
                {
                    if (string.Equals(
                            existing.EnrichmentKey,
                            source.EnrichmentKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }

                    continue;
                }

                // Stable game identity is the next-best key. When both identities
                // are known and differ, never collapse the rows by display name.
                var existingHasGameIdentity =
                    existing.MonsterBaseId.HasValue && existing.MonsterBaseId.Value != 0 &&
                    existing.MonsterNameId.HasValue && existing.MonsterNameId.Value != 0;
                var sourceHasGameIdentity =
                    source.MonsterBaseId.HasValue && source.MonsterBaseId.Value != 0 &&
                    source.MonsterNameId.HasValue && source.MonsterNameId.Value != 0;

                if (existingHasGameIdentity && sourceHasGameIdentity)
                {
                    if (existing.MonsterBaseId == source.MonsterBaseId &&
                        existing.MonsterNameId == source.MonsterNameId)
                    {
                        return i;
                    }

                    continue;
                }
            }

            if (string.Equals(existing.Name, source.Name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static SourceEntry MergeSourceEntry(SourceEntry existing, SourceEntry incoming)
    {
        if (!string.Equals(existing.Kind, "Monster", StringComparison.OrdinalIgnoreCase))
        {
            // For non-monster sources the existing de-duplication semantics are
            // intentionally retained, but prefer newly supplied optional detail.
            return existing with
            {
                Detail = string.IsNullOrWhiteSpace(incoming.Detail) ? existing.Detail : incoming.Detail,
                IsUnlocked = incoming.IsUnlocked ?? existing.IsUnlocked,
                SpecificityHint = string.IsNullOrWhiteSpace(incoming.SpecificityHint)
                    ? existing.SpecificityHint
                    : incoming.SpecificityHint,
                NpcId = incoming.NpcId ?? existing.NpcId,
                EnrichmentKey = string.IsNullOrWhiteSpace(incoming.EnrichmentKey)
                    ? existing.EnrichmentKey
                    : incoming.EnrichmentKey,
                Priority = Math.Min(existing.Priority, incoming.Priority),
            };
        }

        var mergedLocations = MergeMonsterLocations(existing.MonsterLocations, incoming.MonsterLocations);

        return existing with
        {
            Name = string.IsNullOrWhiteSpace(incoming.Name) ? existing.Name : incoming.Name,
            Detail = string.IsNullOrWhiteSpace(incoming.Detail) ? existing.Detail : incoming.Detail,
            Priority = Math.Min(existing.Priority, incoming.Priority),
            SpecificityHint = string.IsNullOrWhiteSpace(incoming.SpecificityHint)
                ? existing.SpecificityHint
                : incoming.SpecificityHint,
            // Incoming non-null monster metadata represents the newest
            // enrichment and is allowed to improve/replace earlier hints.
            MonsterLevel = incoming.MonsterLevel ?? existing.MonsterLevel,
            MonsterZoneLevel = incoming.MonsterZoneLevel ?? existing.MonsterZoneLevel,
            MonsterNameId = incoming.MonsterNameId ?? existing.MonsterNameId,
            MonsterBaseId = incoming.MonsterBaseId ?? existing.MonsterBaseId,
            EnrichmentKey = string.IsNullOrWhiteSpace(incoming.EnrichmentKey)
                ? existing.EnrichmentKey
                : incoming.EnrichmentKey,
            MonsterLocations = mergedLocations,
        };
    }

    private static IReadOnlyList<MonsterSpawnLocation> MergeMonsterLocations(
        IReadOnlyList<MonsterSpawnLocation>? existing,
        IReadOnlyList<MonsterSpawnLocation>? incoming)
    {
        var merged = existing?.ToList() ?? new List<MonsterSpawnLocation>();
        if (incoming is null || incoming.Count == 0)
            return merged;

        foreach (var candidate in incoming)
        {
            // A fallback is only useful when the creature has no concrete spawn
            // information yet. Never let lower-quality fallback data clutter or
            // overwrite an already-resolved location list.
            if (candidate.IsFallback && merged.Count > 0)
                continue;

            // The first concrete coordinate supersedes fallback-only data.
            if (!candidate.IsFallback)
                merged.RemoveAll(location => location.IsFallback);

            var duplicate = merged.Any(location => SameMonsterCoordinate(location, candidate));
            if (!duplicate)
                merged.Add(candidate);
        }

        return merged;
    }

    private static bool SameMonsterCoordinate(MonsterSpawnLocation left, MonsterSpawnLocation right)
    {
        var sameZone = left.TerritoryTypeId != 0 && right.TerritoryTypeId != 0
            ? left.TerritoryTypeId == right.TerritoryTypeId
            : string.Equals(left.ZoneName, right.ZoneName, StringComparison.OrdinalIgnoreCase);

        if (!sameZone)
            return false;

        // CQG presents map coordinates to one decimal place. Compare at that
        // same precision so provider floating-point noise cannot create visually
        // duplicate spawn rows.
        return Math.Round(left.X, 1) == Math.Round(right.X, 1) &&
               Math.Round(left.Y, 1) == Math.Round(right.Y, 1);
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
                parsedSchemaVersion is < 6 or > CacheSchemaVersion)
            {
                // Unsupported cache structures are ignored. Older supported
                // schemas are migration sources: their cached data stays visible
                // immediately, while selected entries revalidate once under the
                // current network/cache policy.
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

            // Older supported cache data remains immediately usable, but schema 11
            // is the first version whose validation state guarantees that mob
            // location metadata was checked as part of the dedicated mob document.
            // Preserve cached values while forcing one selected-target revalidation.
            if (parsedSchemaVersion >= 11 &&
                document.RootElement.TryGetProperty("validatedGameDataFingerprints", out var validations) &&
                validations.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in validations.EnumerateObject())
                {
                    if (uint.TryParse(property.Name, out var itemId) &&
                        itemId != 0 &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() is { Length: > 0 } fingerprint)
                    {
                        validatedGameDataFingerprints[itemId] = fingerprint;
                    }
                }
            }

            // Monster metadata moved to its own global cache in schema 14.
            // Import legacy combined-cache values once so upgrades keep useful
            // monster knowledge, but all new writes go only to monster-cache.json.
            if (parsedSchemaVersion >= 10)
            {
                var legacyChecked = new HashSet<ulong>();
                var legacyLevels = new Dictionary<ulong, uint>();
                var legacyLocations = new Dictionary<ulong, MonsterSpawnLocation>();
                var legacyFingerprints = new Dictionary<ulong, string>();

                if (document.RootElement.TryGetProperty("checkedMobs", out var checkedMobs) &&
                    checkedMobs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in checkedMobs.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.Number &&
                            value.TryGetUInt64(out var mobId) && mobId != 0)
                            legacyChecked.Add(mobId);
                    }
                }

                if (document.RootElement.TryGetProperty("mobLevels", out var mobLevels) &&
                    mobLevels.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in mobLevels.EnumerateObject())
                    {
                        if (ulong.TryParse(property.Name, out var mobId) && mobId != 0 &&
                            property.Value.ValueKind == JsonValueKind.Number &&
                            property.Value.TryGetUInt32(out var level) && level != 0)
                            legacyLevels[mobId] = level;
                    }
                }

                if (document.RootElement.TryGetProperty("mobLocations", out var mobLocations) &&
                    mobLocations.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in mobLocations.EnumerateObject())
                    {
                        if (!ulong.TryParse(property.Name, out var mobId) || mobId == 0 ||
                            property.Value.ValueKind != JsonValueKind.Object)
                            continue;

                        var zoneName = ReadString(property.Value, "zoneName");
                        if (string.IsNullOrWhiteSpace(zoneName) ||
                            !TryReadUInt32(property.Value, "territoryTypeId", out var territoryTypeId) ||
                            !property.Value.TryGetProperty("x", out var xElement) ||
                            !property.Value.TryGetProperty("y", out var yElement) ||
                            !TryReadFloat(xElement, out var x) ||
                            !TryReadFloat(yElement, out var y))
                            continue;

                        legacyLocations[mobId] = new MonsterSpawnLocation(
                            territoryTypeId, zoneName, x, y, IsFallback: true);
                    }
                }

                if (parsedSchemaVersion >= 13 &&
                    document.RootElement.TryGetProperty("validatedMobGameDataFingerprints", out var mobValidations) &&
                    mobValidations.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in mobValidations.EnumerateObject())
                    {
                        if (ulong.TryParse(property.Name, out var mobId) && mobId != 0 &&
                            property.Value.ValueKind == JsonValueKind.String &&
                            property.Value.GetString() is { Length: > 0 } fingerprint)
                            legacyFingerprints[mobId] = fingerprint;
                    }
                }

                var legacyIds = legacyChecked
                    .Concat(legacyLevels.Keys)
                    .Concat(legacyLocations.Keys)
                    .Concat(legacyFingerprints.Keys)
                    .Distinct()
                    .ToArray();

                foreach (var mobId in legacyIds)
                {
                    monsterCache.ImportLegacy(
                        mobId,
                        legacyLevels.TryGetValue(mobId, out var level) ? level : null,
                        legacyLocations.TryGetValue(mobId, out var location) ? location : null,
                        legacyChecked.Contains(mobId),
                        legacyFingerprints.TryGetValue(mobId, out var fingerprint) ? fingerprint : null);
                }

                if (legacyIds.Length > 0)
                    monsterCache.SaveImportedLegacyData();
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
                    uint? monsterLevel = TryReadUInt32(source, "monsterLevel", out var cachedMonsterLevel)
                        ? cachedMonsterLevel
                        : null;
                    uint? monsterZoneLevel = TryReadUInt32(source, "monsterZoneLevel", out var cachedMonsterZoneLevel)
                        ? cachedMonsterZoneLevel
                        : null;
                    uint? monsterNameId = TryReadUInt32(source, "monsterNameId", out var cachedMonsterNameId)
                        ? cachedMonsterNameId
                        : null;
                    uint? monsterBaseId = TryReadUInt32(source, "monsterBaseId", out var cachedMonsterBaseId)
                        ? cachedMonsterBaseId
                        : null;
                    var enrichmentKey = ReadString(source, "enrichmentKey");

                    if (string.IsNullOrWhiteSpace(kind) ||
                        string.IsNullOrWhiteSpace(name))
                        continue;

                    list.Add(new SourceEntry(
                        kind,
                        name,
                        detail,
                        Priority: 60,
                        MonsterLevel: monsterLevel,
                        MonsterZoneLevel: monsterZoneLevel,
                        MonsterNameId: monsterNameId,
                        MonsterBaseId: monsterBaseId,
                        EnrichmentKey: enrichmentKey));
                }

                if (list.Count > 0)
                {
                    sources[itemId] = list;
                    // Backward compatibility with pre-v0.36 caches that did not
                    // persist an explicit checked-ID list.
                    checkedItemIds[itemId] = 0;

                    // Schema 13 is the first cache that preserves the provider's
                    // raw monster-document key. Older monster rows stay visible as
                    // stale cache data, but the selected item must be rediscovered
                    // once so enrichment cannot reconstruct a network key from a
                    // normalized BNpc identity. Non-monster cached items keep their
                    // existing validation state.
                    if (parsedSchemaVersion < 13 &&
                        list.Any(SourceClassification.IsMonster))
                    {
                        validatedGameDataFingerprints.TryRemove(itemId, out _);
                    }
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
        lock (cacheWriteLock)
        {
            SaveCacheCore();
        }
    }

    private void SaveCacheCore()
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
                    monsterLevel = source.MonsterLevel,
                    monsterZoneLevel = source.MonsterZoneLevel,
                    monsterNameId = source.MonsterNameId,
                    monsterBaseId = source.MonsterBaseId,
                    enrichmentKey = source.EnrichmentKey,
                }).ToArray());

            var payload = new
            {
                schemaVersion = CacheSchemaVersion,
                writtenUtc = cacheWrittenUtc.ToString("O"),
                @checked = checkedItemIds.Keys.OrderBy(id => id).ToArray(),
                validatedGameDataFingerprints = validatedGameDataFingerprints
                    .Where(pair => checkedItemIds.ContainsKey(pair.Key))
                    .OrderBy(pair => pair.Key)
                    .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
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

    private static bool SourceListsEquivalent(
        IReadOnlyList<SourceEntry> left,
        IReadOnlyList<SourceEntry> right)
    {
        if (left.Count != right.Count)
            return false;

        static string Key(SourceEntry source)
        {
            var locations = source.MonsterLocations is { Count: > 0 }
                ? string.Join(
                    "|",
                    source.MonsterLocations
                        .OrderBy(location => location.TerritoryTypeId)
                        .ThenBy(location => location.X)
                        .ThenBy(location => location.Y)
                        .Select(location =>
                            $"{location.TerritoryTypeId}:{location.ZoneName}:{location.X:0.0}:{location.Y:0.0}:{location.ZoneLevel}:{location.IsFallback}"))
                : string.Empty;

            return string.Join("\u001f",
                source.Kind ?? string.Empty,
                source.Name ?? string.Empty,
                source.Detail ?? string.Empty,
                source.MonsterLevel?.ToString() ?? string.Empty,
                source.MonsterZoneLevel?.ToString() ?? string.Empty,
                source.MonsterNameId?.ToString() ?? string.Empty,
                source.MonsterBaseId?.ToString() ?? string.Empty,
                source.EnrichmentKey ?? string.Empty,
                locations);
        }

        var leftKeys = left.Select(Key).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var rightKeys = right.Select(Key).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return leftKeys.SequenceEqual(rightKeys, StringComparer.Ordinal);
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    /// <summary>
    /// Discards only the selected item's cached Garland source record and the
    /// dedicated mob metadata reached through that record. This is the manual
    /// repair path for a target whose cached enrichment is stale or incomplete.
    /// Unrelated item/mob caches are intentionally preserved.
    /// </summary>
    public bool RebuildTargetCache(uint itemId, IEnumerable<SourceEntry>? resolvedSources = null)
    {
        if (itemId == 0 || IsLoading || IsRequestInFlight(itemId) || IsMonsterEnrichmentInFlight(itemId))
            return false;

        var relatedMobIds = new HashSet<ulong>();

        void RememberRelatedMobs(IEnumerable<SourceEntry> candidateSources)
        {
            foreach (var source in candidateSources)
            {
                if (TryGetGarlandMobId(source.EnrichmentKey, out var providerMobId))
                {
                    relatedMobIds.Add(providerMobId);
                    continue;
                }

                // Backward compatibility for cache entries written before the
                // provider key was persisted separately.
                if (source.MonsterBaseId is not uint baseId || baseId == 0 ||
                    source.MonsterNameId is not uint nameId || nameId == 0)
                {
                    continue;
                }

                if (TryComposeGarlandMobId(baseId, nameId, out var composedMobId))
                    relatedMobIds.Add(composedMobId);
            }
        }

        // Use both the raw Garland item cache and the resolver's final merged
        // monster rows. The latter makes target repair reliable even when the
        // exact mob identity came from a different provider during normalization.
        var existingSources = GetSources(itemId);
        if (existingSources.Count > 0)
            RememberRelatedMobs(existingSources);

        if (resolvedSources is not null)
            RememberRelatedMobs(resolvedSources);

        lock (cacheWriteLock)
        {
            // Remove only this item's network answer/validation. The local resolver
            // has no persistent per-item result cache, so its source-family pipeline
            // will rebuild naturally on the next Resolve().
            var sourceGate = sourceMutationGates.GetOrAdd(itemId, _ => new object());
            lock (sourceGate)
                sources.TryRemove(itemId, out _);
            checkedItemIds.TryRemove(itemId, out _);
            validatedGameDataFingerprints.TryRemove(itemId, out _);
            retryAfterUtc.TryRemove(itemId, out _);
            noChangesNoticeUntilUtc.TryRemove(itemId, out _);

            // Monster metadata is a global entity cache and is deliberately not
            // invalidated by rebuilding one item. Forget only transient retry state;
            // a separate broad cache clear is the explicit way to discard global
            // monster knowledge.
            foreach (var mobId in relatedMobIds)
            {
                monsterRetryAfterUtc.TryRemove(mobId, out _);
                monsterAttemptCounts.TryRemove(mobId, out _);
            }

            cacheWrittenUtc = DateTime.MinValue;
            SaveCacheCore();
        }
        return true;
    }

    public bool ClearCache()
    {
        if (IsLoading || itemWork.PendingCount > 0 || monsterWork.PendingCount > 0)
            return false;

        lock (cacheWriteLock)
        {
            sources.Clear();
            sourceMutationGates.Clear();
            checkedItemIds.Clear();
            validatedGameDataFingerprints.Clear();
            monsterCache.Clear();
            monsterRetryAfterUtc.Clear();
            monsterAttemptCounts.Clear();
            monsterSubscribers.Clear();
            pendingMonsterIdsByItem.Clear();
            noChangesNoticeUntilUtc.Clear();
            retryAfterUtc.Clear();
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
        }

        return true;
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        itemWork.Dispose();
        monsterWork.Dispose();
        http.Dispose();
        disposeCts.Dispose();
    }
}
