using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Resolves practical acquisition routes for items.
///
/// v0.6 deliberately uses the actual typed game-sheet relationships for the
/// high-volume source types (recipes, shops, quests, achievements, GC shops,
/// gathering) instead of trying to discover them with generic reflection.
///
/// That change is the main fix for the large number of "No source resolved yet"
/// results in v0.5.
/// </summary>
public sealed class SourceResolver
{
    private readonly IDataManager data;
    private readonly SupplementalCsvIndex supplemental;
    private readonly CollectibleSourceIndex collectibleSources;
    private readonly GarlandSourceIndex garlandSources;
    private readonly IPluginLog log;

    private readonly Dictionary<uint, ContentFinderInfo> cfcById = [];
    private readonly Dictionary<uint, List<ContentFinderInfo>> cfcByTerritory = [];

    // Item ID -> sources derived from live/supplemental game relationships.
    private readonly Dictionary<uint, List<SourceEntry>> gameSources = [];

    // Manual/system-level fallbacks are intentionally separate. They never
    // count as a "primary" source, so Garland is still allowed to test whether
    // the manual fallback can be replaced by generic item-specific data.
    private readonly Dictionary<uint, List<SourceEntry>> curatedFallbackSources = [];

    // Named retainer-venture labels indexed by both RetainerTask and linked task row IDs.
    private readonly Dictionary<uint, string> retainerVentureLabelsById = [];

    // Retail ENpcBase data also contains shop references. LuminaSupplemental has
    // the cleanest reverse index, but building this local fallback dramatically
    // improves vendor/location coverage when supplemental ENpcShop rows are
    // incomplete or use a different schema revision.
    private readonly Dictionary<uint, HashSet<uint>> retailShopNpcIds = [];

    public int CuratedFallbackItemCount => curatedFallbackSources.Count;

    public int CuratedFallbackOnlyCount =>
        curatedFallbackSources.Keys.Count(itemId =>
        {
            var target = TryTargetForMetrics(itemId);
            return !HasNonCuratedSource(itemId) &&
                   (target is null || GetBroadSystemSource(target) is null);
        });

    public int CuratedFallbackSupersededCount =>
        curatedFallbackSources.Keys.Count(itemId =>
        {
            var target = TryTargetForMetrics(itemId);
            return HasNonCuratedSource(itemId) ||
                   (target is not null && GetBroadSystemSource(target) is not null);
        });

    // Provider diagnostics, surfaced only under Detailed -> Debug.
    public int CraftingSourceItems { get; private set; }
    public int GilShopSourceItems { get; private set; }
    public int SpecialShopSourceItems { get; private set; }
    public int GrandCompanySourceItems { get; private set; }
    public int QuestSourceItems { get; private set; }
    public int AchievementSourceItems { get; private set; }
    public int GatheringSourceItems { get; private set; }
    public int LeveSourceItems { get; private set; }
    public int CompanyCraftSourceItems { get; private set; }

    // Cache Item rows for currency names / prices.
    private readonly Dictionary<uint, Item> itemsById = [];

    // Exact English item-name lookup. Triple Triad card rows identify the card
    // rather than the inventory item, so this lets us connect "2B" -> "2B Card".
    private readonly Dictionary<string, List<uint>> itemIdsByName =
        new(StringComparer.OrdinalIgnoreCase);

    // Structured SpecialShop dependency graph.  Keeping this relationship in
    // data form lets CQG explain not only where the target is exchanged, but
    // also where the required token/currency comes from.
    private readonly Dictionary<uint, List<ExchangeDependency>> exchangeDependencies = [];

    public SourceResolver(
        IDataManager data,
        SupplementalCsvIndex supplemental,
        CollectibleSourceIndex collectibleSources,
        GarlandSourceIndex garlandSources,
        IPluginLog log)
    {
        this.data = data;
        this.supplemental = supplemental;
        this.collectibleSources = collectibleSources;
        this.garlandSources = garlandSources;
        this.log = log;

        foreach (var item in data.GetExcelSheet<Item>())
        {
            if (item.RowId == 0)
                continue;

            itemsById[item.RowId] = item;

            var itemName = item.Name.ExtractText().Trim();
            if (!string.IsNullOrWhiteSpace(itemName))
            {
                if (!itemIdsByName.TryGetValue(itemName, out var ids))
                {
                    ids = [];
                    itemIdsByName[itemName] = ids;
                }

                ids.Add(item.RowId);
            }
        }

        BuildContentFinderIndex();
        BuildRetailShopNpcIndex();

        // Strongly typed providers. Each one reads a specific game relation
        // backwards: "which items can this row produce / sell / reward?"
        IndexCrafting();
        IndexGilShops();
        IndexSpecialShops();
        IndexGrandCompanyShops();
        IndexQuestRewards();
        IndexAchievementRewards();
        IndexGathering();
        IndexFishingSpots();
        IndexRetainerVentures();
        IndexLeveRewards();
        IndexCompanyCrafting();
        IndexTripleTriadCards();
        IndexKnownSystemFallbacks();

        RecalculateProviderDiagnostics();
    }

    private void RecalculateProviderDiagnostics()
    {
        static int CountKind(
            Dictionary<uint, List<SourceEntry>> sourceMap,
            params string[] kinds)
        {
            return sourceMap.Count(pair =>
                pair.Value.Any(source =>
                    kinds.Contains(source.Kind, StringComparer.OrdinalIgnoreCase)));
        }

        CraftingSourceItems = CountKind(gameSources, "Crafting");
        GilShopSourceItems = gameSources.Count(pair =>
            pair.Value.Any(source =>
                source.Kind == "Vendor" &&
                source.Name.StartsWith("Gil Vendor", StringComparison.OrdinalIgnoreCase)));

        // Preserve the exact resilient SpecialShop count if it was populated.
        if (SpecialShopSourceItems == 0)
        {
            SpecialShopSourceItems = gameSources.Count(pair =>
                pair.Value.Any(source =>
                    source.Kind == "Vendor" &&
                    source.Name.StartsWith(
                        "Special / Currency Shop",
                        StringComparison.OrdinalIgnoreCase)));
        }

        GrandCompanySourceItems = gameSources.Count(pair =>
            pair.Value.Any(source =>
                source.Kind == "Vendor" &&
                source.Name.StartsWith(
                    "Grand Company Quartermaster",
                    StringComparison.OrdinalIgnoreCase)));

        QuestSourceItems = CountKind(gameSources, "Quest");
        AchievementSourceItems = CountKind(gameSources, "Achievement");
        GatheringSourceItems = CountKind(gameSources, "Gathering", "Fishing");
        LeveSourceItems = CountKind(gameSources, "Levequest");

        if (CompanyCraftSourceItems == 0)
            CompanyCraftSourceItems = CountKind(gameSources, "Company Workshop");
    }

    public AcquisitionResult Resolve(TargetEntry target) => ResolveInternal(target, playerFacing: true);

    /// <summary>
    /// Returns the unfiltered resolver graph for diagnostics/export only. Player-facing
    /// UI must use Resolve(), which removes vague/internal relationship records.
    /// </summary>
    public AcquisitionResult ResolveDiagnostic(TargetEntry target) => ResolveInternal(target, playerFacing: false);

    private AcquisitionResult ResolveInternal(TargetEntry target, bool playerFacing)
    {
        var sources = new List<SourceEntry>();

        if (gameSources.TryGetValue(target.ItemId, out var local))
            sources.AddRange(local);

        // FFXIV Collect is item-specific source data for collectible systems.
        // If it has a source for this exact item, prefer it over supplemental
        // duty associations that may have been reconstructed indirectly through
        // territory/chest/boss relationships.
        var communitySources = collectibleSources.GetSources(target.ItemId);
        var hasCollectibleAuthority = communitySources.Count > 0;

        if (hasCollectibleAuthority)
            sources.AddRange(communitySources);

        // Joined duty references are useful for ordinary equipment, but for an
        // item covered by FFXIV Collect we intentionally suppress them. This is
        // a general confidence rule, not a Dawntrail/mount special case.
        if (!hasCollectibleAuthority &&
            supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var dutyRefs))
        {
            foreach (var dutyRef in dutyRefs)
            {
                foreach (var duty in ResolveDutyReference(dutyRef))
                    sources.Add(DutySource(duty, "Supplemental duty association"));
            }
        }

        // Community-maintained supplemental data fills gaps the retail Excel
        // sheets do not contain. For collectible items with an exact FFXIV
        // Collect source, skip only duty-family supplemental rows; keep other
        // useful sources such as FATE, vendor-adjacent conversion, voyage, etc.
        if (supplemental.ItemSources.TryGetValue(target.ItemId, out var records))
        {
            foreach (var record in records)
            {
                var source = ResolveSupplemental(record);
                if (source is null)
                    continue;

                if (hasCollectibleAuthority &&
                    source.Kind is "Duty" or "Activity")
                {
                    continue;
                }

                sources.Add(source);
            }
        }

        // Generic item-specific fallback for anything the built-in/community
        // providers still do not cover.
        sources.AddRange(garlandSources.GetSources(target.ItemId));

        // Metadata-driven broad system source. This sits after Garland so
        // exact item-specific data can replace it automatically.
        var broadSystem = GetBroadSystemSource(target);
        if (broadSystem is not null)
            sources.Add(broadSystem);

        // Curated rules are the absolute last source fallback and are hidden
        // whenever any generic/data-driven source exists.
        if (!HasNonCuratedSource(target.ItemId) &&
            broadSystem is null &&
            curatedFallbackSources.TryGetValue(target.ItemId, out var curated))
        {
            sources.AddRange(curated);
        }

        var guide = CuratedGuides.ForTarget(target);

        // A curated prerequisite guide is a real acquisition route. If no data
        // provider produced a concrete source, expose the guide itself as the
        // primary source instead of showing "No source resolved yet" above an
        // otherwise-complete prerequisite walkthrough.
        if (guide is not null && sources.Count == 0)
        {
            sources.Add(new SourceEntry(
                "Curated Guide",
                guide.Name,
                "Follow the prerequisite guide below.",
                Priority: 75));
        }

        // A tradable item can always be acquired from another player / market
        // board when listings exist. Keep this low-priority so a real farming,
        // crafting or vendor route remains the main Source line.
        if (!target.IsUntradable)
        {
            sources.Add(new SourceEntry(
                "Market",
                "Market Board / Player Trade",
                Priority: 95));
        }

        // Seafood is a real acquisition family even when we cannot yet resolve
        // the exact FishingSpot / SpearfishingNotebook row. This is preferable
        // to presenting thousands of actual fish as "No source resolved yet".
        //
        // Exact spot + bait/condition support can be layered on later without
        // changing the target catalog.
        if ((target.Category == "Fish" ||
             string.Equals(
                 target.GameUiCategory,
                 "Seafood",
                 StringComparison.OrdinalIgnoreCase)) &&
            !sources.Any(s => s.Kind is "Fishing" or "Gathering"))
        {
            sources.Add(new SourceEntry(
                "Fishing",
                "Fishing / Spearfishing",
                "Exact fishing spot is not resolved yet.",
                Priority: 80));
        }

        AddExchangeDependencySources(target.ItemId, sources);
        SuppressAmbiguousSupplementalDuties(sources);
        SuppressGenericSourcesWhenSpecificAlternativeExists(sources);

        IEnumerable<SourceEntry> finalSources = sources;
        if (playerFacing)
        {
            // Hard UI rule: a source shown to a player must describe something
            // they can actually do. Resolver relationships such as ItemSupplement
            // / Item conversion remain available through ResolveDiagnostic(), but
            // never compete for the primary source or appear in Detailed mode.
            finalSources = finalSources
                .Where(IsPlayerFacingActionableSource)
                .Select(ToPlayerFacingSource);
        }

        var deduped = finalSources
            .GroupBy(
                s => $"{s.Kind}|{s.Name}|{s.Detail}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderBy(x => IsMarketSource(x) ? 1 : 0)
                .ThenBy(x => x.Priority)
                .ThenByDescending(x => x.IsUnlocked == true)
                .First())
            // Market Board / Player Trade is a convenience alternative, never
            // the preferred acquisition path while any actionable in-game source
            // exists. Keep this rule independent of provider priority numbers so
            // a future source cannot accidentally rank the board above content.
            .OrderBy(x => IsMarketSource(x) ? 1 : 0)
            .ThenBy(x => x.Priority)
            .ThenByDescending(x => x.IsUnlocked == true)
            .ThenBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A prerequisite guide is itself actionable. If every raw provider row
        // was diagnostic-only, do not leave the player with a blank source panel.
        if (playerFacing && deduped.Count == 0 && guide is not null)
        {
            deduped.Add(new SourceEntry(
                "Guide",
                guide.Name,
                "Follow the prerequisite steps below.",
                Priority: 75,
                SpecificityHint: "Named"));
        }

