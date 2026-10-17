using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

// ===== SourceResolver.cs =====
/// <summary>
/// Coordinates acquisition-source resolution for item targets.
///
/// Exact typed game-sheet relationships are preferred for high-volume source
/// families, with supplemental/community providers and curated fallbacks layered
/// behind them. Player-facing filtering and ordering are intentionally kept
/// separate from the raw diagnostic graph.
/// </summary>
public sealed partial class SourceResolver
{
    private readonly IDataManager data;
    private readonly SupplementalCsvIndex supplemental;
    private readonly CollectibleSourceIndex collectibleSources;
    private readonly GarlandSourceIndex garlandSources;
    private readonly LeveMonsterIndex leveMonsters;
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

    // Item result ID -> exact recipe rows and their direct ingredients. This is
    // deliberately non-recursive: CQG shows the component quantities written in
    // the recipe itself and does not aggregate base materials through subcrafts.
    private readonly Dictionary<uint, List<CraftingRecipeInfo>> craftingRecipes = [];

    // Exact English item-name lookup. Triple Triad card rows identify the card
    // rather than the inventory item, so this lets us connect "2B" -> "2B Card".
    private readonly Dictionary<string, List<uint>> itemIdsByName =
        new(StringComparer.OrdinalIgnoreCase);

    // Garland identifies monster drops by display name rather than BNpcName row ID.
    // Keep a local name -> BNpcName index so those rows can be enriched with the
    // same level/zone metadata as LuminaSupplemental MobDrop rows.
    private readonly Dictionary<string, List<uint>> bnpcNameIdsByName =
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
        leveMonsters = new LeveMonsterIndex(data, log);

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
        foreach (var mob in data.GetExcelSheet<BNpcName>())
        {
            if (mob.RowId == 0)
                continue;

            var mobName = mob.Singular.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(mobName))
                continue;

            if (!bnpcNameIdsByName.TryGetValue(mobName, out var ids))
            {
                ids = [];
                bnpcNameIdsByName[mobName] = ids;
            }

            ids.Add(mob.RowId);
        }

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

    public IReadOnlyList<MonsterLeveContext> GetMonsterLeves(uint? baseId, uint? nameId) =>
        leveMonsters.GetLeves(baseId, nameId);

    public IReadOnlyList<LeveMonsterTestCandidate> GetLeveMonsterTestCandidates() =>
        leveMonsters.BuildTestCandidates();

    public IReadOnlyList<CraftingRecipeInfo> GetCraftingRecipes(uint itemId) =>
        craftingRecipes.TryGetValue(itemId, out var recipes)
            ? recipes
            : Array.Empty<CraftingRecipeInfo>();

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
        // Resolution is deliberately staged. Providers contribute facts first;
        // cross-source rules operate on the complete fact set; source-family
        // normalization/deduplication follows; presentation ordering is last.
        // Keeping IO outside this method makes Resolve() deterministic and cheap.
        var sources = CollectSourceFacts(target, out var guide);
        ApplyCrossSourcePolicies(target, sources);
        var deduped = NormalizeAndOrderSources(target, sources, playerFacing);

        if (playerFacing && deduped.Count == 0 && guide is not null)
        {
            deduped.Add(new SourceEntry(
                "Guide",
                guide.Name,
                "Follow the prerequisite steps below.",
                Priority: 75,
                SpecificityHint: "Named"));
        }

        return new AcquisitionResult(target, deduped, guide);
    }

}

// ===== SourceResolver.Pipeline.cs =====
public sealed partial class SourceResolver
{
    /// <summary>
    /// Stage 1 of resolution: collect provider facts without deciding how they
    /// should be presented. New provider families should plug in here (or into a
    /// provider-specific index that feeds gameSources) instead of adding UI logic.
    /// </summary>
    private List<SourceEntry> CollectSourceFacts(TargetEntry target, out CuratedGuide? guide)
    {
        var sources = new List<SourceEntry>();

        if (gameSources.TryGetValue(target.ItemId, out var local))
            sources.AddRange(local);

        var communitySources = collectibleSources.GetSources(target.ItemId);
        var hasCollectibleAuthority = communitySources.Count > 0;
        if (hasCollectibleAuthority)
            sources.AddRange(communitySources);

        // Indirect supplemental duty joins are useful only when no exact
        // collectible authority already owns the item-to-content relationship.
        if (!hasCollectibleAuthority &&
            supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var dutyRefs))
        {
            foreach (var dutyRef in dutyRefs)
            {
                foreach (var duty in ResolveDutyReference(dutyRef))
                    sources.Add(DutySource(duty, "Supplemental duty association"));
            }
        }

