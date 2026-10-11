using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace CustomQuestGuide;

/// <summary>
/// Community-source supplement for collectible items.
///
/// FFXIV Collect exposes public JSON data for mounts, minions, orchestrion
/// rolls, Triple Triad cards, and outfits.  Those systems contain many sources
/// that are not represented completely in the retail Excel sheets.
///
/// This index loads asynchronously so plugin startup is never blocked.  It is
/// only a fallback: live-game and LuminaSupplemental sources remain higher
/// priority.
/// </summary>
public sealed class CollectibleSourceIndex : IDisposable
{
    private static readonly string[] Endpoints =
    [
        "mounts",
        "minions",
        "orchestrions",
        "cards",
        "outfits",
        "emotes",
        "bardings",
        "hairstyles",
        "fashions",
        "facewear",
        "frames",
        // FFXIV Collect routes the Armoire collection under the plural API path.
        // The old singular path silently left pre-order/event/legacy armoire gear unresolved.
        "armoires",
    ];

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly IPluginLog log;
    private readonly NetworkRequestTracker networkRequests;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource disposeCts = new();

    private readonly ConcurrentDictionary<uint, List<SourceEntry>> sources = new();

    // Some collection models expose a record name but no inventory item_id.
    // Build aliases from the live CQG target catalog so those records can still
    // resolve without maintaining an item-ID table.
    private readonly Dictionary<string, List<uint>> targetIdsByAlias =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoading { get; private set; }
    public bool IsLoaded { get; private set; }
    public string Status { get; private set; } = "Not loaded";
    public int ResolvedItemCount => sources.Count;

    public CollectibleSourceIndex(IPluginLog log, NetworkRequestTracker networkRequests)
    {
        this.log = log;
        this.networkRequests = networkRequests;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CustomQuestGuide/1.1.13");
    }