        return new AcquisitionResult(
            target,
            deduped,
            guide);
    }

    // ---------------------------------------------------------------------
    // Game-sheet source providers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Recipe.ItemResult is authoritative for craftable items.
    /// </summary>
    private void IndexCrafting()
    {
        try
        {
            foreach (var recipe in data.GetExcelSheet<Recipe>())
            {
                var itemId = recipe.ItemResult.RowId;
                if (itemId == 0)
                    continue;

                string job = string.Empty;
                byte level = 0;

                try
                {
                    if (recipe.CraftType.IsValid)
                        job = recipe.CraftType.Value.Name.ExtractText().Trim();

                    if (recipe.RecipeLevelTable.IsValid)
                        level = recipe.RecipeLevelTable.Value.ClassJobLevel;
                }
                catch
                {
                    // A name/level is optional metadata. The source itself is
                    // still valid even if a generated reference fails.
                }

                var detail = (job.Length > 0, level > 0) switch
                {
                    (true, true) => $"{job} Lv. {level}",
                    (true, false) => job,
                    (false, true) => $"Lv. {level}",
                    _ => null,
                };

                Add(itemId, new SourceEntry(
                    "Crafting",
                    "Crafted",
                    detail,
                    Priority: 30));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index crafting recipes.");
        }
    }

    /// <summary>
    /// GilShopItem is a SUBROW sheet. This is why the generic v0.5 provider
    /// both failed to compile and missed ordinary vendors.
    ///
    /// The shop entry identifies stock; Item.PriceMid is the purchase price.
    /// </summary>
    private void IndexGilShops()
    {
        try
        {
            foreach (var shopRows in data.GetSubrowExcelSheet<GilShopItem>())
            {
                var shopId = shopRows.RowId;
                var npcNames = ResolveShopNpcNames(shopId);
                var supplementalShopName = supplemental.GetShopName(shopId);

                foreach (var entry in shopRows)
                {
                    var itemId = entry.Item.RowId;
                    if (itemId == 0)
                        continue;

                    uint price = 0;
                    if (itemsById.TryGetValue(itemId, out var item))
                        price = item.PriceMid;

                    var detailParts = new List<string>();

                    if (price > 0)
                        detailParts.Add($"{price:N0} gil");

                    if (!string.IsNullOrWhiteSpace(supplementalShopName))
                        detailParts.Add(supplementalShopName!);

                    var detail = detailParts.Count > 0
                        ? string.Join(" | ", detailParts)
                        : null;

                    if (npcNames.Count > 0)
                    {
                        foreach (var npc in npcNames)
                        {
                            Add(itemId, new SourceEntry(
                                "Vendor",
                                $"Vendor: {npc.Name}",
                                detail,
                                Priority: 35,
                                NpcId: npc.Id));
                        }
                    }
                    else
                    {
                        Add(itemId, new SourceEntry(
                            "Vendor",
                            "Gil Vendor",
                            detail,
                            Priority: 40));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index gil shops.");
        }
    }

    /// <summary>
    /// SpecialShop contains tomestone, scrip, mark, Wolf Mark, gemstone,
    /// upgrade-material and other exchange shops.
    ///
    /// This is the provider that resolves examples like Greatsword of the
    /// Behemoth King -> 2,000 Wolf Marks.
    /// </summary>
    private void IndexSpecialShops()
    {
        var resolvedItems = new HashSet<uint>();

        try
        {
            foreach (var shop in data.GetExcelSheet<SpecialShop>())
            {
                string shopName;

                try
                {
                    shopName = shop.Name.ExtractText().Trim();
                }
                catch
                {
                    shopName = string.Empty;
                }

                var supplementalShopName = supplemental.GetShopName(shop.RowId);
                if (string.IsNullOrWhiteSpace(shopName) ||
                    (ContainsCjk(shopName) && !string.IsNullOrWhiteSpace(supplementalShopName) && !ContainsCjk(supplementalShopName!)))
                {
                    shopName = supplementalShopName ?? string.Empty;
                }

                var npcNames = ResolveShopNpcNames(shop.RowId);

                foreach (var trade in shop.Item)
                {
                    try
                    {
                        // Index the RECEIVE side first. Cost parsing is optional
                        // metadata and must never be allowed to prevent an item
                        // from being recognized as a shop reward.
                        var received = new List<(uint ItemId, uint Count)>();

                        try
                        {
                            foreach (var reward in trade.ReceiveItems)
                            {
                                var itemId = reward.Item.RowId;
                                if (itemId == 0)
                                    continue;

                                var count = (uint)Math.Max(1, reward.ReceiveCount);
                                received.Add((itemId, count));
                            }
                        }
                        catch (Exception ex)
                        {
                            log.Debug(ex, $"Could not parse one SpecialShop receive entry in row {shop.RowId}.");
                            continue;
                        }

                        if (received.Count == 0)
                            continue;

                        var costs = new List<string>();
                        var structuredCosts = new List<(uint ItemId, uint Amount)>();

                        try
                        {
                            foreach (var cost in trade.ItemCosts)
                            {
                                var currencyId = cost.ItemCost.RowId;
                                var amount = cost.CurrencyCost;

                                if (amount == 0)
                                    continue;

                                // SpecialShop uses row-id 1 as a sentinel for a number
                                // of non-item currencies. Treating that as Item#1 (Gil)
                                // produces confidently wrong guidance such as "600 gil"
                                // for Poetics gear. Prefer a currency name recoverable
                                // from the shop identity and only create a dependency if
                                // that currency is represented by a real Item row.
                                if (currencyId == 1)
                                {
                                    if (TryInferSpecialShopCurrency(shopName, out var inferredCurrency))
                                    {
                                        costs.Add($"{amount:N0} {inferredCurrency}");
                                        if (itemIdsByName.TryGetValue(inferredCurrency, out var inferredIds) && inferredIds.Count > 0)
                                            structuredCosts.Add((inferredIds[0], amount));
                                    }

                                    // SpecialShop row 1 is a currency sentinel, not
                                    // literal Item#1/Gil. If the currency cannot be
                                    // named safely, omit the price instead of lying.
                                    continue;
                                }

                                if (currencyId == 0)
                                    continue;

                                costs.Add(FormatCost(currencyId, amount));
                                structuredCosts.Add((currencyId, amount));
                            }
                        }
                        catch (Exception ex)
                        {
                            // Some shop rows use non-item bucket currencies.
                            // That should only make the price less specific; the
                            // acquisition source itself is still valid.
                            log.Debug(ex, $"Could not parse SpecialShop costs in row {shop.RowId}.");
                        }

                        var detailParts = new List<string>();

                        if (costs.Count > 0)
                            detailParts.Add(string.Join(" + ", costs));

                        try
                        {
                            if (trade.Quest.IsValid && trade.Quest.RowId != 0)
                            {
                                var questName = RowName<Quest>(trade.Quest.RowId, "Name");
                                if (!string.IsNullOrWhiteSpace(questName))
                                    detailParts.Add($"Requires quest: {questName}");
                            }
                        }
                        catch
                        {
                            // Requirement metadata is optional.
                        }

                        var detail = detailParts.Count > 0
                            ? string.Join(" | ", detailParts)
                            : null;

                        var sourceKind = ClassifySpecialShopKind(shopName, detail);

                        foreach (var reward in received)
                        {
                            if (npcNames.Count > 0)
                            {
                                foreach (var npc in npcNames)
                                {
                                    var displayName = string.IsNullOrWhiteSpace(shopName)
                                        ? $"Vendor: {npc.Name}"
                                        : $"Special / Currency Shop: {shopName} — {npc.Name}";

                                    Add(reward.ItemId, new SourceEntry(
                                        sourceKind,
                                        displayName,
                                        detail,
                                        Priority: 32,
                                        NpcId: npc.Id));
                                }
                            }
                            else
                            {
                                var displayName = string.IsNullOrWhiteSpace(shopName)
                                    ? "Special / Currency Shop"
                                    : $"Special / Currency Shop: {shopName}";

                                Add(reward.ItemId, new SourceEntry(
                                    sourceKind,
                                    displayName,
                                    detail,
                                    Priority: 35));
                            }

                            foreach (var structuredCost in structuredCosts)
                            {
                                AddExchangeDependency(
                                    reward.ItemId,
                                    structuredCost.ItemId,
                                    structuredCost.Amount,
                                    shopName,
                                    npcNames.Select(x => x.Name).ToArray());
                            }

                            resolvedItems.Add(reward.ItemId);
                        }
                    }
                    catch (Exception ex)
                    {
                        // One malformed/odd trade must not abort the remaining
                        // thousands of SpecialShop entries.
                        log.Debug(ex, $"Could not index one SpecialShop trade in row {shop.RowId}.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index special/currency shops.");
        }

        SpecialShopSourceItems = resolvedItems.Count;
        log.Information($"CQG SpecialShop indexed {SpecialShopSourceItems:N0} distinct item rewards.");
    }

    private static bool TryInferSpecialShopCurrency(string shopName, out string currencyName)
    {
        currencyName = string.Empty;
        if (string.IsNullOrWhiteSpace(shopName))
            return false;

        var known = new[]
        {
            "Allagan Tomestone of Poetics",
            "Allagan Tomestone of Mathematics",
            "Allagan Tomestone of Heliometry",
            "Wolf Mark",
            "Trophy Crystal",
            "Manderville Gold Saucer Point",
            "MGP",
            "Allied Seal",
            "Centurio Seal",
            "Sack of Nuts",
            "Bicolor Gemstone",
            "Skybuilders' Scrip",
            "White Crafters' Scrip",
            "Purple Crafters' Scrip",
            "White Gatherers' Scrip",
            "Purple Gatherers' Scrip",
        };

        foreach (var candidate in known)
        {
            if (!shopName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                continue;

            currencyName = candidate.Equals("MGP", StringComparison.OrdinalIgnoreCase)
                ? "MGP"
                : candidate;
            return true;
        }

        // Historical tomestone shop names often use "Allagan Tomestones of X"
        // while the currency item is singular. Recover that form generically.
        var pluralMarker = "Allagan Tomestones of ";
        var pluralIndex = shopName.IndexOf(pluralMarker, StringComparison.OrdinalIgnoreCase);
        if (pluralIndex >= 0)
        {
            var tail = shopName[(pluralIndex + pluralMarker.Length)..];
            var stop = tail.IndexOfAny(['(', '—', '-', '|']);
            if (stop > 0)
                tail = tail[..stop];
            tail = tail.Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                currencyName = $"Allagan Tomestone of {tail}";
                return true;
            }
        }

        return false;
    }

    private static string ClassifySpecialShopKind(string shopName, string? detail)
    {
        var text = $"{shopName} {detail}";

        if (ContainsAny(text, "MGP", "Gold Saucer", "Cactpot", "Verminion", "Chocobo Racing"))
            return "Gold Saucer";
        if (ContainsAny(text, "Khloe", "Wondrous Tails", "Certificate of Commendation"))
            return "Wondrous Tails";
        if (ContainsAny(text, "Faux", "Unreal"))
            return "Faux Hollows";
        if (ContainsAny(text, "Wolf Mark", "Trophy Crystal", "Wolves' Den", "Crystalline Conflict", "PvP"))
            return "PvP";
        if (ContainsAny(text, "Allied Seal", "Centurio Seal", "Sack of Nuts", "Hunt Bill", "Clan Mark"))
            return "Hunts";
        if (ContainsAny(text, "Bicolor Gemstone", "Gemstone Trader", "Shared FATE"))
            return "Shared FATE";
        if (ContainsAny(text, "Skybuilder", "Firmament", "Ishgardian Restoration", "Kupo of Fortune"))
            return "Ishgardian Restoration";
        if (ContainsAny(text, "Cowrie", "Island Sanctuary"))
            return "Island Sanctuary";
        if (ContainsAny(text, "Cosmocredit", "Dronebit", "Cosmic", "Stellar"))
            return "Cosmic Exploration";
        if (ContainsAny(text, "Bozja", "Zadnor", "Eureka", "Occult Crescent", "Phantom"))
            return "Field Operation";
        if (ContainsAny(text, "Scrip Exchange", "Crafter's Scrip", "Gatherer's Scrip", "Crafters' Scrip", "Gatherers' Scrip"))
            return "Scrip Exchange";
        if (ContainsAny(text, "Allied Society", "Tribal", "Beast Tribe"))
            return "Allied Society";
        if (ContainsAny(text, "Achievement Certificate"))
            return "Achievement Certificate";

        return "Vendor";
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsCjk(string text) =>
        text.Any(ch =>
            (ch >= '\u3040' && ch <= '\u30ff') ||
            (ch >= '\u3400' && ch <= '\u4dbf') ||
            (ch >= '\u4e00' && ch <= '\u9fff') ||
            (ch >= '\uac00' && ch <= '\ud7af'));

    /// <summary>
    /// Targeted v0.43 source semantics derived from coherent families in the
    /// post-v0.42 specificity backlog.  These rules identify a real named
    /// acquisition system without pretending to know an exact node/NPC/drop
    /// when the retail/supplemental providers do not expose one.
    /// </summary>
    private static SourceEntry? GetTargetedSpecificitySource(TargetEntry target)
    {
        var id = target.ItemId;
        var name = target.Name.Trim();
        var ui = target.GameUiCategory.Trim();

        // Container-backed rewards must resolve to the content that awards the
        // container, not to the internal ItemSupplement conversion edge. This
        // is the first concrete member of that policy and also serves as a
        // regression target for player-facing source ranking.
        if (name.Equals("Modern Aesthetics - A Close Shave", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Deep Dungeon",
                "Eureka Orthos — Bronze-tinged Sacks (floors 1–30)",
                "Obtain Bronze-tinged Sacks from the Accursed Hoard on floors 1–30 and have them appraised for a chance at this hairstyle.",
                Priority: 20,
                SpecificityHint: "Named");
        }

        // Allied Society quest-only gathering/fishing rows.  These contiguous
        // item families are the private quest materials introduced with each
        // gathering society and are explicitly not normal gathering-log items.
        if (id is >= 22616 and <= 22689)
        {
            return new SourceEntry(
                "Allied Society",
                "Namazu society quests",
                "Quest-only gathering/fishing material in the Azim Steppe.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        if (id is >= 29512 and <= 29589)
        {
            return new SourceEntry(
                "Allied Society",
                "Qitari society quests",
                "Quest-only gathering/fishing material in the Rak'tika Greatwood.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        if (id is >= 38276 and <= 38347)
        {
            return new SourceEntry(
                "Allied Society",
                "Omicron society quests",
                "Quest-only gathering/fishing material in Ultima Thule / Elysion.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        if (id is >= 48009 and <= 48083)
        {
            return new SourceEntry(
                "Allied Society",
                "Mamool Ja society quests",
                "Quest-only gathering/fishing material in Yak T'el.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        // Housing construction permits are purchased from residential-district
        // housing merchants.  The live GilShop relation knows the price but not
        // the useful vendor context.
        if (ui.Equals("Construction Permit", StringComparison.OrdinalIgnoreCase) &&
            name.Contains(" Permit (", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Housing",
                "Residential District Housing Merchant — construction permit",
                "Purchased for gil after acquiring the corresponding housing plot.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        // Magicked Prism (Heart) (singular) is the Platinum-plan reward tied
        // to The Ties That Bind, distinct from the plural Hearts vendor favor.
        if (name.Equals("Magicked Prism (Heart)", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Ceremony",
                "The Ties That Bind — Platinum Ceremony Plan",
                "Ceremony of Eternal Bonding Platinum-plan reward.",
                Priority: 44,
                SpecificityHint: "Named");
        }

        // Ceremony favors all share the same Ceremony Outfitter inventory.
        if (name is "Magicked Prism (Balloon)" or "Magicked Prism (Bubbles)" or
            "Magicked Prism (Hearts)" or "Magicked Prism (Petals)")
        {
            return new SourceEntry(
                "Ceremony",
                "Ceremony Outfitter — East Shroud",
                "Ceremony of Eternal Bonding favor; 19 gil after unlocking the Ceremony Outfitter.",
                Priority: 45,
                SpecificityHint: "Named");
        }

        // Blue Mage Whalaqee totems are unlocked through BLU achievements and
        // claimed from one dedicated NPC.
        if (name.StartsWith("Whalaqee ", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(" Totem", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Blue Mage",
                "Wayward Gaheel Ja — Acquire Whalaqee Totems",
                "Ul'dah - Steps of Thal (X:12.5, Y:12.9); unlock requirement varies by Blue Mage achievement.",
                Priority: 42,
                SpecificityHint: "Named");
        }

        // Replica Cosmic tools are exchange rewards within Cosmic Exploration.
        if (name.EndsWith(" Replica", StringComparison.OrdinalIgnoreCase) &&
            ContainsAny(name, "Cosmic ", "Stellar ", "Hyper", " of Stars"))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Exploration replica-tool exchange",
                "Replica of a Cosmic Tool progression stage.",
                Priority: 72,
                SpecificityHint: "Named");
        }

        // ARR combat soul crystals are the level-30 job unlock rewards.  Crafter
        // souls belong to the specialist system rather than ordinary job quests.
        if (id is >= 4542 and <= 4548)
        {
            return new SourceEntry(
                "Job Progression",
                "ARR level 30 job quest",
                "Soul crystal awarded when unlocking the corresponding ARR job.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        if (id is >= 10337 and <= 10344)
        {
            return new SourceEntry(
                "Crafting Specialist",
                "Crafter Specialist system — Soul of the Crafter",
                "Specialist soul crystal for a crafting class.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        // The remaining generic currency rows can usually be assigned to the
        // content system encoded directly in the token/currency name.
        if (name.StartsWith("Allagan Tomestone of ", StringComparison.OrdinalIgnoreCase))
        {
            var detail = name.Equals("Allagan Tomestone of Poetics", StringComparison.OrdinalIgnoreCase)
                ? "Earned repeatedly from eligible duties, roulettes, hunts, and other level-capped content. Use repeatable activities rather than one-time quest rewards when farming a required amount."
                : "Earned from the eligible endgame duties, roulettes, hunts, and other reward systems for its patch/expansion. Availability changes as tomestones rotate into or out of circulation.";

            return new SourceEntry(
                "Tomestones",
                "Allagan Tomestone progression",
                detail,
                Priority: 5,
                SpecificityHint: "Named repeatable currency source");
        }

        if (name.Equals("Silver Chocobo Feather", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "World Population Incentive",
                "Preferred+ World character-creation bonus",
                "Eligible newly created characters on Preferred+ Worlds receive 10 Silver Chocobo Feathers on first login. Exchange them with Calamity Salvagers for leveling gear.",
                Priority: 35,
                SpecificityHint: "Named conditional acquisition source");
        }

        if (ContainsAny(name,
                "Crafters' Scrip", "Gatherers' Scrip",
                "Crafters' Scrip Token", "Gatherers' Scrip Token"))
        {
            return new SourceEntry(
                "Scrip Exchange",
                "Crafter / Gatherer Scrip system",
                "Collectable-delivery and scrip-exchange progression.",
                Priority: 84,
                SpecificityHint: "Named");
        }

        if (name.Equals("Achievement Certificate", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Achievement Certificate",
                "Jonathas — Achievement Certificate rewards",
                "Achievement reward currency/exchange in Old Gridania.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("Faux Leaf", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Faux Hollows",
                "Faux Hollows",
                "Reward currency from Unreal-trial Faux Hollows.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("Trophy Crystal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "PvP",
                "PvP Series — Trophy Crystals",
                "PvP Series currency spent at the Wolves' Den Pier.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("Skybuilders' Scrip", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Ishgardian Restoration",
                "Ishgardian Restoration — Skybuilders' Scrips",
                "Firmament crafting/gathering reward currency.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.StartsWith("Khloe's ", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Certificate of Commendation", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Wondrous Tails",
                "Wondrous Tails — Khloe's Certificate of Commendation",
                "Certificate reward from Wondrous Tails.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("Company Seal Voucher", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Grand Company",
                "Grand Company seal/voucher system",
                Priority: 85,
                SpecificityHint: "Named");
        }

        if (name is "Priority Seal Allowance" or "Mission Ceruleum Voucher")
        {
            return new SourceEntry(
                "Grand Company",
                "Adventurer Squadron missions",
                "Grand Company squadron mission item.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        if (name.Equals("Cryptic Seal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Eureka",
                "The Forbidden Land, Eureka",
                "Eureka reward/exchange token.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        if (name.Equals("Corvosi Manuscript", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Exploration",
                "The Merchant's Tale",
                "Corvos exploration currency/reward item.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.EndsWith(" Credit", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Exploration Token", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Star Contributor Certificate", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(name, "Lunar", "Phaenna", "Oizys", "Auxesia", "Star Contributor"))
            {
                return new SourceEntry(
                    "Cosmic Exploration",
                    "Cosmic Exploration",
                    "Zone credit/token or Starward Standings reward.",
                    Priority: 84,
                    SpecificityHint: "Named");
            }
        }

        if (name is "Inferno Totem" or "Inferno Seal")
        {
            return new SourceEntry(
                "Trial",
                "The Bowl of Embers (Extreme)",
                "Ifrit Extreme token/reward item.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        if (name is "Vortex Totem" or "Vortex Seal")
        {
            return new SourceEntry(
                "Trial",
                "The Howling Eye (Extreme)",
                "Garuda Extreme token/reward item.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        if (name.Equals("Tremor Seal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "The Navel (Extreme)",
                "Titan Extreme token/reward item.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        if (name.EndsWith(" Totem", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "Extreme Trial token",
                "Totem awarded by a specific Extreme Trial; exact trial remains in the exact-source backlog.",
                Priority: 91,
                SpecificityHint: "Named");
        }

        if (name is "Aglaia Coin" or "Euphrosyne Coin" or "Manufactured Coin" or
            "Puppet's Coin" or "Breach Coin" or "Ranperre Coin")
        {
            return new SourceEntry(
                "Alliance Raid",
                "Alliance Raid coin reward",
                "Coin awarded by its corresponding Alliance Raid.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        if (ContainsAny(name,
                "Rowena's Token (", "Mowen's Token (",
                "Tomestone"))
        {
            return new SourceEntry(
                "Endgame Gear",
                "Tomestone / raid gear progression",
                "Historical endgame gear exchange or weapon-upgrade token.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        // Seasonal/collaboration families from the remaining legacy-event tail.
        // Old year-specific materials are preserved for historical/existing-copy
        // guidance rather than being mislabeled as currently recurring drops.
        if (name.StartsWith("Legendary ", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(" Medal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Event",
                "Yo-kai Watch collaboration",
                "Legendary Yo-kai Medal used for collaboration weapon progression.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        if (name.Contains("Archon Egg", StringComparison.OrdinalIgnoreCase) ||
            name is "Odd Egg" or "Lucky Egg" or "Special Brilliant Egg" or "Brittle Motley Egg")
        {
            return new SourceEntry(
                "Legacy Event",
                "Hatching-tide",
                "Historical Hatching-tide event material; existing copies only unless Square Enix reuses the item.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Bombard Ash", StringComparison.OrdinalIgnoreCase) ||
            name is "Blue Spinner" or "Red Lion" or "Green Comet" or "Azeyma's Candle")
        {
            return new SourceEntry(
                "Legacy Event",
                "Firefall Faire / Moonfire Faire",
                "Historical summer-event material or firework.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Faire Voucher", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Moonfire Faire",
                "Historical Moonfire Faire voucher.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.StartsWith("Heavensturn ", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cut Mochi", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Heavensturn",
                "Historical Heavensturn event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Valentione", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Valentione's Day",
                "Historical Valentione's Day event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Pumpkin Cookie", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Demonic Cookie", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "All Saints' Wake",
                "Historical All Saints' Wake event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Equals("Moth-eaten Doll Parts", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Certificate of Collaboration", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Little Ladies' Day",
                "Historical Little Ladies' Day event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.StartsWith("Rising Token", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "The Rising",
                "Historical Rising event token.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name is "Eternal Ice" or "Starlight Box" or "Twinklebox")
        {
            return new SourceEntry(
                "Legacy Event",
                "Starlight Celebration",
                "Historical Starlight Celebration event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Otherworldly Parcel", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Lightning Strikes collaboration",
                "Historical FINAL FANTASY XIII collaboration quest/FATE material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Equals("Deluxe Heavenscracker", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Out-of-game Promotion",
                "Official promotional campaigns",
                "Distributed through official partner campaigns, including Twitch/retail promotions.",
                Priority: 84,
                SpecificityHint: "Named");
        }

        // Current/future currencies and encounter tokens that survived the
        // generic currency pass can be tied to their named content directly.
        if (name.Equals("Guardian Arkveld Certificate", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "The Windward Wilds (Extreme)",
                "Two Guardian Arkveld Certificates are awarded per clear.",
                Priority: 76,
                SpecificityHint: "Named");
        }

        if (name.Equals("Totem of Naught", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "The Unmaking (Extreme)",
                "Extreme-trial token exchanged for Weapons of Naught and related rewards.",
                Priority: 76,
                SpecificityHint: "Named");
        }

        if (name.Equals("MGF", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Gold Saucer",
                "Blunderville — Manderville Gold Saucer Fame (MGF)",
                "Earned by participating in the Fall Guys collaboration's Blunderville shows.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("MGC", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Gold Saucer",
                "Keybound Brawler — Manderville Gold Credits (MGC)",
                "Gold Saucer Keybound Square currency spent at the MGC Trader.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        // Dubious Dirt is not an ordinary mining collectible: it is gathered
        // specifically for the Namazu daily quest Overcommitment.
        if (name.Equals("Dubious Dirt", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Allied Society",
                "Namazu daily quest — Overcommitment",
                "Mine northeast of the Dawn Throne while undertaking Overcommitment.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        // Dawntrail Wachumeqimeqi fishing quest catches are deliberately
        // absent from the ordinary Fish Guide; the quest itself is the real
        // acquisition gate and therefore the most useful source to show.
        if (name.Equals("Cenote Shark", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Fishing Quest",
                "Donuhanu's Devotion — Bitterbark Cenote",
                "Wachumeqimeqi Fisher quest catch in Yak T'el (around X:26.5, Y:39.0).",
                Priority: 76,
                SpecificityHint: "Named");
        }

        if (name.Equals("Fulmisturgeon", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Fishing Quest",
                "An Odd Fish Odyssey — Alexandrian Ruins",
                "Wachumeqimeqi Fisher quest catch in Heritage Found (around X:8.0, Y:34.0).",
                Priority: 76,
                SpecificityHint: "Named");
        }

        // Field-operation coffers are generic in LuminaSupplemental, but several
        // remaining families identify the actual field operation.
        if (name.StartsWith("Occult ", StringComparison.OrdinalIgnoreCase) ||
            name is "Horizontal Neon Wall Light" or "Vertical Neon Wall Light" or
                "Stone Partition" or "Aetherspun Gold" or "Esoteric Gold Piece" or
                "Fortune Carrot")
        {
            return new SourceEntry(
                "Occult Crescent",
                "The Occult Crescent — overworld treasure coffers / field rewards",
                "Available from Occult Crescent field-operation reward pools; exact coffer/encounter remains in the exact-source backlog.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        if (name is "Archaeodemon Horns" or "Stuffed Happy Bunny")
        {
            return new SourceEntry(
                "Field Operation",
                "Eureka Happy Bunny / Occult Crescent treasure coffers",
                "Field-operation treasure reward with multiple current/historical routes.",
                Priority: 84,
                SpecificityHint: "Named");
        }

        if (name.Equals("Garden Mood Lighting", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Submarine Voyage",
                "Southern Aerslaent Plunge (K)",
                "The Northern Empty — Free Company Submarine Venture, rank 144.",
                Priority: 58,
                SpecificityHint: "Named");
        }

        return null;
    }

    /// <summary>
    /// Grand Company quartermaster stock lives in GCScripShopItem subrows.
    /// </summary>
    private void IndexGrandCompanyShops()
    {
        try
        {
            var categories = data.GetExcelSheet<GCScripShopCategory>();

            foreach (var shopRows in data.GetSubrowExcelSheet<GCScripShopItem>())
            {
                uint companyId = 0;

                if (categories.TryGetRow(shopRows.RowId, out var category))
                    companyId = category.GrandCompany.RowId;

                foreach (var entry in shopRows)
                {
                    var itemId = entry.Item.RowId;
                    if (itemId == 0)
                        continue;

                    var seals = entry.CostGCSeals;
                    var sealName = companyId switch
                    {
                        1 => ItemName(20) ?? "Storm Seals",
                        2 => ItemName(21) ?? "Serpent Seals",
                        3 => ItemName(22) ?? "Flame Seals",
                        _ => "Grand Company seals",
                    };

                    Add(itemId, new SourceEntry(
                        "Vendor",
                        "Grand Company Quartermaster",
                        seals > 0 ? $"{seals:N0} {sealName}" : null,
                        Priority: 35));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index Grand Company shops.");
        }
    }

    /// <summary>
    /// Quest has explicit guaranteed and optional item-reward collections.
    /// Reading those is much more reliable than recursively reflecting every
    /// property whose name happens to contain "Item".
    /// </summary>
    private void IndexQuestRewards()
    {
        try
        {
            foreach (var quest in data.GetExcelSheet<Quest>())
            {
                var name = quest.Name.ExtractText().Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string detail = name;

                try
                {
                    if (quest.ClassJobLevel is [var level, ..] && level > 0)
                        detail = $"{name} (Lv. {level})";
                }
                catch
                {
                    // Quest name alone is enough.
                }

                foreach (var reward in quest.Reward)
                    AddQuestReward(reward.RowId, detail);

                foreach (var reward in quest.OptionalItemReward)
                    AddQuestReward(reward.RowId, detail);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index quest rewards.");
        }
    }

    private void AddQuestReward(uint itemId, string quest)
    {
        if (itemId == 0 || !itemsById.ContainsKey(itemId))
            return;

        Add(itemId, new SourceEntry(
            "Quest",
            quest,
            Priority: 20));
    }

    /// <summary>
    /// Achievement.Item is the achievement's direct item reward.
    /// Fully qualified to avoid the FFXIVClientStructs Achievement type.
    /// </summary>
    private void IndexAchievementRewards()
    {
        try
        {
            foreach (var achievement in data.GetExcelSheet<Lumina.Excel.Sheets.Achievement>())
            {
                var itemId = achievement.Item.RowId;
                if (itemId == 0)
                    continue;

                var name = achievement.Name.ExtractText().Trim();

                Add(itemId, new SourceEntry(
                    "Achievement",
                    string.IsNullOrWhiteSpace(name) ? "Achievement Reward" : name,
                    Priority: 20));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index achievement rewards.");
        }
    }

    /// <summary>
    /// GatheringItem maps gathering-sheet IDs to real Item IDs.
    /// GatheringPointBase then tells us which gathering type can produce them.
    /// </summary>
    private void IndexGathering()
    {
        try
        {
            var gatheringItems = data.GetExcelSheet<GatheringItem>()
                .Where(x => x.RowId != 0 && x.Item.RowId != 0)
                .ToDictionary(x => x.RowId, x => x.Item.RowId);

            // GatheringPointBase owns the item table, while GatheringPoint owns
            // the actual named map locations. Join them once up front so Mining,
            // Botany, Logging and Quarrying entries can be actionable rather
            // than merely naming the gathering method.
            var locationsByBase = new Dictionary<uint, HashSet<string>>();

            foreach (var point in data.GetExcelSheet<GatheringPoint>())
            {
                var baseId = ReflectionUtil.GetNestedRowId(
                    point,
                    "GatheringPointBase",
                    "Base");

                if (baseId == 0)
                    continue;

                var placeNameId = ReflectionUtil.GetNestedRowId(
                    point,
                    "PlaceName");

                var placeName = RowName<PlaceName>(
                    placeNameId,
                    "Name");

                if (string.IsNullOrWhiteSpace(placeName))
                    continue;

                if (!locationsByBase.TryGetValue(baseId, out var names))
                {
                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    locationsByBase[baseId] = names;
                }

                names.Add(placeName);
            }

            foreach (var point in data.GetExcelSheet<GatheringPointBase>())
            {
                string method = "Gathering";

                try
                {
                    if (point.GatheringType.IsValid)
                    {
                        var name = point.GatheringType.Value.Name.ExtractText().Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            method = name;
                    }
                }
                catch
                {
                    // "Gathering" is still truthful if the method name cannot be read.
                }

                locationsByBase.TryGetValue(point.RowId, out var locations);

                foreach (var gatheringItemRef in point.Item)
                {
                    var gatheringItemId = gatheringItemRef.RowId;

                    if (gatheringItemId == 0 ||
                        !gatheringItems.TryGetValue(gatheringItemId, out var itemId))
                        continue;

                    if (locations is { Count: > 0 })
                    {
                        foreach (var location in locations.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        {
                            Add(itemId, new SourceEntry(
                                "Gathering",
                                $"{method}: {location}",
                                Priority: 42));
                        }
                    }
                    else
                    {
                        Add(itemId, new SourceEntry(
                            "Gathering",
                            method,
                            Priority: 45));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index gathering sources.");
        }
    }

    /// <summary>
    /// FishingSpot stores up to ten Item references directly and includes the
    /// named fishing hole. SpearfishingItem links to SpearfishingNotebook,
    /// which provides the named spearfishing location. These are authoritative
    /// live-game relationships and therefore should replace the old broad
    /// "Fishing / Spearfishing" fallback whenever present.
    /// </summary>
    private void IndexFishingSpots()
    {
        try
        {
            foreach (var spot in data.GetExcelSheet<FishingSpot>())
            {
                var placeId =
                    ReflectionUtil.GetNestedRowId(spot, "PlaceName") != 0
                        ? ReflectionUtil.GetNestedRowId(spot, "PlaceName")
                        : ReflectionUtil.GetNestedRowId(spot, "PlaceNameSub", "PlaceNameMain");

                var place = RowName<PlaceName>(placeId, "Name");

                if (string.IsNullOrWhiteSpace(place))
                    continue;

                var level = ReflectionUtil.GetNumericProperty(spot, "GatheringLevel") ?? 0;
                var detail = level > 0 ? $"Fishing hole Lv. {level}" : "Fishing hole";

                var itemRefs = ReflectionUtil.GetPropertyValue(spot, "Item");
                if (itemRefs is not System.Collections.IEnumerable enumerable)
                    continue;

                foreach (var itemRef in enumerable)
                {
                    var itemId = ReflectionUtil.ExtractRowId(itemRef);
                    if (itemId == 0 || !itemsById.ContainsKey(itemId))
                        continue;

                    Add(itemId, new SourceEntry(
                        "Fishing",
                        $"Fishing: {place}",
                        detail,
                        Priority: 42));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index named fishing spots.");
        }

        try
        {
            foreach (var spear in data.GetExcelSheet<SpearfishingItem>())
            {
                var itemId = ReflectionUtil.GetNestedRowId(spear, "Item");
                if (itemId == 0 || !itemsById.ContainsKey(itemId))
                    continue;

                var notebookId = ReflectionUtil.GetNestedRowId(
                    spear,
                    "SpearfishingNotebook");

                if (notebookId == 0)
                    continue;

                var notebook = data.GetExcelSheet<SpearfishingNotebook>().GetRow(notebookId);
                var placeId = ReflectionUtil.GetNestedRowId(notebook, "PlaceName");
                var place = RowName<PlaceName>(placeId, "Name");

                if (string.IsNullOrWhiteSpace(place))
                    continue;

                var level = ReflectionUtil.GetNumericProperty(notebook, "GatheringLevel") ?? 0;
                var detail = level > 0 ? $"Spearfishing Lv. {level}" : "Spearfishing";

                Add(itemId, new SourceEntry(
                    "Fishing",
                    $"Spearfishing: {place}",
                    detail,
                    Priority: 42));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index named spearfishing locations.");
        }
    }

    /// <summary>
    /// RetainerTaskNormal provides an authoritative item relation while
    /// RetainerTask supplies the venture's job family and required retainer
    /// level.  Index both the linked normal-task row and the RetainerTask row
    /// so LuminaSupplemental RetainerVentureItem records can be named too.
    /// </summary>
    private void IndexRetainerVentures()
    {
        try
        {
            var normalItems = new Dictionary<uint, uint>();

            foreach (var normal in data.GetExcelSheet<RetainerTaskNormal>())
            {
                if (normal.RowId == 0)
                    continue;

                var itemId = ReflectionUtil.GetNestedRowId(normal, "Item");
                if (itemId != 0)
                    normalItems[normal.RowId] = itemId;
            }

            foreach (var task in data.GetExcelSheet<RetainerTask>())
            {
                if (task.RowId == 0)
                    continue;

                var linkedTaskId = ReflectionUtil.GetNestedRowId(task, "Task");
                if (linkedTaskId == 0)
                    continue;

                var isRandom = ReflectionUtil.GetBoolProperty(task, "IsRandom") == true;
                string? label;

                if (isRandom)
                {
                    label = RowName<RetainerTaskRandom>(linkedTaskId, "Name");
                    if (string.IsNullOrWhiteSpace(label))
                        label = "Quick Exploration";
                }
                else
                {
                    label = BuildRetainerVentureLabel(task);
                }

                if (!string.IsNullOrWhiteSpace(label))
                {
                    retainerVentureLabelsById[task.RowId] = label;
                    retainerVentureLabelsById[linkedTaskId] = label;
                }

                if (!isRandom &&
                    normalItems.TryGetValue(linkedTaskId, out var itemId) &&
                    itemsById.ContainsKey(itemId))
                {
                    Add(itemId, new SourceEntry(
                        "Retainer Venture",
                        $"Retainer Venture: {label ?? "Venture"}",
                        "Live retainer venture task",
                        Priority: 45,
                        SpecificityHint: "Named"));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index named retainer ventures.");
        }
    }

    private string BuildRetainerVentureLabel(RetainerTask task)
    {
        var categoryId = ReflectionUtil.GetNestedRowId(task, "ClassJobCategory");
        var categoryName = RowName<ClassJobCategory>(categoryId, "Name") ?? string.Empty;
        var level = ReflectionUtil.GetNumericProperty(task, "RetainerLevel") ?? 0;

        var family =
            categoryName.Contains("Miner", StringComparison.OrdinalIgnoreCase) ? "Mining" :
            categoryName.Contains("Botanist", StringComparison.OrdinalIgnoreCase) ? "Botany" :
            categoryName.Contains("Fisher", StringComparison.OrdinalIgnoreCase) ? "Fishing" :
            "Hunting";

        return level > 0
            ? $"{family} Venture Lv. {level}"
            : $"{family} Venture";
    }

    /// <summary>
    /// Battlecraft / tradecraft / fieldcraft leve rewards.
    ///
    /// Many ARR "Aetherial" equipment rows come through the Leve reward tables
    /// rather than normal dungeon/shop/quest relationships.  The relationship
    /// is:
    ///
    /// Leve -> LeveRewardItem -> LeveRewardItemGroup -> Item
    /// </summary>
    private void IndexLeveRewards()
    {
        try
        {
            var rewardItems = data.GetExcelSheet<LeveRewardItem>();
            var rewardGroups = data.GetExcelSheet<LeveRewardItemGroup>();

            foreach (var leve in data.GetExcelSheet<Leve>())
            {
                if (!leve.LeveRewardItem.IsValid)
                    continue;

                var leveName = leve.Name.ExtractText().Trim();
                if (string.IsNullOrWhiteSpace(leveName))
                    leveName = $"Leve #{leve.RowId}";

                var rewardRowId = leve.LeveRewardItem.RowId;

                if (!rewardItems.TryGetRow(rewardRowId, out var rewardItem))
                    continue;

                foreach (var groupRef in rewardItem.LeveRewardItemGroup)
                {
                    if (!groupRef.IsValid || groupRef.RowId == 0)
                        continue;

                    if (!rewardGroups.TryGetRow(groupRef.RowId, out var group))
                        continue;

                    foreach (var itemRef in group.Item)
                    {
                        var itemId = itemRef.RowId;

                        if (itemId == 0 || !itemsById.ContainsKey(itemId))
                            continue;

                        Add(itemId, new SourceEntry(
                            "Levequest",
                            $"Levequest: {leveName}",
                            leve.ClassJobLevel > 0
                                ? $"Lv. {leve.ClassJobLevel}"
                                : null,
                            Priority: 25));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index levequest rewards.");
        }
    }

    /// <summary>
    /// Broad acquisition-system fallbacks for item families whose exact
    /// per-item step is not represented cleanly in retail Excel sheets.
    ///
    /// These are deliberately low-priority.  If a recipe/shop/quest/duty or
    /// community source exists, that exact route remains above this line.
    ///
    /// The rules below are limited to names that unambiguously belong to one
    /// progression system; no generic "looks similar" guessing is used.
    /// </summary>
    private void IndexKnownSystemFallbacks()
    {
        foreach (var (itemId, item) in itemsById)
        {
            var name = item.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            SourceEntry? source = null;

            // Exact exchange-token acquisition routes that are not reliably
            // exposed by the retail reverse sheets. These are terminal, named
            // activities rather than vague system labels.
            if (name.Equals("Clouddark Demimateria I", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Clouddark Demimateria II", StringComparison.OrdinalIgnoreCase))
            {
                AddFallback(itemId, new SourceEntry(
                    "Duty",
                    "The Cloud of Darkness (Chaotic)",
                    "Chaotic alliance raid reward.",
                    Priority: 35,
                    SpecificityHint: "Named"));
                continue;
            }

            if (name.StartsWith("AAC Illustrated: CW Edition ", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = name["AAC Illustrated: CW Edition ".Length..].Trim();
                var duty = suffix switch
                {
                    "I" => "AAC Cruiserweight M1 (Savage)",
                    "II" => "AAC Cruiserweight M2 (Savage)",
                    "III" => "AAC Cruiserweight M3 (Savage)",
                    "IV" => "AAC Cruiserweight M4 (Savage)",
                    _ => string.Empty,
                };
                if (!string.IsNullOrWhiteSpace(duty))
                {
                    AddFallback(itemId, new SourceEntry(
                        "Duty", duty, "Savage raid token.", Priority: 35, SpecificityHint: "Named"));
                    continue;
                }
            }

            if (name.Equals("Gelmorran Potsherd", StringComparison.OrdinalIgnoreCase))
            {
                AddFallback(itemId, new SourceEntry(
                    "Deep Dungeon", "The Palace of the Dead",
                    "Obtained while progressing through the Palace of the Dead.",
                    Priority: 45, SpecificityHint: "Named"));
                continue;
            }

            if (name.Equals("Rathalos Scale", StringComparison.OrdinalIgnoreCase))
            {
                AddFallback(itemId, new SourceEntry(
                    "Trial", "The Great Hunt", "Rathalos material reward.",
                    Priority: 35, SpecificityHint: "Named"));
                AddFallback(itemId, new SourceEntry(
                    "Trial", "The Great Hunt (Extreme)", "Rathalos material reward.",
                    Priority: 35, SpecificityHint: "Named"));
                continue;
            }

            if (name.Equals("Rathalos Scale+", StringComparison.OrdinalIgnoreCase))
            {
                AddFallback(itemId, new SourceEntry(
                    "Trial", "The Great Hunt (Extreme)", "Rare Rathalos material reward.",
                    Priority: 35, SpecificityHint: "Named"));
                continue;
            }

            if (name.Equals("Rokkon Potsherd", StringComparison.OrdinalIgnoreCase))
            {
                AddFallback(itemId, new SourceEntry(
                    "Variant Dungeon", "Mount Rokkon", "Variant dungeon reward.",
                    Priority: 40, SpecificityHint: "Named"));
                continue;
            }

            // Several relic/event weapon stages use unique names with no
            // textual stage marker. Their Item IDs are contiguous families in
            // the live game data, so resolve those explicitly.
            if ((source = BuildExactRelicSource(itemId, name)) is not null)
            {
                // Exact stage-level quest mapping.  Provenance remains curated,
                // but SpecificityHint marks the route as named/actionable.
            }
            else if (IsZodiacWeaponId(itemId))
            {
                source = new SourceEntry(
                    "Relic",
                    "Zodiac weapon questline",
                    "ARR Zodiac weapon progression",
                    Priority: 70);
            }
            else if (IsAnimaWeaponId(itemId))
            {
                source = new SourceEntry(
                    "Relic",
                    "Anima weapon questline",
                    "Heavensward Anima weapon progression",
                    Priority: 70);
            }
            else if (IsEurekaWeaponId(itemId))
            {
                source = new SourceEntry(
                    "Relic",
                    "Eureka weapon progression",
                    "The Forbidden Land, Eureka",
                    Priority: 70);
            }
            else if (IsResistanceWeaponId(itemId))
            {
                source = new SourceEntry(
                    "Relic",
                    "Resistance weapon questline",
                    "Bozjan Southern Front / Zadnor progression",
                    Priority: 70);
            }
            else if (IsYokaiWeaponId(itemId))
            {
                source = new SourceEntry(
                    "Event",
                    "Yo-kai Watch collaboration",
                    "Limited-time collaboration event; availability depends on event recurrence.",
                    Priority: 80);
            }
            else if (IsPvpSeriesFramersKit(name))
            {
                source = new SourceEntry(
                    "PvP",
                    "PvP Series Malmstones",
                    "Series reward; older series rewards may rotate to the Crystal Quartermaster.",
                    Priority: 70);
            }
            else if (IsRankedPvpFramersKit(name))
            {
                source = new SourceEntry(
                    "PvP",
                    "Crystalline Conflict ranked season rewards",
                    "Ranked/tier Framer's Kit reward",
                    Priority: 70);
            }
            else if (name.Equals("Lauded Framer's Kit", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Out-of-game Contest / Sweepstakes",
                    "Official FFXIV community contest",
                    "No normal in-game acquisition. Availability depends on future official contests/sweepstakes.",
                    Priority: 75,
                    SpecificityHint: "Named");
            }
            else if (name.Equals("Ten Year Anniversary Framer's Kit", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Event",
                    "Moogle Treasure Trove",
                    "Limited-time event reward",
                    Priority: 75);
            }
            else if (IsTripleTriadTournamentPack(name))
            {
                source = new SourceEntry(
                    "Gold Saucer",
                    "Triple Triad Tournaments",
                    "Tournament ranking / tournament-point reward",
                    Priority: 65,
                    SpecificityHint: "Named");
            }
            else if (IsLegacyFeastReward(name))
            {
                source = new SourceEntry(
                    "PvP",
                    "Seasonal Quartermaster - Wolves' Den Pier",
                    "Legacy Feast ranked-season voucher reward",
                    Priority: 75,
                    SpecificityHint: "Named");
            }
            else if (IsMoogleTreasureTroveReward(name))
            {
                source = new SourceEntry(
                    "Event",
                    "Moogle Treasure Trove / Itinerant Moogle",
                    "Limited-time event reward; availability varies by Treasure Trove rotation.",
                    Priority: 75,
                    SpecificityHint: "Named");
            }
            else if (IsCommunityContestPrize(name))
            {
                source = new SourceEntry(
                    "Out-of-game Contest / Sweepstakes",
                    "Official FFXIV community contest / sweepstakes",
                    "No normal in-game acquisition. Prize may return in future official promotions.",
                    Priority: 80,
                    SpecificityHint: "Named");
            }
            else if (name.Equals("Friendship Circlet", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Recruit a Friend",
                    "Recruit a Friend Campaign",
                    Priority: 65,
                    SpecificityHint: "Named");
            }
            else if (name.Equals("Mad Bird Ring", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Gold Saucer",
                    "Jumbo Cactpot",
                    "1st Prize reward",
                    Priority: 65,
                    SpecificityHint: "Named");
            }
            else if (name is "Great Shin-Zantetsuken" or "Shin-Zantetsuken" or "Damascene Cloth")
            {
                source = new SourceEntry(
                    "Field Operation",
                    "The Forbidden Land, Eureka Hydatos / The Baldesion Arsenal",
                    "Baldesion Arsenal reward/progression item",
                    Priority: 55,
                    SpecificityHint: "Named");
            }
            else if (name.Equals("Plundered Round Shield", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Retainer Venture",
                    "Quick Exploration",
                    "Retainer venture reward",
                    Priority: 65,
                    SpecificityHint: "Named");
            }
            else if (name.Equals("Coffer o' Kupos", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Ishgardian Restoration",
                    "Kupo of Fortune",
                    "Firmament Kupo of Fortune reward",
                    Priority: 55,
                    SpecificityHint: "Named");
            }
            else if (name.Equals("Powersuit Coffer", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Cosmic Exploration",
                    "Cosmic Fortune - Oizys",
                    Priority: 55,
                    SpecificityHint: "Named");
            }
            else if (name.Equals("Commendation Crystal", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "PvP",
                    "Crystalline Conflict ranked season rewards",
                    "Ranking/season standing reward",
                    Priority: 60);
            }
            else if (name.Equals("Blood-spattered Mark Log", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Hunt",
                    "The Hunt",
                    "Hunt reward / exchange item",
                    Priority: 70);
            }
            else if (name.Equals("Powdered Sugar", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Seasonal Event",
                    "Recompense Officer / legacy seasonal rewards",
                    "Exchange material from early seasonal events",
                    Priority: 70);
            }
            else if (name is "Corvosi Potsherd" or "Corvosi Crystal" or "Corvosi Fan" or "Corvosi Sofa")
            {
                source = new SourceEntry(
                    "Exploration",
                    "The Merchant's Tale",
                    "Corvos exploration reward",
                    Priority: 65,
                    SpecificityHint: "Named");
            }
            else if (name.StartsWith("Notes on ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Phantom Dispeller ", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Enlightenment Silver", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Enlightenment Gold", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(" Soul Shard", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(" Demiatma", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Occult Crescent",
                    "The Occult Crescent",
                    "Critical encounter / expedition progression reward",
                    Priority: 70);
            }
            else if (name.StartsWith("Ornate ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Wondrous Tails",
                    "Khloe Aliapoh — Gold Certificate of Commendation exchange",
                    "Wondrous Tails — Idyllshire",
                    Priority: 70,
                    SpecificityHint: "Named");
            }
            else if (name.StartsWith("Antiquated ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Job Progression",
                    "Heavensward job artifact gear — level 60 job quest / Calamity Salvager",
                    "Antiquated artifact equipment; originally awarded through Heavensward job progression and recoverable through the Calamity Salvager where applicable.",
                    Priority: 85,
                    SpecificityHint: "Named");
            }
            else if (name.StartsWith("Cosmic ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Stellar ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Cosmic Exploration",
                    "Cosmic Exploration",
                    Priority: 85);
            }
            else if (name.StartsWith("Hydatos ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Pyros ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Elemental ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Eureka",
                    "The Forbidden Land, Eureka",
                    "Eureka weapon/armor progression",
                    Priority: 85);
            }
            else if (name.StartsWith("Mandervillous ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Majestic Manderville ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Amazing Manderville ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Relic",
                    "Manderville weapon questline",
                    Priority: 85);
            }
            else if (IsPhantomWeaponStage(name))
            {
                source = new SourceEntry(
                    "Relic",
                    "Phantom weapon questline",
                    "Dawntrail relic progression / The Occult Crescent",
                    Priority: 80);
            }
            else if (name.StartsWith("Law's Order ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Augmented Law's Order ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Blade's ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Relic",
                    "Resistance weapon questline",
                    "Bozjan Southern Front / Zadnor progression",
                    Priority: 85);
            }
            else if (name.StartsWith("Animated ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Hyperconductive ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Sharpened ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Relic",
                    "Anima weapon questline",
                    "Heavensward Anima weapon progression",
                    Priority: 85);
            }
            else if (name.StartsWith("Resplendent ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Crafting Progression",
                    "Resplendent tool progression",
                    Priority: 85);
            }
            else if (IsSkysteelTool(item, name))
            {
                source = new SourceEntry(
                    "Relic Tool",
                    "Skysteel tool questline",
                    "Ishgardian Restoration / Skysteel progression",
                    Priority: 85);
            }
            else if (IsSplendorousTool(item, name))
            {
                source = new SourceEntry(
                    "Relic Tool",
                    "Splendorous tool questline",
                    "Crystarium Splendorous tool progression",
                    Priority: 85);
            }
            else if (name.StartsWith("Island ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Island Sanctuary",
                    "Island Sanctuary",
                    Priority: 85);
            }
            else if (IsZodiacWeaponStage(name))
            {
                source = new SourceEntry(
                    "Relic",
                    "Zodiac weapon questline",
                    "ARR Zodiac weapon progression",
                    Priority: 80);
            }
            else if (IsAnimaWeaponStage(name))
            {
                source = new SourceEntry(
                    "Relic",
                    "Anima weapon questline",
                    "Heavensward Anima weapon progression",
                    Priority: 80);
            }
            else if (IsEurekaWeaponStage(name))
            {
                source = new SourceEntry(
                    "Relic",
                    "Eureka weapon progression",
                    "The Forbidden Land, Eureka",
                    Priority: 80);
            }
            else if (name.EndsWith(" Recollection", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Relic",
                    "Resistance weapon questline",
                    "Bozjan Southern Front / Zadnor progression",
                    Priority: 80);
            }
            else if (name.StartsWith("Unfinished ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Relic",
                    "Zodiac weapon questline",
                    "ARR relic weapon progression",
                    Priority: 85);
            }
            else if (name.StartsWith("Aetherial ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "ARR Equipment",
                    "ARR Aetherial equipment — dungeon / leve reward system",
                    "Aetherial equipment comes from ARR-era dungeon/chest and leve reward pools; the exact individual duty/leve remains in the exact-source backlog.",
                    Priority: 90,
                    SpecificityHint: "Named");
            }
            else if (name.StartsWith("Timeworn ", StringComparison.OrdinalIgnoreCase) &&
                     name.EndsWith(" Map", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Gathering",
                    "Timeworn Map Gathering",
                    "Gathered from level-appropriate Miner/Botanist/Fisher nodes; exact node is not resolved yet.",
                    Priority: 85);
            }
            else if ((name.StartsWith("Fledgling Chocobo Registration ", StringComparison.OrdinalIgnoreCase) ||
                      name.StartsWith("Retired Chocobo Registration ", StringComparison.OrdinalIgnoreCase)))
            {
                source = new SourceEntry(
                    "Gold Saucer",
                    "Chocobo Racing / Breeding",
                    Priority: 85);
            }
            else if (IsCosmicTool(item, name))
            {
                source = new SourceEntry(
                    "Cosmic Exploration",
                    "Cosmic Exploration tool progression",
                    Priority: 80);
            }
            else if (IsVeteranRewardAttire(name))
            {
                source = new SourceEntry(
                    "Veteran Reward",
                    "Veteran Reward",
                    VeteranRewardDetail(name),
                    Priority: 20);
            }
            else if (name.StartsWith("Star Pilot ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Cosmic Exploration",
                    "Cosmic Fortune - Phaenna",
                    "Star Pilot Attire Coffer reward",
                    Priority: 35);
            }
            else if (name.StartsWith("Cosmosuit ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Cosmic Exploration",
                    "Cosmic Exploration",
                    "Cosmosuit Coffer reward",
                    Priority: 35);
            }
            else if (name.StartsWith("Rule-breaker's ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "PvP",
                    "PvP Series 11",
                    "Level 25 series reward",
                    Priority: 35);
            }
            else if (name.StartsWith("Brand-new ", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Brand-new Ring", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Training",
                    "Hall of the Novice",
                    "Role training reward",
                    Priority: 25);
            }
            else if (name.StartsWith("Forgotten Fragment of ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Field Notes on ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Field Operation",
                    "Bozjan Southern Front / Zadnor",
                    "Field-operation reward/drop",
                    Priority: 80);
            }
            else if (name.StartsWith("Irregular Tomestone ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Event",
                    "Moogle Treasure Trove",
                    "Event-specific currency; availability varies by event.",
                    Priority: 85);
            }
            else if (name.StartsWith("Sphere Scroll:", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Relic",
                    "Zodiac weapon questline",
                    "Novus weapon progression",
                    Priority: 80);
            }
            else if (name.StartsWith("Oddly Delicate ", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Highly Viscous ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Relic Tool",
                    "Skysteel tool questline",
                    "Skysteel tool upgrade material",
                    Priority: 80);
            }
            else if (name.StartsWith("Oddly Specific ", StringComparison.OrdinalIgnoreCase))
            {
                source = new SourceEntry(
                    "Relic Tool",
                    "Skysteel tool questline",
                    "Skysteel tool crafting/gathering material",
                    Priority: 80);
            }
            else if (IsContentToken(item, name))
            {
                source = new SourceEntry(
                    "Currency",
                    "Content Currency / Exchange Token",
                    "Earned from the associated content, raid, trial, event, or exchange system.",
                    Priority: 92);
            }

            if (source is not null)
                AddFallback(itemId, source);
        }
    }

    private static bool IsPvpSeriesFramersKit(string name)
    {
        string[] names =
        [
            "Archeia Harmonias Framer's Kit",
            "Bayside Battleground Framer's Kit",
            "Borderland Ruins (Secure) Framer's Kit",
            "Clockwork Castletown Framer's Kit",
            "Fields of Glory Framer's Kit",
            "Hidden Gorge Framer's Kit",
            "Immortal Flames Framer's Kit",
            "Maelstrom Framer's Kit",
            "Onsal Hakair Framer's Kit",
            "Order of the Twin Adder Framer's Kit",
            "Red Sands Framer's Kit",
            "Seal Rock Framer's Kit",
            "Volcanic Heart Framer's Kit",
            "Worqor Chirteh Framer's Kit",
        ];

        return names.Any(x => string.Equals(name, x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRankedPvpFramersKit(string name)
    {
        string[] names =
        [
            "Bronze Framer's Kit", "Silver Framer's Kit", "Gold Framer's Kit",
            "Platinum Framer's Kit", "Diamond Framer's Kit", "Crystal Framer's Kit",
            "Endless Conflict Framer's Kit", "Final Conflict Framer's Kit",
            "Rising Conflict Framer's Kit",
        ];

        return names.Any(x => string.Equals(name, x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTripleTriadTournamentPack(string name)
    {
        return name.Equals("Platinum Triad Card", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Tournament of Champions Card", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Spinner's Pull Card", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Rowena Cup Classic Card", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Durai Memorial Card", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyFeastReward(string name)
    {
        string[] names =
        [
            "Grey Hound Armor Coffer", "Gunro Armor Coffer", "Koro Armor Coffer",
            "Lone Faehound Earring", "Pack Faehound Earring",
            "Lone Hellhound Earring", "Pack Hellhound Earring",
        ];

        return names.Any(x => string.Equals(name, x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMoogleTreasureTroveReward(string name)
    {
        string[] names =
        [
            "Mameshiba Earring", "Namazu Earring", "Namazu Neckerchief",
            "Porxie Earrings", "Buuz Earring", "Coffee Cup Earring",
            "Donut Earring", "Demon Brick Earring", "Emerald Carbuncle Earring",
            "Paissa Earring", "Inferno Jacket",
        ];

        return names.Any(x => string.Equals(name, x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCommunityContestPrize(string name) =>
        SpecialAcquisitionCatalog.IsContestOrSweepstakesPrize(name) &&
        !name.Equals("Lauded Framer's Kit", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Wrapped Present", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("Housing Design Award MMXXV Plaque", StringComparison.OrdinalIgnoreCase);

    private static bool IsZodiacWeaponId(uint itemId) =>
        itemId >= 9491 && itemId <= 9501;

    private static bool IsAnimaWeaponId(uint itemId) =>
        (itemId >= 13223 && itemId <= 13236) ||
        (itemId >= 15251 && itemId <= 15264);

    private static bool IsEurekaWeaponId(uint itemId) =>
        (itemId >= 22941 && itemId <= 22956) ||
        (itemId >= 24675 && itemId <= 24690);

    private static bool IsResistanceWeaponId(uint itemId) =>
        (itemId >= 30228 && itemId <= 30245) ||
        (itemId >= 30767 && itemId <= 30784);

    private static SourceEntry? BuildExactRelicSource(uint itemId, string name)
    {
        static SourceEntry NamedRelic(string quest, string detail) =>
            new(
                "Relic",
                $"Quest: {quest}",
                detail,
                Priority: 70,
                SpecificityHint: "Named");

        // Heavensward Anima stages.
        if (name.StartsWith("Animated ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("Soul without Life", "Animated Anima weapon stage — Ardashir / Azys Lla");

        if (name.EndsWith(" Awoken", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("Toughening Up", "Awoken Anima weapon stage");

        if (itemId >= 13223 && itemId <= 13236)
            return NamedRelic("Coming into Its Own", "Anima weapon stage");

        if (name.StartsWith("Hyperconductive ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("Finding Your Voice", "Hyperconductive Anima weapon stage");

        if (itemId >= 15223 && itemId <= 15236)
            return NamedRelic("A Dream Fulfilled", "Reconditioned Anima weapon stage");

        if (name.StartsWith("Sharpened ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("Future Proof", "Sharpened Anima weapon stage");

        if (itemId >= 15251 && itemId <= 15264)
            return NamedRelic("Born Again Anima", "Complete Anima weapon stage");

        if (name.EndsWith(" Lux", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("Best Friends Forever", "Anima Lux weapon stage");

        // Shadowbringers Resistance stages.
        if (itemId >= 30228 && itemId <= 30245)
            return NamedRelic(
                "Fire in the Forge / Resistance Is (Not) Futile",
                "Base Resistance weapon — first/repeat acquisition");

        if (itemId >= 30767 && itemId <= 30784)
            return NamedRelic("For Want of a Memory", "Augmented Resistance weapon stage");

        if (name.EndsWith(" Recollection", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("The Will to Resist", "Resistance Recollection stage");

        if (name.StartsWith("Augmented Law's Order ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("A New Path of Resistance", "Augmented Law's Order stage");

        if (name.StartsWith("Law's Order ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("Change of Arms", "Law's Order stage");

        if (name.StartsWith("Blade's ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic("Irresistible", "Blade's Resistance weapon stage");

        // Endwalker Manderville stages.
        if (itemId >= 38400 && itemId <= 38419)
            return NamedRelic(
                "Make It a Manderville / Make Another Manderville",
                "Base Manderville weapon — first/repeat acquisition");

        if (name.StartsWith("Amazing Manderville ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic(
                "Well-oiled / The Next Mander-level",
                "Amazing Manderville weapon stage");

        if (name.StartsWith("Majestic Manderville ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic(
                "A Spirited Reforging / In Need of Adjustment",
                "Majestic Manderville weapon stage");

        if (name.StartsWith("Mandervillous ", StringComparison.OrdinalIgnoreCase))
            return NamedRelic(
                "Resonating with Perfection / Positively Mandervillous",
                "Mandervillous weapon stage");

        return null;
    }

    private static bool IsYokaiWeaponId(uint itemId) =>
        // Original Yo-kai weapon block ends at 15221. 15223-15236 are
        // Reconditioned Anima weapons and must never be classified as event gear.
        (itemId >= 15208 && itemId <= 15221) ||
        // 2020 recurrence added RDM/GNB/DNC weapons outside the original block.
        itemId is 30808 or 30809 or 30810;

    private static uint targetFilterGroup(Item item) =>
        (uint?)ReflectionUtil.GetNumericProperty(item, "FilterGroup") ?? 0;

    private static bool IsContentToken(Item item, string name)
    {
        // Only apply this generic rule to non-equipment inventory categories.
        // It is deliberately keyword-based but restricted enough to avoid
        // weapon/armor false positives.
        if (IsToolItem(item))
            return false;

        string[] keywords =
        [
            " Token",
            "Token of ",
            " Certificate",
            "Certificate of ",
            " Tomestone",
            "Tomestone of ",
            " Totem",
            "Totem of ",
            " Voucher",
            "Voucher for ",
            " Coin",
            "Coin of ",
            " Seal",
            "Seal of ",
            " Credit",
            "Credit of ",
            " Insignia",
            " Medal",
        ];

        return keywords.Any(keyword =>
            name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVeteranRewardAttire(string name)
    {
        return name.StartsWith("Strife ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Tantalus ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Wild Rose ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Leonhart ", StringComparison.OrdinalIgnoreCase);
    }

    private static string VeteranRewardDetail(string name)
    {
        if (name.StartsWith("Strife ", StringComparison.OrdinalIgnoreCase))
            return "Rank 1 - 60 subscription days";

        if (name.StartsWith("Tantalus ", StringComparison.OrdinalIgnoreCase))
            return "Rank 2 - 150 subscription days";

        if (name.StartsWith("Wild Rose ", StringComparison.OrdinalIgnoreCase))
            return "Rank 3 - 240 subscription days";

        if (name.StartsWith("Leonhart ", StringComparison.OrdinalIgnoreCase))
            return "Rank 4 - 330 subscription days";

        return "Subscription-time reward";
    }

    private static bool IsPhantomWeaponStage(string name)
    {
        if (!name.StartsWith("Phantom ", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Replica", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] stages =
        [
            " Penumbrae",
            " Umbrae",
            " Obscurum",
            " Eclipticum",
            " Occultum",
        ];

        return stages.Any(stage =>
            name.EndsWith(stage, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsZodiacWeaponStage(string name)
    {
        string[] suffixes =
        [
            " Atma",
            " Animus",
            " Novus",
            " Nexus",
            " Zeta",
        ];

        return suffixes.Any(suffix =>
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAnimaWeaponStage(string name)
    {
        return name.EndsWith(" Awoken", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(" Lux", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEurekaWeaponStage(string name)
    {
        string[] suffixes =
        [
            " Anemos",
            " Pagos",
            " Eureka",
            " Physeos",
        ];

        return suffixes.Any(suffix =>
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUiCategory(Item item, string expected)
    {
        try
        {
            if (!item.ItemUICategory.IsValid)
                return false;

            var category = item.ItemUICategory.Value.Name.ExtractText().Trim();
            return string.Equals(category, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSoulCrystal(Item item) =>
        IsUiCategory(item, "Soul Crystal");

    private static bool IsDye(Item item) =>
        IsUiCategory(item, "Dye");

    private static bool IsGardeningItem(Item item) =>
        IsUiCategory(item, "Gardening");

    private static bool IsMateria(Item item) =>
        IsUiCategory(item, "Materia");

    private static bool IsSeasonalMiscellany(Item item) =>
        IsUiCategory(item, "Seasonal Miscellany");

    private static bool IsCurrencyItem(Item item) =>
        IsUiCategory(item, "Currency");

    private static bool IsCosmicTool(Item item, string name)
    {
        if (!IsToolItem(item))
            return false;

        return name.Contains("Hyper", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(" of Stars", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSkysteelTool(Item item, string name)
    {
        if (!IsToolItem(item))
            return false;

        string[] prefixes =
        [
            "Dragonsung ",
            "Augmented Dragonsung ",
            "Skysung ",
            "Skybuilders' ",
        ];

        return prefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSplendorousTool(Item item, string name)
    {
        if (!IsToolItem(item))
            return false;

        string[] prefixes =
        [
            "Splendorous ",
            "Augmented Splendorous ",
            "Crystalline ",
            "Chora-Zoi's ",
            "Brilliant ",
            "Vrandtic ",
            "Lodestar ",
        ];

        return prefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsToolItem(Item item)
    {
        try
        {
            if (!item.ItemUICategory.IsValid)
                return false;

            var category = item.ItemUICategory.Value.Name.ExtractText().Trim();

            return category.Contains("Tool", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Free Company workshop crafting.
    ///
    /// CompanyCraftSequence.ResultItem is authoritative for airship/submersible
    /// parts, many housing exteriors, aetherial wheel stands, and other
    /// workshop projects that are not represented by the normal Recipe sheet.
    /// </summary>
    private void IndexCompanyCrafting()
    {
        var resolved = new HashSet<uint>();

        try
        {
            foreach (var sequence in data.GetExcelSheet<CompanyCraftSequence>())
            {
                var itemId = sequence.ResultItem.RowId;
                if (itemId == 0 || !itemsById.ContainsKey(itemId))
                    continue;

                string? detail = null;

                try
                {
                    if (sequence.CompanyCraftType.IsValid)
                    {
                        var raw = ReflectionUtil.GetPropertyValue(
                            sequence.CompanyCraftType.Value,
                            "Name");

                        var name = raw?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            detail = name;
                    }
                }
                catch
                {
                    // Type name is optional metadata.
                }

                Add(itemId, new SourceEntry(
                    "Company Workshop",
                    "Company Workshop Crafting",
                    detail,
                    Priority: 30));

                resolved.Add(itemId);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index Company Workshop crafting.");
        }

        CompanyCraftSourceItems = resolved.Count;
        log.Information(
            $"CQG CompanyCraft indexed {CompanyCraftSourceItems:N0} distinct result items.");
    }

    /// <summary>
    /// Triple Triad is unusually well-described by retail game data.
    ///
    /// TripleTriadCardResident.Acquisition is a conditional link:
    ///   2/3/12 -> ContentFinderCondition
    ///   4/5    -> FATE
    ///   6/10   -> ENpcResident
    ///   8/9    -> Item
    ///   11     -> Achievement
    ///
    /// This provider therefore resolves many cards without relying on a wiki
    /// or supplemental loot table at all.
    /// </summary>
    private void IndexTripleTriadCards()
    {
        try
        {
            var residents = data.GetExcelSheet<TripleTriadCardResident>();

            foreach (var card in data.GetExcelSheet<TripleTriadCard>())
            {
                var cardName = card.Name.ExtractText().Trim();
                if (string.IsNullOrWhiteSpace(cardName))
                    continue;

                if (!residents.TryGetRow(card.RowId, out var resident))
                    continue;

                var inventoryName = cardName.EndsWith(
                    " Card",
                    StringComparison.OrdinalIgnoreCase)
                    ? cardName
                    : $"{cardName} Card";

                if (!itemIdsByName.TryGetValue(inventoryName, out var itemIds))
                    continue;

                var acquisitionType =
                    ReflectionUtil.GetNestedRowId(resident, "AcquisitionType");

                // In case a schema version exposes the discriminator as a raw
                // numeric field rather than a RowRef.
                if (acquisitionType == 0)
                {
                    acquisitionType = (uint?)ReflectionUtil.GetNumericProperty(
                        resident,
                        "AcquisitionType",
                        "ObtainType") ?? 0;
                }

                var acquisitionId =
                    ReflectionUtil.GetNestedRowId(resident, "Acquisition");

                var questId =
                    ReflectionUtil.GetNestedRowId(resident, "Quest");

                foreach (var itemId in itemIds)
                {
                    switch (acquisitionType)
                    {
                        // Duty / raid / trial.
                        case 2:
                        case 3:
                        case 12:
                        {
                            if (cfcById.TryGetValue(acquisitionId, out var duty))
                            {
                                Add(itemId, new SourceEntry(
                                    DutyKind(duty),
                                    $"{DutyKind(duty)}: {duty.Name}",
                                    QuestRequirement(questId),
                                    IsContentUnlocked(duty.InstanceContentId),
                                    Priority: 8));
                            }

                            break;
                        }

                        // FATE.
                        case 4:
                        case 5:
                        {
                            var fateName = RowName<Fate>(acquisitionId, "Name");

                            if (!string.IsNullOrWhiteSpace(fateName))
                            {
                                Add(itemId, new SourceEntry(
                                    "FATE",
                                    $"FATE: {fateName}",
                                    QuestRequirement(questId),
                                    Priority: 20));
                            }

                            break;
                        }

                        // Triple Triad NPC.
                        case 6:
                        case 10:
                        {
                            var npcName = RowName<ENpcResident>(acquisitionId, "Singular", "Name");

                            if (!string.IsNullOrWhiteSpace(npcName))
                            {
                                Add(itemId, new SourceEntry(
                                    "Triple Triad NPC",
                                    $"Triple Triad NPC: {npcName}",
                                    QuestRequirement(questId),
                                    Priority: 20));
                            }

                            break;
                        }

                        // Card pack / item container.
                        case 8:
                        case 9:
                        {
                            var sourceItem = ItemName(acquisitionId);

                            if (!string.IsNullOrWhiteSpace(sourceItem))
                            {
                                Add(itemId, new SourceEntry(
                                    "Item",
                                    $"Item: {sourceItem}",
                                    QuestRequirement(questId),
                                    Priority: 25));
                            }

                            break;
                        }

                        // Achievement.
                        case 11:
                        {
                            var achievementName = RowName<Lumina.Excel.Sheets.Achievement>(
                                acquisitionId,
                                "Name");

                            if (!string.IsNullOrWhiteSpace(achievementName))
                            {
                                Add(itemId, new SourceEntry(
                                    "Achievement",
                                    $"Achievement: {achievementName}",
                                    QuestRequirement(questId),
                                    Priority: 15));
                            }

                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index Triple Triad acquisition sources.");
        }
    }

    private string? QuestRequirement(uint questId)
    {
        if (questId == 0)
            return null;

        var questName = RowName<Quest>(questId, "Name");
        return string.IsNullOrWhiteSpace(questName)
            ? null
            : $"Requires quest: {questName}";
    }

    private string DutyKind(ContentFinderInfo duty)
    {
        // Prefer explicit roulette/group flags because they remain useful even
        // if localized ContentType naming changes.
        if (duty.AllianceRaid)
            return "Alliance Raid";

        if (duty.NormalRaid)
            return "Normal Raid";

        if (duty.Trial)
            return "Trial";

        if (duty.QueueMaxPlayers >= 24)
            return "Alliance Raid";

        return "Duty";
    }

    private string? RowName<T>(uint rowId, params string[] propertyNames)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        if (rowId == 0)
            return null;

        try
        {
            var row = data.GetExcelSheet<T>().GetRow(rowId);

            foreach (var propertyName in propertyNames)
            {
                var raw = ReflectionUtil.GetPropertyValue(row, propertyName);
                if (raw is null)
                    continue;

                var text = raw.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch
        {
            // No row/name available.
        }

        return null;
    }

    /// <summary>
    /// True when we know a real acquisition route for this target.
    ///
    /// Market Board / player trade alone does NOT count. Truthful broad-system
    /// sources (including Fishing / Spearfishing when an exact spot is unknown)
    /// do count, because they still tell the player how the item is acquired.
    /// This method is used to color unresolved entries red in the picker.
    /// </summary>
    public bool HasDirectSource(TargetEntry target)
    {
        if (CuratedGuides.ForTarget(target) is not null)
            return true;

        if (gameSources.TryGetValue(target.ItemId, out var game) && game.Count > 0)
            return true;

        if (supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var duties) &&
            duties.Count > 0)
            return true;

        if (supplemental.ItemSources.TryGetValue(target.ItemId, out var supplementalSources) &&
            supplementalSources.Count > 0)
            return true;

        if (collectibleSources.HasSource(target.ItemId))
            return true;

        if (garlandSources.HasSource(target.ItemId))
            return true;

        if (GetBroadSystemSource(target) is not null)
            return true;

        if (curatedFallbackSources.TryGetValue(target.ItemId, out var curated) &&
            curated.Count > 0)
            return true;

        // Seafood has a known broad source family even when exact spot data has
        // not yet been resolved. Count this as resolved so it is not red.
        if (target.Category == "Fish" ||
            string.Equals(
                target.GameUiCategory,
                "Seafood",
                StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Legacy coverage predicate retained for diagnostics/backward compatibility.
    /// A "primary" source can still be truthful but generic.
    /// </summary>
    public bool HasPrimarySource(TargetEntry target)
    {
        if (CuratedGuides.ForTarget(target) is not null)
            return true;

        if (gameSources.TryGetValue(target.ItemId, out var game) && game.Count > 0)
            return true;

        if (supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var duties) &&
            duties.Count > 0)
            return true;

        if (supplemental.ItemSources.TryGetValue(target.ItemId, out var supplementalSources) &&
            supplementalSources.Count > 0)
            return true;

        return collectibleSources.HasSource(target.ItemId);
    }

    private static bool IsIncompleteShopSource(SourceEntry source)
    {
        var looksLikeShop = source.Kind.Contains("Vendor", StringComparison.OrdinalIgnoreCase) ||
                            source.Kind.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
                            source.Name.Contains("Special / Currency Shop", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeShop)
            return false;

        var name = source.Name.Trim();

        // Ordinary named vendor forms already identify the contact directly.
        if ((name.StartsWith("Vendor: ", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("Gil Vendor: ", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("Grand Company Quartermaster: ", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("Triple Triad NPC: ", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("NPC: ", StringComparison.OrdinalIgnoreCase)) &&
            name[(name.IndexOf(':') + 1)..].Trim().Length > 0)
        {
            return false;
        }

        // SpecialShop / trade-shop forms encode the contact after the final em
        // dash. Without that contact CQG still cannot answer the practical
        // "where do I go?" question.
        return !name.Contains(" — ", StringComparison.Ordinal);
    }

    private static bool IsUnresolvedDutyPlaceholder(SourceEntry source)
    {
        if (!IsDutyLike(source))
            return false;

        var name = source.Name.Trim();
        return name.Equals("Duty", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Dungeon", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Trial", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Raid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Duty Chest", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Duty Boss", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Duty Boss Chest", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Duty / Instance Drop", StringComparison.OrdinalIgnoreCase) ||
               (source.Detail?.Contains("exact duty unresolved", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (source.Detail?.Contains("conflict", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool IsActionableItemSpecificSource(SourceEntry source)
    {
        if (IsGenericSourceEntry(source) ||
            IsIncompleteShopSource(source) ||
            IsUnresolvedDutyPlaceholder(source))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when Garland can still materially improve the acquisition
    /// description. This is deliberately broader than "unresolved": named live,
    /// supplemental, or FFXIV Collect data stops enrichment, while generic vendor,
    /// gathering, mob, leve, voyage, gardening, etc. sources remain eligible.
    /// </summary>
    public bool NeedsGarlandEnrichment(TargetEntry target)
    {
        // Unreleased/future client-data rows do not have a published source yet.
        // Re-querying Garland cannot make them actionable until that changes;
        // the acquisition-source refresh remains available once a patch exposes
        // their source.
        if (SpecialAcquisitionCatalog.TryGetSource(target.Name)?.Kind.Equals(
                "Future / Unknown Availability",
                StringComparison.OrdinalIgnoreCase) == true)
            return false;

        // If Garland has already supplied a named/actionable route, there is no
        // reason to ask it for the same item again simply because a weaker live
        // provider also exposes a generic route.
        var existingGarland = garlandSources.GetSources(target.ItemId);
        if (existingGarland.Any(IsActionableItemSpecificSource))
            return false;

        var providerSources = GetNonGarlandProviderSources(target);

        // A named exchange that does not identify an NPC/location is still
        // incomplete guidance.  Garland often has the missing trade-shop NPC.
        if (providerSources.Any(IsIncompleteShopSource))
            return true;

        // Inferred supplemental duty associations are only authoritative when
        // they agree.  A direct supplemental item->CFC relation is intentionally
        // excluded from this count and can resolve the ambiguity locally.
        var supplementalDutyCount = providerSources
            .Where(source => string.Equals(
                source.SpecificityHint,
                "Supplemental duty association",
                StringComparison.OrdinalIgnoreCase))
            .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var hasAuthoritativeLocalDuty = providerSources.Any(source =>
            string.Equals(
                source.SpecificityHint,
                "Supplemental exact duty relation",
                StringComparison.OrdinalIgnoreCase));

        if (supplementalDutyCount > 1 && !hasAuthoritativeLocalDuty)
            return true;

        // Broad inferred associations should not independently satisfy this
        // quality check when they conflict.  All other actionable item-specific
        // sources, including explicit supplemental duty rows, can.
        if (providerSources.Any(source =>
                !string.Equals(
                    source.SpecificityHint,
                    "Supplemental duty association",
                    StringComparison.OrdinalIgnoreCase) &&
                IsActionableItemSpecificSource(source)))
        {
            return false;
        }

        if (supplementalDutyCount == 1)
            return false;

        // No named provider source, or only generic provider sources: Garland
        // may still be able to add an NPC, node, mob, leve, shop, route, source
        // item, etc. Negative/check-result caching prevents repeated requests.
        return true;
    }

    /// <summary>
    /// True when the currently winning acquisition information is still below
    /// named item-specific quality. Unlike NeedsGarlandEnrichment(), this is a
    /// provider-agnostic quality metric: it includes system/curated/guide-level
    /// entries even when Garland has already been checked and has nothing more.
    /// </summary>
    public bool NeedsSpecificityEnrichment(TargetEntry target)
    {
        var specificity = GetSourceSpecificity(target);
        return !specificity.StartsWith("Named ", StringComparison.OrdinalIgnoreCase) ||
               specificity.StartsWith("Named incomplete", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stronger quality predicate than the named-source backlog. Named
    /// system/curated guidance is useful and should not be treated as vague, but
    /// it still is not the same as an item-specific NPC/node/duty/quest/drop.
    /// This metric keeps the long-term "make everything exact where possible"
    /// goal visible while the named-source backlog measures everyday usability.
    /// </summary>
    public bool NeedsExactItemSpecificity(TargetEntry target)
    {
        var special = SpecialAcquisitionCatalog.TryGetSource(target.Name);
        if (special is not null &&
            !special.Kind.Equals("Future / Unknown Availability", StringComparison.OrdinalIgnoreCase) &&
            IsNamedSystemSpecificSource(special))
        {
            return false;
        }

        if (curatedFallbackSources.TryGetValue(target.ItemId, out var curated) &&
            curated.Any(IsNamedSystemSpecificSource))
        {
            return false;
        }

        return !GetSourceSpecificity(target).Equals(
            "Named item-specific",
            StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<SourceEntry> GetNonGarlandProviderSources(TargetEntry target)
    {
        var result = new List<SourceEntry>();

        if (gameSources.TryGetValue(target.ItemId, out var game))
            result.AddRange(game);

        var community = collectibleSources.GetSources(target.ItemId);
        if (community.Count > 0)
            result.AddRange(community);

        if (supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var dutyRefs))
        {
            foreach (var dutyRef in dutyRefs)
            {
                foreach (var duty in ResolveDutyReference(dutyRef))
                    result.Add(DutySource(duty, "Supplemental duty association"));
            }
        }

        if (supplemental.ItemSources.TryGetValue(target.ItemId, out var records))
        {
            foreach (var record in records)
            {
                var source = ResolveSupplemental(record);
                if (source is not null)
                    result.Add(source);
            }
        }

        return result;
    }

    private static bool IsMarketSource(SourceEntry source) =>
        source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase) &&
        source.Name.Equals("Market Board / Player Trade", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlayerFacingActionableSource(SourceEntry source)
    {
        var kind = source.Kind.Trim();
        var name = source.Name.Trim();

        // Market Board / player trade is generic data-wise, but it is still a
        // concrete acquisition action and is therefore valid player guidance.
        if (kind.Equals("Market", StringComparison.OrdinalIgnoreCase) &&
            name.Equals("Market Board / Player Trade", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Internal resolver relationships are useful to diagnostics only. They
        // do not answer where to go, what to run, what to exchange, or what to do.
        if (kind.Equals("Item Conversion", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Item conversion", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Item conversion / supplemental source", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsIncompleteShopSource(source) || IsUnresolvedDutyPlaceholder(source))
            return false;

        // Generic placeholders must never win merely because a data relation exists.
        if (IsGenericSourceEntry(source))
            return false;

        // Additional broad placeholders that are intentionally kept in the raw
        // resolver graph for audits/enrichment but are not useful instructions.
        if (name.Equals("Content Currency / Exchange Token", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Content Currency / Token", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Dye acquisition", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Materia acquisition", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Seasonal / Collaboration Event", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(name);
    }

    private static SourceEntry ToPlayerFacingSource(SourceEntry source)
    {
        var detail = source.Detail;
        if (!string.IsNullOrWhiteSpace(detail) && IsInternalResolverDetail(detail))
            detail = null;

        return source with { Detail = detail };
    }

    private static bool IsInternalResolverDetail(string detail)
    {
        return detail.Contains("LuminaSupplemental", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("supplemental duty association", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("supplemental exact duty relation", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("resolver relationship", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("exact-source backlog", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericSourceEntry(SourceEntry source)
    {
        if (source.SpecificityHint?.Equals("Named", StringComparison.OrdinalIgnoreCase) == true)
            return false;
        if (source.SpecificityHint?.Equals("Generic", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        var name = source.Name.Trim();

        if (source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsUnresolvedDutyPlaceholder(source))
            return true;

        if (name.StartsWith("Vendor: ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Gathering: ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Fishing: ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Monster: ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Levequest: ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Duty: ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Aetherial Reduction: ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (source.Kind.Equals("Duty", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Duty", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Duty / Instance Drop", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (source.Kind.Equals("Quest", StringComparison.OrdinalIgnoreCase) ||
            source.Kind.Equals("Achievement", StringComparison.OrdinalIgnoreCase) ||
            source.Kind.Equals("FATE", StringComparison.OrdinalIgnoreCase) ||
            source.Kind.Equals("Activity", StringComparison.OrdinalIgnoreCase) ||
            source.Kind.Equals("Company Workshop", StringComparison.OrdinalIgnoreCase) ||
            source.Kind.Equals("Crafting", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.StartsWith("Special / Currency Shop:", StringComparison.OrdinalIgnoreCase))
            return false;

        return name.Equals("Gil Vendor", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Vendor", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Special / Currency Shop", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Grand Company Quartermaster", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Gathering", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Mining", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Botany", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Logging", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Quarrying", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Fishing / Spearfishing", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Fishing", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Monster Drop", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Levequest Reward", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Retainer Venture", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Field operation coffer", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Airship Voyage", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Submarine Voyage", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Airship / Submarine Voyage", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Gardening", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Gardening / Gathering", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Treasure Hunt", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Desynthesis", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Duty / Instance Drop", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Achievement", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Quest", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Duty", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Dungeon", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Trial", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Raid", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("FATE", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Event", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Seasonal Event", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("PvP", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Gold Saucer", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Item conversion / supplemental source", StringComparison.OrdinalIgnoreCase);
    }

    private void AddExchangeDependency(
        uint rewardItemId,
        uint costItemId,
        uint amount,
        string shopName,
        IReadOnlyList<string> npcNames)
    {
        if (rewardItemId == 0 || costItemId == 0 || rewardItemId == costItemId)
            return;

        if (!exchangeDependencies.TryGetValue(rewardItemId, out var list))
        {
            list = [];
            exchangeDependencies[rewardItemId] = list;
        }

        if (list.Any(x => x.CostItemId == costItemId && x.Amount == amount))
            return;

        list.Add(new ExchangeDependency(
            costItemId,
            amount,
            shopName,
            npcNames.ToArray()));
    }

    /// <summary>
    /// Adds the most useful known acquisition route for a token/currency used
    /// by a SpecialShop exchange.  This turns "8 Midan Manifesto - Page 4"
    /// into actionable guidance such as the raid that awards that manifesto.
    /// The dependency source is intentionally lower-priority than the direct
    /// shop itself, so CQG still says how the target is actually obtained.
    /// </summary>
    public IReadOnlyList<ExchangeDependencyInfo> GetExchangeDependencies(uint targetItemId)
    {
        if (!exchangeDependencies.TryGetValue(targetItemId, out var dependencies))
            return Array.Empty<ExchangeDependencyInfo>();

        return dependencies
            .Select(x => new ExchangeDependencyInfo(
                x.CostItemId,
                ItemName(x.CostItemId) ?? $"item {x.CostItemId}",
                x.Amount,
                x.ShopName,
                x.NpcNames))
            .ToArray();
    }

    public IReadOnlyList<string> GetSupplementalDutyCandidateNames(uint itemId)
    {
        if (!supplemental.DutyReferencesByItem.TryGetValue(itemId, out var dutyRefs))
            return Array.Empty<string>();

        return dutyRefs
            .SelectMany(ResolveDutyReference)
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<SourceEntry> GetKnownSourcesForItemId(uint itemId)
    {
        var candidateSources = new List<SourceEntry>();

        if (gameSources.TryGetValue(itemId, out var game))
            candidateSources.AddRange(game);

        candidateSources.AddRange(collectibleSources.GetSources(itemId));

        if (supplemental.DutyReferencesByItem.TryGetValue(itemId, out var dutyRefs))
        {
            foreach (var dutyRef in dutyRefs)
            {
                foreach (var duty in ResolveDutyReference(dutyRef))
                    candidateSources.Add(DutySource(duty, "Supplemental duty association"));
            }
        }

        if (supplemental.ItemSources.TryGetValue(itemId, out var supplementalRows))
        {
            foreach (var row in supplementalRows)
            {
                var resolved = ResolveSupplemental(row);
                if (resolved is not null)
                    candidateSources.Add(resolved);
            }
        }

        candidateSources.AddRange(garlandSources.GetSources(itemId));

        var synthetic = TryTargetForMetrics(itemId);
        if (synthetic is not null)
        {
            var broad = GetBroadSystemSource(synthetic);
            if (broad is not null)
                candidateSources.Add(broad);

            if (!HasNonCuratedSource(itemId) &&
                broad is null &&
                curatedFallbackSources.TryGetValue(itemId, out var curated))
            {
                candidateSources.AddRange(curated);
            }
        }

        SuppressAmbiguousSupplementalDuties(candidateSources);
        SuppressGenericSourcesWhenSpecificAlternativeExists(candidateSources);

        return candidateSources
            .GroupBy(s => $"{s.Kind}|{s.Name}|{s.Detail}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.Priority).First())
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool HasKnownActionableSourceForItemId(uint itemId) =>
        GetKnownSourcesForItemId(itemId).Any(IsActionableItemSpecificSource);

    private static bool IsAllaganTomestoneName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.StartsWith("Allagan Tomestone of ", StringComparison.OrdinalIgnoreCase);

    private void AddExchangeDependencySources(uint targetItemId, List<SourceEntry> sources)
    {
        AddExchangeDependencySourcesRecursive(
            targetItemId,
            sources,
            new HashSet<uint> { targetItemId },
            depth: 0);
    }

    private void AddExchangeDependencySourcesRecursive(
        uint targetItemId,
        List<SourceEntry> sources,
        HashSet<uint> visited,
        int depth)
    {
        if (depth > 2 || !exchangeDependencies.TryGetValue(targetItemId, out var dependencies))
            return;

        foreach (var dependency in dependencies)
        {
            var costName = ItemName(dependency.CostItemId) ?? $"item {dependency.CostItemId}";
            if (costName.Equals("Gil", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!visited.Add(dependency.CostItemId))
                continue;

            var candidateSources = GetKnownSourcesForItemId(dependency.CostItemId);

            // Tomestones are farmable currencies. A quest that happens to award
            // a few tomestones is technically a source, but it is poor guidance
            // for an exchange that requires hundreds or thousands. Prefer the
            // repeatable tomestone-system route and suppress incidental quest /
            // conversion rewards from the dependency summary.
            if (IsAllaganTomestoneName(costName))
            {
                candidateSources = candidateSources
                    .Where(x => x.Kind.Equals("Tomestones", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (candidateSources.Count == 0)
                {
                    candidateSources = new[]
                    {
                        new SourceEntry(
                            "Tomestones",
                            "Allagan Tomestone progression",
                            costName.Equals("Allagan Tomestone of Poetics", StringComparison.OrdinalIgnoreCase)
                                ? "Earn repeatedly from eligible duties, roulettes, hunts, and other level-capped content."
                                : "Earn from the eligible endgame reward activities for this tomestone's patch/expansion.",
                            Priority: 30,
                            SpecificityHint: "Named repeatable currency source")
                    };
                }
            }

            var actionable = candidateSources
                .Where(IsActionableItemSpecificSource)
                .ToArray();

            var detail = depth == 0
                ? $"Source for {dependency.Amount:N0} {costName}"
                : $"Dependency source for {dependency.Amount:N0} {costName}";

            // A token can legitimately come from several duties (for example
            // normal-raid books/armor tokens). Those are alternatives, not
            // ambiguity. Keep every equally actionable duty instead of silently
            // collapsing the answer to whichever duty sorts first.
            var dutyOptions = actionable
                .Where(IsDutyLike)
                .GroupBy(x => DutyUnlockGuidanceIndex.ExtractDutyName(x) ?? x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderBy(y => y.Priority).First())
                .ToArray();

            if (dutyOptions.Length > 0)
            {
                foreach (var duty in dutyOptions)
                {
                    sources.Add(duty with
                    {
                        Detail = detail,
                        Priority = Math.Max(duty.Priority, 40 + depth * 5),
                        SpecificityHint = "Exchange dependency",
                    });
                }
            }
            else
            {
                var best = actionable
                    .OrderBy(x => x.Priority)
                    .FirstOrDefault()
                    ?? candidateSources
                        .Where(x => !IsGenericSourceEntry(x))
                        .OrderBy(x => x.Priority)
                        .FirstOrDefault();

                if (best is not null)
                {
                    sources.Add(new SourceEntry(
                        "Currency Source",
                        best.Name,
                        detail + (string.IsNullOrWhiteSpace(best.Detail) ? string.Empty : $" — {best.Detail}"),
                        Priority: 50 + depth * 5,
                        SpecificityHint: "Exchange dependency"));
                }
            }

            // Follow an exchange token through at most two additional exchange
            // layers. This resolves token -> token -> duty/vendor chains while
            // preventing cycles or giant dependency trees.
            AddExchangeDependencySourcesRecursive(
                dependency.CostItemId,
                sources,
                visited,
                depth + 1);
        }
    }

    /// <summary>
    /// Supplemental chest/territory joins are useful but can associate one
    /// item with several unrelated duties.  Never present one of those as the
    /// authoritative answer.  If Garland has an item-specific duty relation,
    /// retain only supplemental entries that agree with it.  Otherwise replace
    /// a conflicting group with a truthful generic duty placeholder while the
    /// target-scoped enrichment request resolves the exact source.
    /// </summary>
    private static void SuppressAmbiguousSupplementalDuties(List<SourceEntry> sources)
    {
        // Strong item-specific authority can come from Garland or from a
        // supplemental row that explicitly names ContentFinderCondition.
        // Joined chest/territory associations are weaker and must never be
        // allowed to overrule an explicit item -> duty relation.
        var authoritativeDuties = sources
            .Where(x =>
                x.Detail?.Contains("Garland Tools", StringComparison.OrdinalIgnoreCase) == true ||
                string.Equals(
                    x.SpecificityHint,
                    "Supplemental exact duty relation",
                    StringComparison.OrdinalIgnoreCase))
            .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var supplementalAssociations = sources
            .Where(x => string.Equals(
                x.SpecificityHint,
                "Supplemental duty association",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (supplementalAssociations.Length == 0)
            return;

        if (authoritativeDuties.Count > 0)
        {
            // Keep every direct/authoritative duty source.  Remove only weaker
            // inferred associations that disagree with all authoritative names.
            // This allows items with several genuinely valid direct duty sources
            // to show all of those routes without inventing ambiguity.
            sources.RemoveAll(source =>
            {
                if (!string.Equals(
                        source.SpecificityHint,
                        "Supplemental duty association",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var name = DutyUnlockGuidanceIndex.ExtractDutyName(source);
                return string.IsNullOrWhiteSpace(name) ||
                       !authoritativeDuties.Contains(name);
            });
            return;
        }

        var distinctSupplementalDuties = supplementalAssociations
            .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (distinctSupplementalDuties <= 1)
            return;

        // No direct evidence exists and the inferred associations disagree.
        // Do not choose one.  Replace the conflicting group with an explicit
        // incomplete placeholder so target-scoped enrichment can continue.
        sources.RemoveAll(source =>
            string.Equals(
                source.SpecificityHint,
                "Supplemental duty association",
                StringComparison.OrdinalIgnoreCase));

        sources.Add(new SourceEntry(
            "Duty",
            "Duty / Instance Drop",
            "Multiple inferred duty associations conflict; exact duty still unresolved.",
            Priority: 85));
    }

    private static bool IsDutyLike(SourceEntry source) =>
        source.Kind.Equals("Activity", StringComparison.OrdinalIgnoreCase) ||
        source.Kind.Equals("Duty", StringComparison.OrdinalIgnoreCase) ||
        source.Kind.Contains("Raid", StringComparison.OrdinalIgnoreCase) ||
        source.Kind.Contains("Trial", StringComparison.OrdinalIgnoreCase) ||
        source.Kind.Contains("Dungeon", StringComparison.OrdinalIgnoreCase);

    private static void SuppressGenericSourcesWhenSpecificAlternativeExists(
        List<SourceEntry> sources)
    {
        if (sources.Count < 2)
            return;

        // Duty placeholders and concrete duties can use different Kind values
        // (for example Duty vs Activity).  Once a concrete duty is known, a
        // generic "Duty Chest" / "Duty / Instance Drop" line adds no useful
        // information and must not remain beside the exact answer.
        var hasConcreteDuty = sources.Any(source =>
            IsDutyLike(source) &&
            !IsUnresolvedDutyPlaceholder(source) &&
            !string.Equals(
                source.SpecificityHint,
                "Supplemental duty association",
                StringComparison.OrdinalIgnoreCase));

        if (hasConcreteDuty)
        {
            sources.RemoveAll(IsUnresolvedDutyPlaceholder);
        }

        var specificKinds = sources
            .Where(source => !IsGenericSourceEntry(source))
            .Select(source => source.Kind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (specificKinds.Count == 0)
            return;

        sources.RemoveAll(source =>
            IsGenericSourceEntry(source) &&
            specificKinds.Contains(source.Kind));
    }

    /// <summary>
    /// Generic broad acquisition systems inferred from live item metadata.
    /// These are weaker than Garland and therefore do not block Garland fetches.
    /// </summary>
    private SourceEntry? GetBroadSystemSource(TargetEntry target)
    {
        var ui = target.GameUiCategory.Trim();
        var residueName = target.Name.Trim();

        // Centralized external/future acquisition policy. Exact live/community
        // sources still sort above this broad-system layer.
        var specialAcquisition = SpecialAcquisitionCatalog.TryGetSource(residueName);
        if (specialAcquisition is not null)
            return specialAcquisition;

        // v0.43 targeted specificity pass.  These rules are intentionally
        // system/family based: the 817-row backlog exposed coherent acquisition
        // families whose exact item-level provider is absent, but whose real
        // acquisition system is unambiguous.
        var targetedSpecificity = GetTargetedSpecificitySource(target);
        if (targetedSpecificity is not null)
            return targetedSpecificity;

        // Remaining v0.34 audit: unusual but real in-game acquisition systems.
        if (residueName is "Carbuncle House Slippers" or "Extra-stuffed Carbuncle")
        {
            return new SourceEntry(
                "Ishgardian Restoration",
                "Kupo of Fortune",
                "Firmament Kupo of Fortune reward.",
                Priority: 65);
        }

        if (residueName.Equals("Spring Meadow Partition", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Ishgardian Restoration",
                "Kupo of Fortune / Cosmic Fortune",
                "Available from Kupo of Fortune and later Cosmic Fortune reward pools.",
                Priority: 65);
        }

        if (residueName.Equals("Drafting Table", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Fortune - Sinus Ardorum",
                "Cosmic Fortune reward.",
                Priority: 60);
        }

        if (residueName.Equals("Adventurer's Parcel", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Starward Standings / Star Contributor reward",
                "Cosmic Exploration reward parcel containing zone currency.",
                Priority: 65);
        }

        if (residueName is "Cracked Planicrystal" or "Cracked Stellacrystal")
        {
            return new SourceEntry(
                "Wondrous Tails",
                "Khloe's Certificate exchange (historical tier)",
                "Legacy Shadowbringers-era Wondrous Tails certificate exchange for non-combat materia currency.",
                Priority: 70);
        }

        if (residueName.Equals("Pepperoni Pizza", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Online Store / Housing",
                "Pepperoni Pizza Set",
                "Serving tied to the Pepperoni Pizza Set housing food; not a normal standalone farm/drop item.",
                Priority: 90);
        }

        if (residueName.Equals("Mizzenmast Biscuit", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Item",
                "Legacy 1.x inn item / existing player-held copies",
                "No modern new-drop source; existing tradable copies may still circulate between players.",
                Priority: 92);
        }

        // Remaining-residue pass (v0.33): active systems generic providers still miss.
        if (residueName.Equals("Company-issue Expectorant", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry("Retainer Venture", "Retainer Venture", Priority: 85);

        if (residueName.Equals("Bronze Sheep", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry("Island Sanctuary", "Isleworks Handicraft", Priority: 85);

        if (residueName.Equals("Alnairan Salmon", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry("Fishing", "Ksiroda, Thavnair", "Quest fish; Leech / Versatile Lure", Priority: 85);

        if (residueName.Equals("Lunar Lamenter", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry("Fishing", "Frozen Fissure, Mare Lamentorum", "Quest fish; Grey Worm / Versatile Lure", Priority: 85);

        if (residueName.Contains("Practice Materials", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry(
                "Ishgardian Restoration",
                "Ishgardian Restoration expert-crafting practice recipes",
                "Practice materials used by Restoration-era expert crafting practice recipes.",
                Priority: 85,
                SpecificityHint: "Named");

        // Tail-resolution pass (v0.33): exact active systems verified against
        // current acquisition data. These remain broad-system fallbacks so any
        // exact live/Lumina/community/Garland source continues to outrank them.
        if (target.Name is "Ring of Fidelity" or "Ring of Fortitude" or
            "Ring of Fortune" or "Ring of Freedom")
        {
            return new SourceEntry(
                "Quest",
                "Starting city opening quest",
                "Reward choice from Coming to Gridania / Limsa Lominsa / Ul'dah.",
                Priority: 85);
        }

        if (target.Name.Equals("Neophyte's Ring", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Hall of the Novice",
                "Tactical Training",
                "Complete Tactical Training exercise 3 after unlocking the advanced Hall of the Novice exercises.",
                Priority: 80);
        }

        if (target.Name.Equals("AAC Illustrated: HW Edition IV", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Raid",
                "AAC Heavyweight M4 (Savage)",
                "Savage raid book/token reward.",
                Priority: 20);
        }

        if (target.Name.Equals("Modern Vocation", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Vendor",
                "Retainer Vocation vendor",
                "Purchase while inquiring about retainer jobs.",
                Priority: 55,
                SpecificityHint: "Named");
        }

        if (target.Name.Equals("Magicked Prism (Cosmic Exploration)", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Fortune",
                "Cosmic Fortune reward.",
                Priority: 60);
        }

        if (target.Name.Equals("Voidsent Contract", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Occult Crescent",
                "Calamity Bound",
                "Occult Crescent: South Horn critical encounter reward.",
                Priority: 55);
        }

        if (target.Name.Equals("Sanguinite", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Occult Crescent",
                "The Forked Tower: Blood / Sanguine Recluse",
                "South Horn progression reward.",
                Priority: 50);
        }

        if (target.Name.Equals("Proof of Covering", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Chocobo Racing",
                "Chocobo breeding",
                "Issued by Katering when arranging a covering at Moogle's Gift Mounts.",
                Priority: 65);
        }

        if (target.Name.Equals("Wrapped Present", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Out-of-game Contest / Sweepstakes",
                "Official FFXIV community contest / sweepstakes",
                "No normal in-game acquisition. Awarded through selected official community events and may return.",
                Priority: 75);
        }

        if (target.Name.StartsWith("Runner's Plating (", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Duty",
                "Delubrum Reginae (Savage)",
                "Savage duty chest reward used for Law's Order gear augmentation.",
                Priority: 20);
        }

        if (target.Name.Equals("Portable Exoterminal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Fortune - Phaenna",
                Priority: 55);
        }

        if (target.Name is "Green Corvosi Carpet" or "Purple Corvosi Carpet" or
            "Imitation Corvosi Shisha")
        {
            return new SourceEntry(
                "Variant Dungeon",
                "The Merchant's Tale",
                "Duty reward.",
                Priority: 25);
        }

        if (target.Name.Equals("Housing Design Award MMXXV Plaque", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Out-of-game Contest / Sweepstakes",
                "Furnishing Design Contest 2025",
                "Official out-of-game contest prize; no normal in-game acquisition. May return through future promotions.",
                Priority: 75);
        }

        if (target.Name is "Odder Otter Round Andon Lamp" or "Stuffed Giant Beaver")
        {
            return new SourceEntry(
                "Merchandise Bonus",
                "Physical merchandise item code",
                "Bonus in-game furnishing from qualifying official merchandise.",
                Priority: 80);
        }

        // FG50 fishing/tackle rows are Cosmic Exploration mission fish/baits.
        // Their live relationship can only say Fishing, but the filter-group
        // identity safely supplies the named acquisition system.
        if (target.FilterGroup == 50 &&
            (string.Equals(ui, "Seafood", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(ui, "Fishing Tackle", StringComparison.OrdinalIgnoreCase) ||
             target.Category == "Fish"))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Exploration / Stellar Missions",
                "Cosmic Exploration fishing or mission material.",
                Priority: 92,
                SpecificityHint: "Named");
        }

        if (string.Equals(ui, "Seafood", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ui, "Fishing Tackle", StringComparison.OrdinalIgnoreCase) ||
            target.Category == "Fish")
        {
            return new SourceEntry(
                "Fishing",
                "Fishing / Spearfishing",
                "Exact fishing spot/bait is not resolved yet.",
                Priority: 95);
        }

        if (string.Equals(ui, "Dye", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Dye",
                "Dye acquisition",
                "Vendor, crafting, venture, event, or content reward depending on the dye.",
                Priority: 95);
        }

        if (string.Equals(ui, "Materia", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Materia",
                "Materia acquisition",
                "Spiritbond extraction, exchange, vendor, or content reward depending on grade.",
                Priority: 95);
        }

        if (string.Equals(ui, "Gardening", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Gardening",
                "Gardening / Gathering",
                "Seeds, soils, and gardening items come from gathering, vendors, or gardening systems.",
                Priority: 95);
        }

        if (string.Equals(ui, "Soul Crystal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Job Progression",
                "Job / Specialist progression",
                "Soul crystal or crafting-specialist progression.",
                Priority: 95);
        }

        if (string.Equals(ui, "Currency", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Currency",
                "Content Currency / Token",
                "Exact earning activity is not resolved yet.",
                Priority: 95);
        }

        if (string.Equals(ui, "Seasonal Miscellany", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Event",
                "Seasonal / Collaboration Event",
                "Exact event or historical availability is not resolved yet.",
                Priority: 98);
        }

        // Current live catalog audit: FG26 is entirely Primed Aetherial Wheels.
        if (target.FilterGroup == 26)
        {
            return new SourceEntry(
                "Free Company",
                "Aetherial Wheel",
                "Primed company-action wheel.",
                Priority: 95);
        }

        // Current live catalog audit: FG47 contains only Island/Seafarer's
        // Cowries, while FG48 is Island Sanctuary inventory.
        if (target.FilterGroup is 47 or 48)
        {
            return new SourceEntry(
                "Island Sanctuary",
                "Island Sanctuary",
                "Island Sanctuary currency, material, tool, feed, or animal resource.",
                Priority: 95);
        }

        // Current live catalog audit: these filter groups are actual,
        // spendable Cosmic Exploration currencies.
        //
        // FG55 = Cosmocredit
        // FG58 = zone Dronebits
        if (target.FilterGroup is 55 or 58)
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Exploration",
                target.FilterGroup == 55
                    ? "Cosmocredit currency."
                    : "Dronebit currency from Stellar Missions.",
                Priority: 90);
        }

        // FG50 is heterogeneous overall, but the audit shows the
        // FG50 + UI=Other slice is consistently Cosmic Exploration
        // mission/artifact material. Cosmic fish remain handled by Seafood.
        if (target.FilterGroup == 50 &&
            string.Equals(ui, "Other", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Exploration / Stellar Missions",
                "Cosmic mission, gathering, artifact, or exploration material.",
                Priority: 92);
        }

        // FG54 is the Occult Crescent Arcane/Enlightenment progression
        // namespace in the current live catalog.
        if (target.FilterGroup == 54)
        {
            return new SourceEntry(
                "Occult Crescent",
                "The Occult Crescent",
                "Expedition currency / progression reward.",
                Priority: 92);
        }

        // Shared component stages for the Splendorous tool series.
        if (target.Name.EndsWith(
                " Component",
                StringComparison.OrdinalIgnoreCase))
        {
            string[] splendorousStages =
            [
                "Splendorous ",
                "Adaptive ",
                "Customized ",
                "Brilliant ",
                "Inspirational ",
                "Nightforged ",
            ];

            if (splendorousStages.Any(prefix =>
                    target.Name.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return new SourceEntry(
                    "Relic Tool",
                    "Splendorous tool questline",
                    "Boutique of Splendors collectable/component exchange.",
                    Priority: 88);
            }
        }

        // Deep Dungeon aetherpool / hoard currencies and containers.
        if (string.Equals(
                target.Name,
                "Aetherpool Grip",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Deep Dungeon",
                "Palace of the Dead",
                "Forged from Palace of the Dead aetherpool strength.",
                Priority: 85);
        }

        if (string.Equals(
                target.Name,
                "Empyrean Aetherpool Grip",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Deep Dungeon",
                "Heaven-on-High",
                "Forged from Heaven-on-High aetherpool strength.",
                Priority: 85);
        }

        if (target.Name.StartsWith(
                "Orthos Aetherpool ",
                StringComparison.OrdinalIgnoreCase) ||
            target.Name.EndsWith(
                "-tinged Sack",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Deep Dungeon",
                "Eureka Orthos",
                "Eureka Orthos aetherpool / Accursed Hoard reward.",
                Priority: 85);
        }

        if (target.Name.StartsWith(
                "Illumed Aetherpool ",
                StringComparison.OrdinalIgnoreCase) ||
            (target.Name.StartsWith(
                 "Sack of ",
                 StringComparison.OrdinalIgnoreCase) &&
             target.Name.EndsWith(
                 " Light",
                 StringComparison.OrdinalIgnoreCase)))
        {
            return new SourceEntry(
                "Deep Dungeon",
                "Pilgrim's Traverse",
                "Pilgrim's Traverse aetherpool / Hoard reward.",
                Priority: 85);
        }

        // Hunt currencies/logs.
        if (target.Name.Contains(
                "Clan Mark Log",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                target.Name,
                "Unstained Mark Log",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Hunts",
                "The Hunt",
                "Hunt currency / elite-mark reward.",
                Priority: 90);
        }

        // Adventurer Squadron consumable manuals.
        if (target.Name.StartsWith(
                "Squadron ",
                StringComparison.OrdinalIgnoreCase) &&
            target.Name.EndsWith(
                " Manual",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Grand Company",
                "Adventurer Squadron",
                "Squadron mission reward.",
                Priority: 90);
        }

        // Cosmic Exploration artifact-search and Fortune families.
        if (target.Name.EndsWith(
                " Ancient Record",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Artifact Search",
                "Appraised ancient record from Cosmic Exploration artifact search.",
                Priority: 85);
        }

        if (target.Name.StartsWith(
                "Big Bang Ticket (",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Fortune",
                "Cosmic Fortune ticket.",
                Priority: 85);
        }

        // Eureka field-operation currencies/containers that do not have a
        // stable reverse relation in the installed supplemental data.
        if (target.Name.Contains("Protean Crystal", StringComparison.OrdinalIgnoreCase) ||
            target.Name.EndsWith("-warped Lockbox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Lockbox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Damaged Lockbox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Eureka Fragment", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Field Operation",
                "The Forbidden Land, Eureka",
                "Eureka progression, notorious monsters, lockboxes, or Baldesion Arsenal reward.",
                Priority: 88);
        }

        // Anima weapon progression materials.
        if (string.Equals(target.Name, "Crystal Sand", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Amber-encased Vilekin", StringComparison.OrdinalIgnoreCase) ||
            target.Name.EndsWith(" Gobbiegoo", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Relic Weapon",
                "Anima weapon questline",
                "Heavensward Anima weapon progression material.",
                Priority: 88);
        }

        // Dawntrail Phantom relic progression currency.
        if (string.Equals(target.Name, "Crystal Paste", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Relic Weapon",
                "Phantom weapon questline",
                "Dawntrail Phantom relic progression material.",
                Priority: 82);
        }

        // Current Windward Wilds rewards are automatic duty rewards rather than
        // ordinary treasure-chest drops, so generic chest reconstruction can miss them.
        if (string.Equals(target.Name, "Veldian Weapon Coffer (IL 745)", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Guardian Scale", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "The Windward Wilds",
                "Guaranteed/current trial reward; also available from the Extreme version as applicable.",
                Priority: 12);
        }

        // Island Sanctuary workshop/progression objects represented as ordinary
        // Item rows instead of the dedicated Island filter groups.
        if (target.Name.Contains("Mammet-sized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Mammet of the Cycle Award", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Isleberry Jam", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Islefish Pie", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Island Sanctuary",
                "Island Sanctuary",
                "Island Sanctuary workshop, rank, or development progression.",
                Priority: 92);
        }

        // Flowerpot/gardening outputs are Miscellany in the game UI and therefore
        // are not covered by the Gardening ItemUICategory fallback.
        if (target.Name.EndsWith(" Cornflowers", StringComparison.OrdinalIgnoreCase) ||
            target.Name.EndsWith(" Tea Flowers", StringComparison.OrdinalIgnoreCase) ||
            target.Name.EndsWith(" Champa", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Curiel Root", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Blood Pepper", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Jute", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Althyk Lavender", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Name, "Voidrake", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Gardening",
                "Gardening / Flowerpot",
                "Gardening, flowerpot, or seed crossbreeding output.",
                Priority: 90);
        }

        // Cosmic-named furnishing/reward families can live outside FG50.
        if (target.Name.StartsWith("Cosmo", StringComparison.OrdinalIgnoreCase) ||
            target.Name.StartsWith("Imitation Cosmo", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Exploration",
                "Cosmic Exploration reward or exchange item.",
                Priority: 92);
        }

        // Ceremony of Eternal Bonding objects.
        if (target.Name.StartsWith(
                "Promise of ",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                target.Name,
                "Eternity Ring",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                target.Name,
                "Blank Invitation",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                target.Name,
                "Ceremony Invitation",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Ceremony",
                "Ceremony of Eternal Bonding",
                "Eternal Bonding ceremony item.",
                Priority: 90);
        }

        // Account/campaign and Squadron utility items.
        if (string.Equals(
                target.Name,
                "Aetheryte Pendulum",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Account Reward",
                "Recruit a Friend",
                "Recruit a Friend campaign reward.",
                Priority: 90);
        }

        if (string.Equals(
                target.Name,
                "Gold Chocobo Feather",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Account Reward",
                "Recruit a Friend / World Transfer incentives",
                "Account/campaign reward currency.",
                Priority: 90);
        }

        if (string.Equals(
                target.Name,
                "Priority Aetheryte Pass",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                target.Name,
                "Gold Saucer VIP Card",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Grand Company",
                "Adventurer Squadron",
                "Squadron mission reward.",
                Priority: 90);
        }

        return null;
    }

    public bool HasBroadSystemSource(TargetEntry target) =>
        GetBroadSystemSource(target) is not null;

    // ---------------------------------------------------------------------
    // Duty / supplemental sources
    // ---------------------------------------------------------------------

    private IEnumerable<ContentFinderInfo> ResolveDutyReference(DutyReference reference)
    {
        if (reference.Kind == DutyReferenceKind.ContentFinderCondition)
        {
            if (cfcById.TryGetValue(reference.Id, out var cfc))
                yield return cfc;

            yield break;
        }

        if (reference.Kind == DutyReferenceKind.TerritoryType &&
            cfcByTerritory.TryGetValue(reference.Id, out var duties))
        {
            // TerritoryType is not a unique duty key. Multiple duties can share
            // a territory, and returning all/first matches can fabricate sources
            // (the Dawntrail Wing -> Alexander bug was one example).
            //
            // Only trust a territory-only association when it resolves to one
            // unique Content Finder entry.
            var unique = duties
                .GroupBy(d => d.CfcId)
                .Select(g => g.First())
                .ToArray();

            if (unique.Length == 1)
                yield return unique[0];
        }
    }

    private void BuildContentFinderIndex()
    {
        foreach (var cfc in data.GetExcelSheet<ContentFinderCondition>())
        {
            var name = cfc.Name.ExtractText().Trim();

            if (cfc.RowId == 0 || string.IsNullOrWhiteSpace(name))
                continue;

            var territoryId = ReflectionUtil.GetNestedRowId(
                cfc,
                "TerritoryType",
                "Territory");

            var instanceContentId = ReflectionUtil.GetNestedRowId(
                cfc,
                "Content",
                "InstanceContent");

            var info = new ContentFinderInfo(
                cfc.RowId,
                name,
                territoryId,
                instanceContentId,
                ReflectionUtil.GetBoolProperty(cfc, "AllianceRoulette") == true,
                ReflectionUtil.GetBoolProperty(cfc, "NormalRaidRoulette") == true,
                ReflectionUtil.GetBoolProperty(cfc, "TrialRoulette") == true,
                (uint?)ReflectionUtil.GetNumericProperty(cfc, "QueueMaxPlayers") ?? 0);

            cfcById[cfc.RowId] = info;

            if (territoryId != 0)
            {
                if (!cfcByTerritory.TryGetValue(territoryId, out var list))
                {
                    list = [];
                    cfcByTerritory[territoryId] = list;
                }

                list.Add(info);
            }
        }
    }

    private SourceEntry? ResolveSupplemental(SupplementalSourceRecord record)
    {
        var row = record.Row;

        if (record.Dataset.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase))
        {
            var cfcId = SupplementalCsvIndex.FirstIdContaining(
                row, "ContentFinderCondition", "Cfc");

            // An explicit ContentFinderCondition on the item-source row is a
            // direct item -> duty relationship.  This is materially stronger
            // than a duty inferred by joining a generic chest/boss parent row,
            // so preserve that provenance instead of mixing it into the broad
            // supplemental-association bucket.
            if (cfcId != 0 && cfcById.TryGetValue(cfcId, out var direct))
                return DutySource(direct, "Supplemental exact duty relation");

            var territoryId = SupplementalCsvIndex.FirstIdContaining(
                row, "TerritoryType", "Territory");

            if (territoryId != 0 &&
                cfcByTerritory.TryGetValue(territoryId, out var duties))
            {
                var unique = duties
                    .GroupBy(d => d.CfcId)
                    .Select(g => g.First())
                    .ToArray();

                if (unique.Length == 1)
                    return DutySource(unique[0], "Supplemental duty association");
            }

            // Do not invent a duty name from an ambiguous territory mapping.
            return new SourceEntry(
                "Duty",
                record.Kind,
                "Duty family known; exact duty unresolved.",
                Priority: 90);
        }

        if (record.Dataset.Equals("FateItem", StringComparison.OrdinalIgnoreCase))
        {
            var name = ResolveFateName(row) ?? "FATE reward";
            return new SourceEntry("FATE", name, Priority: 50);
        }

        if (record.Dataset.Equals("MobDrop", StringComparison.OrdinalIgnoreCase))
        {
            var name = ResolveMobName(row) ?? "Monster drop";
            return new SourceEntry("Monster", name, Priority: 50);
        }

        if (record.Dataset.Equals("FieldOpCoffer", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry("Field Operation", "Field operation coffer", Priority: 50);

        if (record.Dataset.Equals("RetainerVentureItem", StringComparison.OrdinalIgnoreCase))
        {
            var ventureId = SupplementalCsvIndex.FirstIdContaining(row, "RetainerTask");
            if (ventureId == 0)
                ventureId = SupplementalCsvIndex.FirstIdContaining(row, "Venture");

            if (ventureId != 0 &&
                retainerVentureLabelsById.TryGetValue(ventureId, out var ventureLabel))
            {
                return new SourceEntry(
                    "Retainer Venture",
                    $"Retainer Venture: {ventureLabel}",
                    "LuminaSupplemental venture relationship",
                    Priority: 55,
                    SpecificityHint: "Named");
            }

            var method = SupplementalCsvIndex.GetText(
                row,
                "VentureType",
                "Type",
                "Method",
                "Source");

            if (!string.IsNullOrWhiteSpace(method) &&
                !method.Equals("Retainer Venture", StringComparison.OrdinalIgnoreCase))
            {
                return new SourceEntry(
                    "Retainer Venture",
                    $"Retainer Venture: {method}",
                    "LuminaSupplemental venture relationship",
                    Priority: 55,
                    SpecificityHint: "Named");
            }

            return new SourceEntry("Retainer Venture", "Retainer Venture", Priority: 60);
        }

        if (record.Dataset.Equals("AirshipDrop", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry(
                "Airship Voyage",
                ResolveRouteName(row, airship: true) ?? "Airship voyage",
                Priority: 60);

        if (record.Dataset.Equals("SubmarineDrop", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry(
                "Submarine Voyage",
                ResolveRouteName(row, airship: false) ?? "Submarine voyage",
                Priority: 60);

        if (record.Dataset.Equals("GardeningCrossbreed", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry("Gardening", "Gardening crossbreed", Priority: 60);

        if (record.Dataset.Equals("ItemSupplement", StringComparison.OrdinalIgnoreCase))
        {
            var type =
                SupplementalCsvIndex.GetText(row, "SourceType", "Type", "Method", "Reason")
                ?? "Item conversion";

            return new SourceEntry("Item Conversion", type, Priority: 70);
        }

        return new SourceEntry(record.Kind, record.Kind, Priority: 90);
    }

    private SourceEntry DutySource(ContentFinderInfo duty, string? specificityHint = null)
    {
        var unlocked = IsContentUnlocked(duty.InstanceContentId);

        return new SourceEntry(
            "Activity",
            duty.Name,
            IsUnlocked: unlocked,
            Priority: unlocked == true ? 10 : 15,
            SpecificityHint: specificityHint);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void Add(uint itemId, SourceEntry source)
    {
        if (!gameSources.TryGetValue(itemId, out var list))
        {
            list = [];
            gameSources[itemId] = list;
        }

        list.Add(source);
    }

    private void AddFallback(uint itemId, SourceEntry source)
    {
        if (!curatedFallbackSources.TryGetValue(itemId, out var list))
        {
            list = [];
            curatedFallbackSources[itemId] = list;
        }

        // Curated logic must always lose to exact data providers. This prevents
        // a hand-maintained rule from masking a generic resolver improvement.
        var normalized = source with
        {
            Priority = Math.Max(source.Priority, 90),
        };

        list.Add(normalized);
    }

    private TargetEntry? TryTargetForMetrics(uint itemId)
    {
        if (!itemsById.TryGetValue(itemId, out var item))
            return null;

        var name = item.Name.ExtractText().Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string uiCategory = string.Empty;

        try
        {
            if (item.ItemUICategory.IsValid)
                uiCategory = item.ItemUICategory.Value.Name.ExtractText().Trim();
        }
        catch
        {
        }

        var category =
            string.Equals(uiCategory, "Seafood", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uiCategory, "Fishing Tackle", StringComparison.OrdinalIgnoreCase)
                ? "Fish"
                : string.Empty;

        var filterGroup =
            (uint?)ReflectionUtil.GetNumericProperty(item, "FilterGroup") ?? 0;

        return new TargetEntry(
            itemId,
            name,
            category,
            uiCategory,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            item.IsUntradable,
            filterGroup);
    }

    private bool HasNonCuratedSource(uint itemId)
    {
        if (gameSources.TryGetValue(itemId, out var game) && game.Count > 0)
            return true;

        if (supplemental.DutyReferencesByItem.TryGetValue(itemId, out var duties) &&
            duties.Count > 0)
            return true;

        if (supplemental.ItemSources.TryGetValue(itemId, out var supplementalSources) &&
            supplementalSources.Count > 0)
            return true;

        if (collectibleSources.HasSource(itemId))
            return true;

        if (garlandSources.HasSource(itemId))
            return true;

        return false;
    }

    public string GetResolutionTier(TargetEntry target)
    {
        // Keep provenance classification lightweight. This method is used by
        // diagnostics that may iterate the entire catalog, so it must never call
        // Resolve(target): Resolve can follow exchange dependency chains and was
        // responsible for the v1.1.12.6 full-catalog hitch when Debug was open.
        var known = GetKnownSourcesForItemId(target.ItemId);
        var primary = known.FirstOrDefault(source =>
            !source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase));

        if (primary is not null)
        {
            var garland = garlandSources.GetSources(target.ItemId);
            if (garland.Contains(primary))
                return "Garland";

            var collect = collectibleSources.GetSources(target.ItemId);
            if (collect.Contains(primary))
                return "FFXIV Collect";

            if (gameSources.TryGetValue(target.ItemId, out var game) && game.Contains(primary))
                return "Live/Game-sheet";

            if ((primary.SpecificityHint?.Equals(
                    "Supplemental duty association",
                    StringComparison.OrdinalIgnoreCase) == true ||
                 primary.SpecificityHint?.Equals(
                    "Supplemental exact duty relation",
                    StringComparison.OrdinalIgnoreCase) == true) ||
                (supplemental.ItemSources.TryGetValue(target.ItemId, out var supplementalRows) &&
                 supplementalRows.Select(ResolveSupplemental).Any(source => source is not null && source.Equals(primary))))
            {
                return "LuminaSupplemental";
            }

            var broad = GetBroadSystemSource(target);
            if (broad is not null && broad.Equals(primary))
                return "Broad system";

            if (curatedFallbackSources.TryGetValue(target.ItemId, out var fallback) &&
                fallback.Any(source => source.Equals(primary)))
            {
                return "Curated fallback";
            }
        }

        if (CuratedGuides.ForTarget(target) is not null)
            return "Curated guide";

        // Provider data may exist but be intentionally suppressed because it is
        // ambiguous. Preserve the source family without pretending it is exact.
        if ((supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var duties) && duties.Count > 0) ||
            (supplemental.ItemSources.TryGetValue(target.ItemId, out var supp) && supp.Count > 0))
        {
            return "LuminaSupplemental";
        }

        return "Unresolved";
    }

    /// <summary>
    /// User-facing availability class. This is deliberately broader than a
    /// real-time event calendar: it distinguishes permanent acquisition from
    /// recurring/limited and external/future families without pretending a
    /// specific event is active right now.
    /// </summary>
    public string GetAvailabilityState(TargetEntry target)
    {
        if (target.Name.Equals("MGF", StringComparison.OrdinalIgnoreCase) ||
            target.Name.Equals("MGC", StringComparison.OrdinalIgnoreCase))
            return "Limited / Recurring Event";

        var special = SpecialAcquisitionCatalog.TryGetSource(target.Name);
        if (special is not null)
        {
            if (special.Kind.Equals("Future / Unknown Availability", StringComparison.OrdinalIgnoreCase))
                return "Future / Unknown";

            if (special.Kind.StartsWith("Out-of-game", StringComparison.OrdinalIgnoreCase))
                return "Limited / External";
        }

        var broad = GetBroadSystemSource(target);
        if (broad is not null &&
            (broad.Kind.Equals("Legacy Event", StringComparison.OrdinalIgnoreCase) ||
             broad.Kind.Equals("Legacy Item", StringComparison.OrdinalIgnoreCase)))
        {
            return "Legacy / Existing Copies";
        }

        if (broad is not null &&
            broad.Kind.StartsWith("Out-of-game", StringComparison.OrdinalIgnoreCase))
        {
            return "Limited / External";
        }

        if (broad is not null &&
            (broad.Kind.Equals("Event", StringComparison.OrdinalIgnoreCase) ||
             broad.Kind.Contains("Seasonal", StringComparison.OrdinalIgnoreCase) ||
             broad.Name.Contains("event", StringComparison.OrdinalIgnoreCase) ||
             (broad.Detail?.Contains("event", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            return "Limited / Recurring Event";
        }

        return "Available / Permanent";
    }

    public string GetSourceSpecificity(TargetEntry target)
    {
        var tier = GetResolutionTier(target);

        if (tier == "Broad system")
        {
            var broad = GetBroadSystemSource(target);
            if (broad is not null &&
                broad.Kind.Equals("Duty", StringComparison.OrdinalIgnoreCase) &&
                !IsUnresolvedDutyPlaceholder(broad) &&
                !string.IsNullOrWhiteSpace(DutyUnlockGuidanceIndex.ExtractDutyName(broad)))
            {
                return "Named item-specific";
            }

            return broad is not null && IsNamedSystemSpecificSource(broad)
                ? "Named system-specific"
                : "System-level";
        }
        if (tier == "Curated fallback")
        {
            if (curatedFallbackSources.TryGetValue(target.ItemId, out var curated) &&
                curated.Any(IsNamedSystemSpecificSource))
            {
                return "Named curated";
            }

            return "Curated fallback";
        }
        if (tier == "Curated guide")
            return "Guide-level";
        if (tier == "Unresolved")
            return "Unresolved";

        var providerSources = GetNonGarlandProviderSources(target);

        if (garlandSources.HasSource(target.ItemId))
            providerSources = providerSources
                .Concat(garlandSources.GetSources(target.ItemId))
                .ToArray();

        var supplementalDutyNames = providerSources
            .Where(source => string.Equals(
                source.SpecificityHint,
                "Supplemental duty association",
                StringComparison.OrdinalIgnoreCase))
            .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Never let several conflicting inferred duty associations make the
        // target look exact.  An explicit item->duty relation (or any other
        // authoritative actionable source) still legitimately satisfies exact
        // specificity.
        var hasAuthoritativeActionableSource = providerSources.Any(source =>
            !string.Equals(
                source.SpecificityHint,
                "Supplemental duty association",
                StringComparison.OrdinalIgnoreCase) &&
            IsActionableItemSpecificSource(source));

        if (hasAuthoritativeActionableSource || supplementalDutyNames.Length == 1)
            return "Named item-specific";

        if (supplementalDutyNames.Length > 1)
            return "Named incomplete item-specific";

        // A named but incomplete shop/duty relationship is still useful context,
        // but it is not an exact actionable answer. Keep it explicitly below
        // item-specific quality so target-scoped enrichment remains eligible.
        if (providerSources.Any(source =>
                IsIncompleteShopSource(source) || IsUnresolvedDutyPlaceholder(source)))
            return "Named incomplete item-specific";

        // A generic live relationship (for example "Mining") can coexist with
        // a stronger metadata-driven system identity (for example Cosmic
        // Exploration / Stellar Missions). Preserve the live provenance tier,
        // but surface the strongest truthful specificity that the user can act
        // on rather than pretending the generic verb is all CQG knows.
        var broadSystem = GetBroadSystemSource(target);
        if (broadSystem is not null && IsNamedSystemSpecificSource(broadSystem))
            return "Named system-specific";

        return "Generic item-specific";
    }

    /// <summary>
    /// True when a source names a real acquisition system/content family rather
    /// than only a generic verb such as Vendor, Mining, Fishing, Currency, or
    /// "Seasonal / Collaboration Event". This deliberately does NOT promote
    /// vague umbrella labels; exact item-specific providers remain a separate,
    /// stronger specificity tier.
    /// </summary>
    private static bool IsNamedSystemSpecificSource(SourceEntry source)
    {
        if (source.SpecificityHint?.Equals("Named", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (source.SpecificityHint?.Equals("Generic", StringComparison.OrdinalIgnoreCase) == true)
            return false;

        var kind = source.Kind.Trim();
        var name = source.Name.Trim();

        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Explicitly generic labels remain generic even if their Kind names a
        // larger system.
        if (ContainsAny(
                name,
                "Special / Currency Shop",
                "Gil Vendor",
                "Fishing / Spearfishing",
                "Dye acquisition",
                "Content Currency / Exchange Token",
                "Content Currency / Token",
                "Seasonal / Collaboration Event",
                "ARR dungeon / levequest reward",
                "Job quest / Calamity Salvager",
                "Job / Specialist progression",
                "Field operation coffer",
                "Market Board / Player Trade",
                "Retainer Venture",
                "Airship / Submarine Voyage",
                "Practice recipe materials",
                "No published acquisition source yet"))
        {
            return false;
        }

        if (name is "Mining" or "Quarrying" or "Logging" or "Botany" or "Gathering")
            return false;

        // Named systems/content that are already materially more useful than a
        // generic acquisition verb, even when the exact NPC/node/drop is not
        // recoverable from the current provider data.
        if (kind is
            "Cosmic Exploration" or
            "Island Sanctuary" or
            "Eureka" or
            "Occult Crescent" or
            "Relic" or
            "Relic Tool" or
            "Relic Weapon" or
            "Crafting Progression" or
            "Free Company" or
            "Gold Saucer" or
            "PvP" or
            "Training" or
            "Deep Dungeon" or
            "Grand Company" or
            "Ceremony" or
            "Hunts" or
            "Hunt" or
            "Wondrous Tails" or
            "Ishgardian Restoration" or
            "Chocobo Racing" or
            "Hall of the Novice" or
            "Variant Dungeon" or
            "Duty" or
            "Raid" or
            "Trial" or
            "Account Reward" or
            "Seasonal Event" or
            "Out-of-game Contest / Sweepstakes" or
            "Merchandise Bonus" or
            "Online Store / Housing" or
            "Legacy Item")
        {
            return true;
        }

        if (kind == "Event" &&
            (name.Contains("Moogle Treasure Trove", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Yo-kai Watch", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (kind == "Field Operation" &&
            (name.Contains("Bozjan", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Zadnor", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Eureka", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (kind == "Gathering" &&
            name.Contains("Timeworn Map", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (kind == "Gardening" &&
            name.Contains("Gardening", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (kind == "Quest" &&
            name.Contains("Starting city", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public bool IsCuratedFallbackOnly(TargetEntry target) =>
        curatedFallbackSources.ContainsKey(target.ItemId) &&
        !HasNonCuratedSource(target.ItemId) &&
        GetBroadSystemSource(target) is null;

    private string FormatCost(uint itemId, uint amount)
    {
        var name = ItemName(itemId) ?? $"Item #{itemId}";
        return $"{amount:N0} {name}";
    }

    private string? ItemName(uint itemId)
    {
        if (!itemsById.TryGetValue(itemId, out var item))
            return null;

        var name = item.Name.ExtractText().Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private string? ResolveFateName(IReadOnlyDictionary<string, string> row)
    {
        var fateId = SupplementalCsvIndex.FirstIdContaining(row, "Fate");
        if (fateId == 0)
            return null;

        try
        {
            var fate = data.GetExcelSheet<Fate>().GetRow(fateId);
            var name = fate.Name.ExtractText().Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveMobName(IReadOnlyDictionary<string, string> row)
    {
        var bnpcNameId = SupplementalCsvIndex.FirstIdContaining(row, "BNpcName");
        if (bnpcNameId == 0)
            return null;

        try
        {
            var mob = data.GetExcelSheet<BNpcName>().GetRow(bnpcNameId);
            var name = mob.Singular.ExtractText().Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveRouteName(
        IReadOnlyDictionary<string, string> row,
        bool airship)
    {
        var direct = SupplementalCsvIndex.GetText(
            row,
            "Name",
            "RouteName",
            "DestinationName",
            "Destination");

        if (!string.IsNullOrWhiteSpace(direct) &&
            !uint.TryParse(direct, out _))
            return direct.Trim();

        if (airship)
        {
            var pointId = SupplementalCsvIndex.FirstIdContaining(
                row,
                "AirshipExplorationPoint",
                "AirshipExploration",
                "ExplorationPoint",
                "Destination",
                "Route");

            var name = RowName<AirshipExplorationPoint>(
                pointId,
                "Name",
                "NameShort");

            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        else
        {
            var pointId = SupplementalCsvIndex.FirstIdContaining(
                row,
                "SubmarineExploration",
                "ExplorationPoint",
                "Destination",
                "Route");

            var name = RowName<SubmarineExploration>(
                pointId,
                "Destination",
                "Location");

            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private void BuildRetailShopNpcIndex()
    {
        try
        {
            var knownShopIds = new HashSet<uint>();

            foreach (var rows in data.GetSubrowExcelSheet<GilShopItem>())
            {
                if (rows.RowId != 0)
                    knownShopIds.Add(rows.RowId);
            }

            foreach (var shop in data.GetExcelSheet<SpecialShop>())
            {
                if (shop.RowId != 0)
                    knownShopIds.Add(shop.RowId);
            }

            if (knownShopIds.Count == 0)
                return;

            foreach (var npc in data.GetExcelSheet<ENpcBase>())
            {
                if (npc.RowId == 0)
                    continue;

                var raw = ReflectionUtil.GetPropertyValue(npc, "ENpcData", "Data");
                if (raw is not IEnumerable values)
                    continue;

                foreach (var value in values)
                {
                    uint id = 0;

                    try
                    {
                        id = ReflectionUtil.ExtractRowId(value);
                        if (id == 0 && value is not null)
                            id = Convert.ToUInt32(value);
                    }
                    catch
                    {
                        id = 0;
                    }

                    if (id == 0 || !knownShopIds.Contains(id))
                        continue;

                    if (!retailShopNpcIds.TryGetValue(id, out var npcs))
                    {
                        npcs = [];
                        retailShopNpcIds[id] = npcs;
                    }

                    npcs.Add(npc.RowId);
                }
            }

            log.Debug(
                $"CQG retail shop fallback metadata: shops={retailShopNpcIds.Count:N0}, " +
                $"links={retailShopNpcIds.Sum(x => x.Value.Count):N0}.");
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not build retail ENpc shop fallback index.");
        }
    }

    private IReadOnlyList<ShopNpcRef> ResolveShopNpcNames(uint shopId)
    {
        var npcIds = new HashSet<uint>(supplemental.GetShopNpcIds(shopId));

        // Supplemental ENpcShop is authoritative when present. Only use the
        // retail ENpcBase reverse scan as a gap-filler for shops with no
        // supplemental contact at all; this avoids introducing speculative
        // NPCs from unrelated ENpcData values that happen to share an ID.
        if (npcIds.Count == 0 && retailShopNpcIds.TryGetValue(shopId, out var retailIds))
        {
            foreach (var npcId in retailIds)
                npcIds.Add(npcId);
        }

        var refs = new List<ShopNpcRef>();

        foreach (var npcId in npcIds)
        {
            var name = RowName<ENpcResident>(
                npcId,
                "Singular",
                "Name");

            if (!string.IsNullOrWhiteSpace(name) &&
                !refs.Any(x => x.Id == npcId))
            {
                refs.Add(new ShopNpcRef(npcId, name));
            }
        }

        return refs
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .ToArray();
    }

    private static unsafe bool? IsContentUnlocked(uint instanceContentId)
    {
        if (instanceContentId == 0)
            return null;

        try
        {
            return UIState.IsInstanceContentUnlocked(instanceContentId);
        }
        catch
        {
            return null;
        }
    }

    private sealed record ShopNpcRef(uint Id, string Name);

    private sealed record ExchangeDependency(
        uint CostItemId,
        uint Amount,
        string ShopName,
        IReadOnlyList<string> NpcNames);

    private sealed record ContentFinderInfo(
        uint CfcId,
        string Name,
        uint TerritoryTypeId,
        uint InstanceContentId,
        bool AllianceRaid,
        bool NormalRaid,
        bool Trial,
        uint QueueMaxPlayers);
}