        if (supplemental.ItemSources.TryGetValue(target.ItemId, out var records))
        {
            foreach (var record in records)
            {
                var source = ResolveSupplemental(record);
                if (source is null)
                    continue;

                if (hasCollectibleAuthority && source.Kind is "Duty" or "Activity")
                    continue;

                sources.Add(source);
            }
        }

        // External target-scoped discovery is just another fact source. Network
        // scheduling is owned by GarlandSourceIndex; Resolve() never initiates IO.
        sources.AddRange(garlandSources.GetSources(target.ItemId));

        var broadSystem = GetBroadSystemSource(target);
        if (broadSystem is not null)
            sources.Add(broadSystem);

        if (!HasNonCuratedSource(target.ItemId) &&
            broadSystem is null &&
            curatedFallbackSources.TryGetValue(target.ItemId, out var curated))
        {
            sources.AddRange(curated);
        }

        guide = CuratedGuides.ForTarget(target);
        if (guide is not null && sources.Count == 0)
        {
            sources.Add(new SourceEntry(
                "Curated Guide",
                guide.Name,
                "Follow the prerequisite guide below.",
                Priority: 75));
        }

        if (!target.IsUntradable)
        {
            sources.Add(new SourceEntry(
                "Market",
                "Market Board / Player Trade",
                Priority: 95));
        }

        if ((target.Category == "Fish" ||
             string.Equals(target.GameUiCategory, "Seafood", StringComparison.OrdinalIgnoreCase)) &&
            !sources.Any(source => source.Kind is "Fishing" or "Gathering"))
        {
            sources.Add(new SourceEntry(
                "Fishing",
                "Fishing / Spearfishing",
                "Exact fishing spot is not resolved yet.",
                Priority: 80));
        }