    public void BindTargets(IEnumerable<TargetEntry> targets)
    {
        targetIdsByAlias.Clear();

        foreach (var target in targets)
        {
            AddAlias(target.Name, target.ItemId);

            if (target.Name.EndsWith(
                    " Framer's Kit",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(
                    target.Name[..^" Framer's Kit".Length],
                    target.ItemId);
            }

            const string facePrefix = "The Faces We Wear - ";
            if (target.Name.StartsWith(
                    facePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(target.Name[facePrefix.Length..], target.ItemId);
            }

            const string hairPrefix = "Modern Aesthetics - ";
            if (target.Name.StartsWith(
                    hairPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(target.Name[hairPrefix.Length..], target.ItemId);
            }

            if (target.Name.EndsWith(
                    " Attire Coffer",
                    StringComparison.OrdinalIgnoreCase))
            {
                var withoutCoffer = target.Name[..^" Coffer".Length];
                AddAlias(withoutCoffer, target.ItemId);

                if (withoutCoffer.EndsWith(
                        " Attire",
                        StringComparison.OrdinalIgnoreCase))
                {
                    AddAlias(
                        withoutCoffer[..^" Attire".Length],
                        target.ItemId);
                }
            }

            if (target.Name.EndsWith(
                    " Armor Coffer",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddAlias(target.Name[..^" Coffer".Length], target.ItemId);
            }
        }
    }

    private void AddAlias(string alias, uint itemId)
    {
        var normalized = NormalizeAlias(alias);

        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!targetIdsByAlias.TryGetValue(normalized, out var ids))
        {
            ids = [];
            targetIdsByAlias[normalized] = ids;
        }

        if (!ids.Contains(itemId))
            ids.Add(itemId);
    }

    private static string NormalizeAlias(string value) =>
        new(
            value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

    public IReadOnlyList<SourceEntry> GetSources(uint itemId)
    {
        if (!sources.TryGetValue(itemId, out var list))
            return [];

        lock (list)
            return list.ToArray();
    }

    public bool HasSource(uint itemId) =>
        sources.TryGetValue(itemId, out var list) && list.Count > 0;

    public async Task RefreshAsync()
    {
        if (!await refreshGate.WaitAsync(0))
            return;

        IsLoading = true;
        Status = "Loading community collectible sources...";

        try
        {
            // Seed from the last good in-memory snapshot. A partial outage must
            // reduce enrichment, not erase data we already had this session.
            var fresh = new ConcurrentDictionary<uint, List<SourceEntry>>();
            foreach (var pair in sources)
                fresh[pair.Key] = pair.Value.ToList();

            var succeeded = 0;
            var failed = 0;

            foreach (var endpoint in Endpoints)
            {
                try
                {
                    if (await LoadEndpointAsync(endpoint, fresh, disposeCts.Token))
                        succeeded++;
                    else
                        failed++;
                }
                catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.Debug(ex, $"CQG FFXIV Collect endpoint failed: {endpoint}.");
                }
            }

            if (succeeded == 0)
            {
                Status = IsLoaded
                    ? $"Using previous community data ({sources.Count:N0} records; refresh failed)"
                    : "Community source refresh unavailable";
                return;
            }

            sources.Clear();
            foreach (var pair in fresh)
                sources[pair.Key] = pair.Value;

            IsLoaded = true;
            Status = failed == 0
                ? $"Ready ({sources.Count:N0} item source records)"
                : $"Ready ({sources.Count:N0} records; {failed} endpoint(s) unavailable)";
            log.Information($"CQG loaded {sources.Count} FFXIV Collect item source records ({succeeded} endpoints succeeded, {failed} failed). ");
        }
        catch (Exception ex)
        {
            Status = IsLoaded
                ? $"Using previous community data ({sources.Count:N0} records; refresh failed)"
                : $"Community source load failed: {ex.Message}";
            log.Warning(ex, "Could not load FFXIV Collect source data.");
        }
        finally
        {
            IsLoading = false;
            refreshGate.Release();
        }
    }

    private async Task<bool> LoadEndpointAsync(
        string endpoint,
        ConcurrentDictionary<uint, List<SourceEntry>> output,
        CancellationToken cancellationToken)
    {
        // FFXIV Collect accepts a high limit.  If the service caps it, the
        // returned list still gives us the most current records rather than
        // failing plugin startup.
        var url = $"https://ffxivcollect.com/api/{endpoint}?limit=5000";
        networkRequests.Request("FFXIV Collect");
        using var response = await http.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            networkRequests.Failure("FFXIV Collect");
            log.Warning($"CQG FFXIV Collect request failed: {endpoint} -> {(int)response.StatusCode}");
            return false;
        }
        networkRequests.Success("FFXIV Collect");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream);

        JsonElement results;

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            results = document.RootElement;
        }
        else if (!document.RootElement.TryGetProperty("results", out results) ||
                 results.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var record in results.EnumerateArray())
        {
            var itemIds = new HashSet<uint>();

            if (TryReadUInt(record, "item_id", out var itemId) && itemId != 0)
                itemIds.Add(itemId);

            // Collection records are not fully uniform.  Frames in particular
            // commonly use a collection-facing display name while exposing the
            // actual inventory unlock item separately.  Try both forms so we do
            // not need an item-ID table for every Framer's Kit / armoire record.
            AddRecordAlias(record, "name", itemIds);
            AddRecordAlias(record, "item_name", itemIds);
            AddRecordAlias(record, "itemName", itemIds);

            if (record.TryGetProperty("item", out var itemValue))
            {
                if (itemValue.ValueKind == JsonValueKind.String)
                {
                    AddAliasMatches(itemValue.GetString(), itemIds);
                }
                else if (itemValue.ValueKind == JsonValueKind.Object)
                {
                    AddRecordAlias(itemValue, "name", itemIds);
                    AddRecordAlias(itemValue, "item_name", itemIds);
                    if (TryReadUInt(itemValue, "id", out var nestedItemId) && nestedItemId != 0)
                        itemIds.Add(nestedItemId);
                }
            }

            if (itemIds.Count == 0)
                continue;

            if (!record.TryGetProperty("sources", out var sourceArray) ||
                sourceArray.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var source in sourceArray.EnumerateArray())
            {
                var type = ReadString(source, "type") ?? "Community";
                var text = ReadString(source, "text");

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var kind = NormalizeKind(type, text);
                var sourceEntry = new SourceEntry(
                    kind,
                    $"{kind}: {text}",
                    Detail: "FFXIV Collect",
                    Priority: 5);

                foreach (var resolvedItemId in itemIds)
                {
                    var list = output.GetOrAdd(resolvedItemId, _ => []);

                    lock (list)
                    {
                        if (!list.Any(x =>
                                string.Equals(
                                    x.Kind,
                                    sourceEntry.Kind,
                                    StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(
                                    x.Name,
                                    sourceEntry.Name,
                                    StringComparison.OrdinalIgnoreCase)))
                        {
                            list.Add(sourceEntry);
                        }
                    }
                }
            }
        }

        return true;
    }


    private void AddRecordAlias(
        JsonElement record,
        string property,
        HashSet<uint> itemIds)
    {
        AddAliasMatches(ReadString(record, property), itemIds);
    }

    private void AddAliasMatches(string? value, HashSet<uint> itemIds)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalized = NormalizeAlias(value);
        if (!targetIdsByAlias.TryGetValue(normalized, out var aliasIds))
            return;

        foreach (var aliasId in aliasIds)
            itemIds.Add(aliasId);
    }