        return sources;
    }

    /// <summary>
    /// Stage 2: source-family relationships that need the complete fact set.
    /// These policies may suppress ambiguity or add dependency-derived facts,
    /// but they do not perform player-facing filtering or ordering.
    /// </summary>
    private void ApplyCrossSourcePolicies(TargetEntry target, List<SourceEntry> sources)
    {
        AddExchangeDependencySources(target.ItemId, sources);
        SuppressAmbiguousSupplementalDuties(sources);
        SuppressGenericSourcesWhenSpecificAlternativeExists(sources);
    }

    /// <summary>
    /// LuminaSupplemental exposes FateItem (item -> FATE) and MobDrop
    /// (item -> monster) but no authoritative monster -> FATE table. Keep this
    /// relationship item-scoped and conservative: only attach FATE context when
    /// the complete item fact set contains exactly one concrete monster. With
    /// multiple monsters the local data cannot say which one belongs to the FATE,
    /// so CQG deliberately leaves the relationship unattached instead of guessing.
    /// </summary>
    private void AttachConservativeItemFateContext(TargetEntry target, List<SourceEntry> sources)
    {
        var fateNames = supplemental.GetFateNamesForItem(target.ItemId);
        if (fateNames.Count == 0)
            return;

        var monsterIndexes = sources
            .Select((source, index) => (source, index))
            .Where(x => MonsterResolutionPolicy.IsConcreteMonster(x.source))
            .ToArray();

        if (monsterIndexes.Length != 1)
            return;

        var only = monsterIndexes[0];
        var merged = new HashSet<string>(
            only.source.MonsterFateNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var fateName in fateNames)
        {
            if (!string.IsNullOrWhiteSpace(fateName))
                merged.Add(fateName.Trim());
        }

        sources[only.index] = only.source with
        {
            MonsterFateNames = merged
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

    /// <summary>
    /// Battle-leve enemy membership is a local game-data relationship. Attach it
    /// after provider deduplication so every provider for the same monster shares
    /// one deterministic Levequest context list without network enrichment.
    /// </summary>
    private void AttachLeveContextToMonsters(List<SourceEntry> sources)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (!MonsterResolutionPolicy.IsConcreteMonster(source))
                continue;

            var leves = leveMonsters.GetLeves(source.MonsterBaseId, source.MonsterNameId);
            if (leves.Count == 0)
                continue;

            sources[i] = source with { MonsterLeves = leves };
        }
    }

    /// <summary>
    /// A duty-only monster has no meaningful overworld spawn coordinate. When the
    /// complete item fact set names exactly one concrete duty, attach that duty as
    /// the monster's actionable location context. Ambiguous/multiple duties are
    /// intentionally left unresolved rather than guessing.
    /// </summary>
    private static void AttachUniqueDutyContextToLocationlessMonsters(List<SourceEntry> sources)
    {
        var dutyNames = sources
            .Where(IsDutyLike)
            .Where(source => !IsUnresolvedDutyPlaceholder(source))
            .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (dutyNames.Length != 1)
            return;

        var dutyName = dutyNames[0];
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (!MonsterResolutionPolicy.IsConcreteMonster(source) ||
                MonsterResolutionPolicy.HasExactLocation(source.MonsterLocations) ||
                MonsterResolutionPolicy.HasFateContext(source) ||
                MonsterResolutionPolicy.HasLeveContext(source))
            {
                continue;
            }

            sources[i] = source with { MonsterDutyName = dutyName };
        }
    }

    /// <summary>
    /// Stage 3/4: normalize provider facts into stable source-family objects,
    /// deduplicate them, then apply one centralized presentation ordering policy.
    /// </summary>
    private List<SourceEntry> NormalizeAndOrderSources(
        TargetEntry target,
        IEnumerable<SourceEntry> sources,
        bool playerFacing)
    {
        IEnumerable<SourceEntry> normalized = sources
            .Select(EventSourcePolicy.Normalize);
        if (playerFacing)
        {
            normalized = normalized
                .Where(IsPlayerFacingActionableSource)
                .Select(ToPlayerFacingSource)
                .Select(EnrichMonsterMetadata);
        }

        var dedupedCandidates = normalized
            .GroupBy(SourceDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(MergeDuplicateSources)
            // Final family normalization happens after merge so stale/fallback
            // provider fields cannot win merely because they were merged last.
            .Select(EnrichMonsterMetadata)
            .ToList();

        AttachConservativeItemFateContext(target, dedupedCandidates);
        AttachLeveContextToMonsters(dedupedCandidates);
        AttachUniqueDutyContextToLocationlessMonsters(dedupedCandidates);

        var hasMonsterSource = dedupedCandidates.Any(SourceClassification.IsMonster);

        return dedupedCandidates
            .OrderBy(source => SourcePresentationPolicy.GetGroup(source, hasMonsterSource))
            .ThenBy(SourcePresentationPolicy.GetEffectivePriority)
            .ThenByDescending(source => source.IsUnlocked == true)
            .ThenBy(source => SourceClassification.IsMonster(source)
                ? source.MonsterZoneLevel ?? uint.MaxValue
                : 0u)
            .ThenBy(source => SourceClassification.IsMonster(source)
                ? source.MonsterLevel ?? uint.MaxValue
                : 0u)
            .ThenBy(source => source.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

// ===== SourceResolver.Helpers.cs =====
public sealed partial class SourceResolver
{
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
        {
            return "Limited / Recurring Event";
        }

        var special = SpecialAcquisitionCatalog.TryGetSource(target.Name);
        if (special is not null)
        {
            if (special.Kind.Equals("Future / Unknown Availability", StringComparison.OrdinalIgnoreCase))
                return "Future / Unknown";

            if (special.Kind.StartsWith("Out-of-game", StringComparison.OrdinalIgnoreCase))
                return "Limited / External";

            if (EventSourcePolicy.IsLegacyEvent(special))
                return "Legacy / Existing Copies";

            if (EventSourcePolicy.IsEventDerived(special))
                return "Limited / Recurring Event";
        }

        // Availability follows the best known actionable acquisition route, not
        // merely the target's UI category. This lets an exact FFXIV Collect /
        // Garland event source classify the item even when a generic fallback is
        // also present, while a stronger permanent source keeps the item
        // correctly classified as permanently obtainable. No network request is
        // started here; only already-loaded provider/cache data is inspected.
        var primary = GetKnownSourcesForItemId(target.ItemId)
            .Select(EventSourcePolicy.Normalize)
            .Where(IsPlayerFacingActionableSource)
            .Where(source => !SourceClassification.IsMarket(source))
            .OrderBy(source => source.Priority)
            .FirstOrDefault();

        if (primary is not null)
        {
            // A provider may expose a seasonal reward as an ordinary quest.
            // Exact occurrence knowledge takes precedence over the generic Quest
            // kind so an ended event quest is never labeled permanent.
            if (EventOccurrenceCatalog.TryMatch(primary, out var occurrence))
                return occurrence.Availability;

            if (primary.Kind.Equals("Future / Unknown Availability", StringComparison.OrdinalIgnoreCase))
                return "Future / Unknown";

            if (primary.Kind.StartsWith("Out-of-game", StringComparison.OrdinalIgnoreCase))
                return "Limited / External";

            if (EventSourcePolicy.IsLegacyEvent(primary))
                return "Legacy / Existing Copies";

            if (EventSourcePolicy.IsEventDerived(primary))
                return "Limited / Recurring Event";

            // A concrete non-event primary is permanently available even if an
            // event is also an alternate historical/acquisition route.
            return "Available / Permanent";
        }

        var broad = GetBroadSystemSource(target);
        if (broad is not null)
        {
            broad = EventSourcePolicy.Normalize(broad);

            if (EventSourcePolicy.IsLegacyEvent(broad))
                return "Legacy / Existing Copies";

            if (broad.Kind.StartsWith("Out-of-game", StringComparison.OrdinalIgnoreCase))
                return "Limited / External";

            if (EventSourcePolicy.IsEventDerived(broad))
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

        // Source-family completeness outranks provider provenance. A monster
        // name or level is useful partial data, but it is not an exact actionable
        // answer until the merged Monster Drop object also has a location.
        if (HasIncompleteMonsterResolution(target))
            return "Named incomplete item-specific";

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
        var bnpcNameId = SupplementalCsvIndex.FirstIdNamed(
            row,
            "BNpcName", "BNpcNameId");
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

// ===== SourceResolver.Policy.cs =====
public sealed partial class SourceResolver
{
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

    private SourceEntry EnrichMonsterMetadata(SourceEntry source)
    {
        if (!SourceClassification.IsMonster(source))
            return source;

        // Always normalize monster metadata, even when the incoming row appears
        // complete. Older cached/provider records can predate provenance flags
        // such as IsFallback, causing a representative network coordinate to
        // deserialize as apparently exact. Rebuilding from the current exact
        // monster identity on every resolver pass keeps local MobSpawn data
        // authoritative and prevents stale fallback locations from surviving.

        var monsterName = source.Name.Trim();
        if (monsterName.StartsWith("Monster: ", StringComparison.OrdinalIgnoreCase))
            monsterName = monsterName["Monster: ".Length..].Trim();

        var ids = new HashSet<uint>();
        uint? resolvedMonsterNameId = source.MonsterNameId;
        uint? resolvedMonsterBaseId = source.MonsterBaseId;
        if (source.MonsterNameId.HasValue && source.MonsterNameId.Value != 0)
        {
            // Exact identity always wins. Do not broaden an already identified
            // monster back out to every BNpcName row sharing the same display
            // name; those rows can represent different variants/contexts.
            ids.Add(source.MonsterNameId.Value);
        }
        else if (bnpcNameIdsByName.TryGetValue(monsterName, out var nameIds))
        {
            // Name-only lookup is safe only when the game itself has exactly one
            // BNpcName row with that display name. If several rows share the name,
            // combining them would fabricate a synthetic monster whose locations
            // come from multiple unrelated variants. Leave the identity unresolved
            // so the selected-target enrichment path can establish it exactly.
            var distinctIds = nameIds.Where(id => id != 0).Distinct().ToArray();
            if (distinctIds.Length == 1)
            {
                resolvedMonsterNameId = distinctIds[0];
                ids.Add(distinctIds[0]);
            }
        }

        if (resolvedMonsterNameId.HasValue && resolvedMonsterNameId.Value != 0 &&
            (!resolvedMonsterBaseId.HasValue || resolvedMonsterBaseId.Value == 0))
        {
            resolvedMonsterBaseId = supplemental.GetUniqueMobBaseId(resolvedMonsterNameId.Value);
        }

        uint? level = source.MonsterLevel;
        uint? zoneLevel = source.MonsterZoneLevel;

        // Rebuild exact locations from current local spawn data instead of trusting
        // cached/provider classification. Older cache schemas predate IsFallback,
        // so a historical Garland representative coordinate can deserialize as an
        // apparently exact location. Treat incoming locations as fallback candidates
        // until current Supplemental data independently confirms them.
        var exactLocations = new List<MonsterSpawnLocation>();
        var fallbackLocations = source.MonsterLocations?.ToList() ?? [];

        // A spawn belongs to a monster only when the exact composite identity
        // is known. BNpcName-only indexes are useful for levels/diagnostics, but
        // may combine several battle-NPC bases that happen to share a name.
        if (resolvedMonsterNameId.HasValue && resolvedMonsterNameId.Value != 0 &&
            resolvedMonsterBaseId.HasValue && resolvedMonsterBaseId.Value != 0)
        {
            var candidateLevel = supplemental.GetLowestMobLevel(
                resolvedMonsterBaseId.Value,
                resolvedMonsterNameId.Value);
            if (candidateLevel.HasValue && (!level.HasValue || candidateLevel.Value < level.Value))
                level = candidateLevel;

            var candidateZoneLevel = supplemental.GetLowestMobZoneLevel(
                resolvedMonsterBaseId.Value,
                resolvedMonsterNameId.Value);
            if (candidateZoneLevel.HasValue && (!zoneLevel.HasValue || candidateZoneLevel.Value < zoneLevel.Value))
                zoneLevel = candidateZoneLevel;

            foreach (var location in supplemental.GetMobSpawnLocations(
                         resolvedMonsterBaseId.Value,
                         resolvedMonsterNameId.Value))
            {
                if (location.IsFallback)
                {
                    if (!fallbackLocations.Any(existing =>
                            existing.TerritoryTypeId == location.TerritoryTypeId &&
                            Math.Abs(existing.X - location.X) < 0.1f &&
                            Math.Abs(existing.Y - location.Y) < 0.1f))
                    {
                        fallbackLocations.Add(location);
                    }
                    continue;
                }

                if (!exactLocations.Any(existing =>
                        existing.TerritoryTypeId == location.TerritoryTypeId &&
                        Math.Abs(existing.X - location.X) < 0.1f &&
                        Math.Abs(existing.Y - location.Y) < 0.1f))
                {
                    exactLocations.Add(location);
                }
            }
        }
        else
        {
            // Identity is still incomplete. Preserve a known name-level monster
            // level for sorting, but do not manufacture locations by unioning all
            // BNpcBase variants sharing that name.
            foreach (var id in ids)
            {
                var candidateLevel = supplemental.GetLowestMobLevel(id);
                if (candidateLevel.HasValue && (!level.HasValue || candidateLevel.Value < level.Value))
                    level = candidateLevel;
            }
        }

        // Only consult Garland's representative coordinate when no exact local
        // spawn survived the named-monster join. This preserves a useful fallback
        // without allowing it to masquerade as another exact spawn.
        MonsterSpawnLocation garlandLocation = default!;
        var hasGarlandLocation = !string.IsNullOrWhiteSpace(source.EnrichmentKey)
            ? garlandSources.TryGetCachedMobLocation(source.EnrichmentKey, out garlandLocation)
            : resolvedMonsterBaseId.HasValue && resolvedMonsterBaseId.Value != 0 &&
              resolvedMonsterNameId.HasValue && resolvedMonsterNameId.Value != 0 &&
              garlandSources.TryGetCachedMobLocation(
                  resolvedMonsterBaseId.Value,
                  resolvedMonsterNameId.Value,
                  out garlandLocation);

        var hasVerifiedExactLocation = exactLocations.Any(
            location => location.Confidence == MonsterCoordinateConfidence.Verified);

        if (!hasVerifiedExactLocation &&
            hasGarlandLocation &&
            !fallbackLocations.Any(existing =>
                existing.TerritoryTypeId == garlandLocation.TerritoryTypeId &&
                Math.Abs(existing.X - garlandLocation.X) < 0.1f &&
                Math.Abs(existing.Y - garlandLocation.Y) < 0.1f))
        {
            fallbackLocations.Add(garlandLocation);
        }

        // Verified local MobSpawn data remains authoritative. When all local
        // coordinates are explicitly Unverified, retain them but allow a verified
        // provider fallback to participate so it can become the representative.
        var locations = hasVerifiedExactLocation
            ? exactLocations
            : exactLocations.Concat(fallbackLocations).ToList();

        // FATE context is intentionally item-scoped. LuminaSupplemental gives us
        // FateItem and MobDrop relationships, but no authoritative global
        // monster -> FATE join. Do not import historical Garland mob-cache FATE
        // guesses here; AttachConservativeItemFateContext owns that policy after
        // the complete item source set is known.
        var fateNames = new HashSet<string>(
            source.MonsterFateNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        return source with
        {
            MonsterLevel = level,
            MonsterZoneLevel = zoneLevel,
            MonsterLocations = locations
                .OrderBy(x => x.Confidence)
                .ThenBy(x => x.ZoneLevel ?? uint.MaxValue)
                .ThenBy(x => x.ZoneName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.X)
                .ThenBy(x => x.Y)
                .ToArray(),
            MonsterNameId = resolvedMonsterNameId,
            MonsterBaseId = resolvedMonsterBaseId,
            MonsterFateNames = fateNames
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }

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
            name.Equals("Materia acquisition", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(name);
    }

    /// <summary>
    /// Combines duplicate provider rows without discarding useful metadata.
    ///
    /// Multiple providers can describe the same acquisition source. Monster
    /// drops are the important case: LuminaSupplemental is often the stronger
    /// relationship source while Garland's compact mob partial may carry the
    /// enemy level. Choosing only one provider row caused that level to vanish
    /// before rendering. Keep the best representative row, then merge the
    /// lowest known monster/zone levels across every duplicate.
    /// </summary>
    private static SourceEntry MergeDuplicateSources(IEnumerable<SourceEntry> duplicates)
    {
        var entries = duplicates.ToList();
        var representative = entries
            .OrderBy(x => SourceClassification.IsMarket(x) ? 1 : 0)
            .ThenBy(x => x.Priority)
            .ThenByDescending(x => x.IsUnlocked == true)
            .First();

        if (!SourceClassification.IsMonster(representative))
            return representative;

        var monsterLevel = entries
            .Where(x => x.MonsterLevel.HasValue)
            .Select(x => x.MonsterLevel!.Value)
            .DefaultIfEmpty(uint.MaxValue)
            .Min();

        var monsterZoneLevel = entries
            .Where(x => x.MonsterZoneLevel.HasValue)
            .Select(x => x.MonsterZoneLevel!.Value)
            .DefaultIfEmpty(uint.MaxValue)
            .Min();

        var allMonsterLocations = entries
            .Where(x => x.MonsterLocations is { Count: > 0 })
            .SelectMany(x => x.MonsterLocations!)
            .ToArray();

        // Verified exact local spawn coordinates are authoritative. Unverified
        // local coordinates must not hide a verified provider fallback; they stay
        // available, but sort behind every verified coordinate. Once any verified
        // exact local location exists, provider fallback locations can disappear.
        var hasVerifiedExact = allMonsterLocations.Any(x =>
            !x.IsFallback && x.Confidence == MonsterCoordinateConfidence.Verified);
        var preferredMonsterLocations = hasVerifiedExact
            ? allMonsterLocations.Where(x => !x.IsFallback)
            : allMonsterLocations.AsEnumerable();

        var monsterLocations = preferredMonsterLocations
            .GroupBy(
                x => $"{x.TerritoryTypeId}|{MathF.Round(x.X, 1)}|{MathF.Round(x.Y, 1)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(x => x.Confidence)
                .ThenBy(x => x.IsFallback)
                .First())
            .OrderBy(x => x.Confidence)
            .ThenBy(x => x.ZoneLevel ?? uint.MaxValue)
            .ThenBy(x => x.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.X)
            .ThenBy(x => x.Y)
            .ToArray();

        var exactIdentity = entries
            .FirstOrDefault(x =>
                x.MonsterBaseId.HasValue && x.MonsterBaseId.Value != 0 &&
                x.MonsterNameId.HasValue && x.MonsterNameId.Value != 0);

        // Provider transaction identity is independent from the normalized game
        // identity. Preserve it when a local row wins presentation priority so
        // the enrichment scheduler can still address the exact provider record.
        var enrichmentKey = entries
            .Select(x => x.EnrichmentKey)
            .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));

        var monsterFateNames = entries
            .Where(x => x.MonsterFateNames is { Count: > 0 })
            .SelectMany(x => x.MonsterFateNames!)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var monsterLeves = entries
            .Where(x => x.MonsterLeves is { Count: > 0 })
            .SelectMany(x => x.MonsterLeves!)
            .GroupBy(x => x.LeveId)
            .Select(group => group.First())
            .OrderBy(x => x.Level)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.LeveId)
            .ToArray();

        return representative with
        {
            MonsterLevel = monsterLevel == uint.MaxValue ? null : monsterLevel,
            MonsterZoneLevel = monsterZoneLevel == uint.MaxValue ? null : monsterZoneLevel,
            MonsterLocations = monsterLocations,
            // Keep the identity as one coherent pair. Selecting BaseId and NameId
            // independently from different provider rows can create a monster that
            // never existed and then join it to the wrong spawn list.
            MonsterNameId = exactIdentity?.MonsterNameId ?? representative.MonsterNameId,
            MonsterBaseId = exactIdentity?.MonsterBaseId ?? representative.MonsterBaseId,
            EnrichmentKey = enrichmentKey ?? representative.EnrichmentKey,
            MonsterFateNames = monsterFateNames,
            MonsterLeves = monsterLeves,
        };
    }

    private static string SourceDedupKey(SourceEntry source)
    {
        if (SourceClassification.IsLevequest(source))
        {
            var leveName = source.Name.Trim();
            if (leveName.StartsWith("Levequest: ", StringComparison.OrdinalIgnoreCase))
                leveName = leveName["Levequest: ".Length..].Trim();

            // A leve's provider provenance/detail is enrichment, not identity.
            // Local game data may contribute the level while Garland names the
            // same leve with a provider-only detail string. Merge those rows into
            // one canonical player-facing source by leve name.
            return $"Levequest|{leveName}";
        }

        if (source.Kind.Equals("Monster", StringComparison.OrdinalIgnoreCase))
        {
            var monsterName = source.Name.Trim();
            if (monsterName.StartsWith("Monster: ", StringComparison.OrdinalIgnoreCase))
                monsterName = monsterName["Monster: ".Length..].Trim();

            // Monster identity is data, not display text. Different BNpcName rows
            // can legitimately share the same localized name while representing
            // different variants/contexts. Merge providers by exact BNpcName when
            // known; only fall back to the display name when no provider resolved
            // an identity at all.
            if (source.MonsterBaseId is uint monsterBaseId && monsterBaseId != 0 &&
                source.MonsterNameId is uint monsterNameId && monsterNameId != 0)
            {
                return $"MonsterIdentity|{monsterBaseId}|{monsterNameId}";
            }

            if (source.MonsterNameId is uint nameId && nameId != 0)
                return $"MonsterNameId|{nameId}";

            return $"MonsterName|{monsterName}";
        }

        return $"{source.Kind}|{source.Name}|{source.Detail}";
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
               detail.Equals("Garland Tools leve data", StringComparison.OrdinalIgnoreCase) ||
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

    /// <summary>
    /// Returns historical event provenance independently from current acquisition
    /// ranking. Provider/event rows can otherwise disappear when a stronger live
    /// route wins presentation, even though the history is still useful context.
    /// This method intentionally collects event-derived evidence before generic
    /// source suppression and augments it with the small curated provenance catalog
    /// for legacy tradable rewards whose event relationship is no longer exposed by
    /// current game/provider data.
    /// </summary>
    public IReadOnlyList<SourceEntry> GetHistoricalProvenanceSourcesForItemId(uint itemId)
    {
        var candidateSources = new List<SourceEntry>();

        if (gameSources.TryGetValue(itemId, out var game))
            candidateSources.AddRange(game);

        candidateSources.AddRange(collectibleSources.GetSources(itemId));

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

        var target = TryTargetForMetrics(itemId);
        if (target is not null)
        {
            var broad = GetBroadSystemSource(target);
            if (broad is not null)
                candidateSources.Add(broad);

            candidateSources.AddRange(HistoricalAcquisitionCatalog.GetSources(target.Name));
        }

        return candidateSources
            .Select(EventSourcePolicy.Normalize)
            .Where(EventSourcePolicy.IsEventDerived)
            .GroupBy(s => $"{s.Kind}|{s.Name}|{s.Detail}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.Priority).First())
            .OrderBy(x => EventOccurrenceCatalog.TryMatch(x, out _) ? 0 : 1)
            .ThenBy(x => x.Priority)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
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
}