    private static string NormalizeKind(string raw, string? sourceText = null)
    {
        var value = raw.Trim();
        var combined = $"{value} {sourceText}";

        // Distinct systems first; many arrive from FFXIV Collect simply as
        // "Purchase", "Other", or "Event" even though the source text is
        // substantially more useful.
        if (ContainsAny(combined, "Wondrous Tails", "Khloe Aliapoh", "Khloe's "))
            return "Wondrous Tails";
        if (ContainsAny(combined, "Faux Hollows", "Faux Leaves", "Faux Commander"))
            return "Faux Hollows";
        if (ContainsAny(combined, "Kupo of Fortune", "Ishgardian Restoration", "Skybuilders' Scrip", "Firmament"))
            return "Ishgardian Restoration";
        if (combined.Contains("Ocean Fishing", StringComparison.OrdinalIgnoreCase))
            return "Ocean Fishing";
        if (ContainsAny(combined, "Moogle Treasure Trove", "Itinerant Moogle", "Irregular Tomestone"))
            return "Moogle Treasure Trove";
        if (ContainsAny(combined, "Gold Saucer", "MGP", "Cactpot", "GATE", "Lord of Verminion", "Chocobo Racing"))
            return "Gold Saucer";
        if (ContainsAny(combined, "Wolf Marks", "Trophy Crystals", "Crystalline Conflict", "The Feast", "Frontline", "Rival Wings", "PvP Series"))
            return "PvP";
        if (ContainsAny(combined, "Allied Seals", "Centurio Seals", "Sacks of Nuts", "The Hunt", "Clan Mark"))
            return "Hunts";
        if (ContainsAny(combined, "Bicolor Gemstone", "Gemstone Trader", "Shared FATE"))
            return "Shared FATE";
        if (ContainsAny(combined, "Palace of the Dead", "Heaven-on-High", "Eureka Orthos", "Pilgrim's Traverse", "Deep Dungeon"))
            return "Deep Dungeon";
        if (ContainsAny(combined, "Bozja", "Zadnor", "Forbidden Land, Eureka", "Occult Crescent", "Field Operation"))
            return "Field Operation";
        if (combined.Contains("Island Sanctuary", StringComparison.OrdinalIgnoreCase))
            return "Island Sanctuary";
        if (ContainsAny(combined, "Cosmic Exploration", "Stellar Mission", "Cosmic Fortune", "Cosmocredit", "Phaenna", "Sinus Ardorum"))
            return "Cosmic Exploration";
        if (combined.Contains("Custom Deliver", StringComparison.OrdinalIgnoreCase))
            return "Custom Deliveries";
        if (combined.Contains("Achievement Certificate", StringComparison.OrdinalIgnoreCase))
            return "Achievement Certificate";
        if (combined.Contains("Allied Society", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Tribal", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Beast Tribe", StringComparison.OrdinalIgnoreCase))
            return "Allied Society";
        if (combined.Contains("Recruit a Friend", StringComparison.OrdinalIgnoreCase))
            return "Recruit a Friend";
        if (combined.Contains("Eternal Bond", StringComparison.OrdinalIgnoreCase))
            return "Ceremony of Eternal Bonding";
        if (combined.Contains("Twitch", StringComparison.OrdinalIgnoreCase))
            return "Out-of-game Twitch Promotion";
        if (ContainsAny(combined, "Amazon", "Butterfinger", "Grubhub", "Mountain Dew", "Mrs. Freshley's"))
            return "Out-of-game Partner Promotion";
        if (ContainsAny(combined, "pre-order", "preorder"))
            return "Out-of-game Preorder Bonus";
        if (combined.Contains("Collector's Edition", StringComparison.OrdinalIgnoreCase))
            return "Out-of-game Collector's Edition";
        if (ContainsAny(combined, "Fan Festival", "Fan Fest"))
            return "Out-of-game Convention / Fan Festival";
        if (ContainsAny(combined, "plush", "figure", "merchandise", "item code", "art book", "artbook", "soundtrack", "Blu-ray", "Encyclopaedia Eorzea"))
            return "Out-of-game Merchandise Bonus";
        if (combined.Contains("Online Store", StringComparison.OrdinalIgnoreCase))
            return "Online Store";
        if (combined.Contains("Desynth", StringComparison.OrdinalIgnoreCase))
            return "Desynthesis";
        if (combined.Contains("Aetherial Reduction", StringComparison.OrdinalIgnoreCase))
            return "Aetherial Reduction";

        if (value.Contains("Raid", StringComparison.OrdinalIgnoreCase))
            return value.Contains("Alliance", StringComparison.OrdinalIgnoreCase)
                ? "Alliance Raid"
                : "Raid";
        if (value.Contains("Dungeon", StringComparison.OrdinalIgnoreCase))
            return "Dungeon";
        if (value.Contains("Trial", StringComparison.OrdinalIgnoreCase))
            return "Trial";
        if (value.Contains("Achievement", StringComparison.OrdinalIgnoreCase))
            return "Achievement";
        if (value.Contains("Quest", StringComparison.OrdinalIgnoreCase))
            return "Quest";
        if (value.Contains("Vendor", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Purchase", StringComparison.OrdinalIgnoreCase))
            return "Vendor";
        if (value.Contains("PvP", StringComparison.OrdinalIgnoreCase))
            return "PvP";
        if (value.Contains("Treasure", StringComparison.OrdinalIgnoreCase))
            return "Treasure Hunt";
        if (value.Contains("Event", StringComparison.OrdinalIgnoreCase))
            return "Event";

        return string.IsNullOrWhiteSpace(value) ? "Community" : value;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static bool TryReadUInt(
        JsonElement element,
        string property,
        out uint value)
    {
        value = 0;

        if (!element.TryGetProperty(property, out var raw) ||
            raw.ValueKind == JsonValueKind.Null)
            return false;

        if (raw.ValueKind == JsonValueKind.Number)
            return raw.TryGetUInt32(out value);

        if (raw.ValueKind == JsonValueKind.String)
            return uint.TryParse(raw.GetString(), out value);

        return false;
    }

    public bool ClearSessionCache()
    {
        if (IsLoading)
            return false;

        sources.Clear();
        IsLoaded = false;
        Status = "Not loaded";
        return true;
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        http.Dispose();
    }
}
