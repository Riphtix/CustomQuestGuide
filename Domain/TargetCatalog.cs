using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Builds the target list from the live Item sheet.
///
/// Category rules are intentionally explicit. In particular, housing categories
/// are checked BEFORE collectible/unlock categories so "Wall-mounted" can never
/// be mistaken for "Mounts".
/// </summary>
public sealed class TargetCatalog
{
    private readonly IDataManager data;
    private readonly SupplementalCsvIndex supplemental;

    private readonly List<TargetEntry> targets = [];
    private readonly Dictionary<string, List<TargetEntry>> byCategory =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> jobNames = ["All"];
    private readonly HashSet<uint> liveExchangeCostItemIds = [];

    public int SupplementalStoreExclusionCount { get; private set; }
    public int SupplementalUnobtainableExclusionCount { get; private set; }
    public int LiveUnobtainableExclusionCount { get; private set; }
    public int CuratedExclusionCount { get; private set; }
    public int VirtualProgressionExclusionCount { get; private set; }
    public int EphemeralTaskItemExclusionCount { get; private set; }
    public int RetiredSystemExclusionCount { get; private set; }

    public IReadOnlyList<TargetEntry> Targets => targets;

    public IReadOnlyList<string> Categories =>
        byCategory.Keys
            .OrderBy(CategorySortKey)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<string> Jobs => jobNames;

    public TargetCatalog(IDataManager data, SupplementalCsvIndex supplemental)
    {
        this.data = data;
        this.supplemental = supplemental;

        BuildJobList();
        BuildLiveExchangeCostItemIds();
        Build();
    }

    public IReadOnlyList<TargetEntry> GetTargets(
        string category,
        string selectedJob = "All")
    {
        if (!byCategory.TryGetValue(category, out var list))
            return [];

        if (selectedJob == "All" || !IsGearCategory(category))
            return list;

        return list
            .Where(t => t.EquipJobs.Contains(selectedJob))
            .ToArray();
    }

    public TargetEntry? GetById(uint itemId) =>
        targets.FirstOrDefault(t => t.ItemId == itemId);

    public static bool IsGearCategory(string category) =>
        category is "Weapons" or "Tools" or "Armor" or "Accessories";

    private void BuildJobList()
    {
        try
        {
            foreach (var job in data.GetExcelSheet<ClassJob>())
            {
                var abbr = job.Abbreviation.ExtractText().Trim();

                if (string.IsNullOrWhiteSpace(abbr))
                    continue;

                // ClassJob contains both base classes and jobs. For filtering,
                // showing all equip-compatible abbreviations is useful and future-proof.
                if (!jobNames.Contains(abbr, StringComparer.OrdinalIgnoreCase))
                    jobNames.Add(abbr);
            }

            jobNames.Sort((a, b) =>
            {
                if (a == "All") return -1;
                if (b == "All") return 1;
                return StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });
        }
        catch
        {
            // Keep "All" even if the ClassJob sheet changes unexpectedly.
        }
    }

    private void BuildLiveExchangeCostItemIds()
    {
        // Any real Item row currently referenced as the cost of a live
        // SpecialShop exchange is a meaningful CQG prerequisite target, even if
        // an availability dataset categorizes it as unobtainable/store-like.
        // This keeps currencies such as Silver Chocobo Feathers selectable.
        // Row IDs 0 and 1 are excluded because SpecialShop uses 1 as a
        // non-item currency sentinel in many shops.
        try
        {
            foreach (var shop in data.GetExcelSheet<SpecialShop>())
            {
                foreach (var trade in shop.Item)
                {
                    foreach (var cost in trade.ItemCosts)
                    {
                        var itemId = cost.ItemCost.RowId;
                        if (itemId > 1 && cost.CurrencyCost > 0)
                            liveExchangeCostItemIds.Add(itemId);
                    }
                }
            }
        }
        catch
        {
            // Catalog construction must remain resilient to sheet/schema
            // changes; ordinary target filtering still works without this hint.
        }
    }

    private void Build()
    {
        foreach (var item in data.GetExcelSheet<Item>())
        {
            if (item.RowId == 0)
                continue;

            var name = item.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Some legitimate targets intentionally have no normal in-game
            // reverse relationship: official contest rewards, merchandise and
            // partner-promotion codes, and client-visible future/unreleased
            // items. Preserve those known families even when an availability
            // dataset currently calls the row unobtainable or store-like.
            var preserveSpecialAcquisition =
                SpecialAcquisitionCatalog.ShouldPreserveAsTarget(name) ||
                liveExchangeCostItemIds.Contains(item.RowId);

            if (!preserveSpecialAcquisition &&
                supplemental.StoreItems.Contains(item.RowId))
            {
                SupplementalStoreExclusionCount++;
                continue;
            }

            if (!preserveSpecialAcquisition &&
                supplemental.UnobtainableItems.Contains(item.RowId))
            {
                SupplementalUnobtainableExclusionCount++;
                continue;
            }

            var uiCategory = TryGetUiCategoryName(item);

            var filterGroup =
                (uint?)ReflectionUtil.GetNumericProperty(item, "FilterGroup") ?? 0;

            // These Item rows are implementation records for progression values,
            // not inventory objects a player can possess/select/use.
            //
            // FG38 = Bozjan Mettle
            // FG52 = Occult Crescent Knowledge
            // FG53 = Phantom EXP
            // FG59 = per-class Cosmic Tool Mastery Points
            //
            // Spendable currencies such as Cowries, Cosmocredits, sector
            // Credits, and Dronebits are intentionally NOT filtered.
            if (IsVirtualProgressionCounter(filterGroup))
            {
                VirtualProgressionExclusionCount++;
                continue;
            }

            // Filter only clearly transient task/quest turn-in objects.
            //
            // The rule is intentionally narrow:
            // - Filter Group 12
            // - UI category Other/Miscellany
            // - untradable
            // - generic task-material naming
            //
            // This preserves real tradable materials/flowers and progression
            // resources such as relic materials, while removing objects whose
            // only purpose is to satisfy an active task that already directs
            // the player to the objective.
            if (IsEphemeralTaskItem(
                    filterGroup,
                    uiCategory,
                    item.IsUntradable,
                    name))
            {
                EphemeralTaskItemExclusionCount++;
                continue;
            }

            // The live Item sheet itself marks a large set of legacy rows as
            // "Unobtainable". Do not rely only on supplemental data for these.
            //
            // The user's v0.8.2 audit contained 1,122 unresolved rows whose
            // Game UI Category was literally "Unobtainable".
            if (!preserveSpecialAcquisition &&
                string.Equals(
                    uiCategory,
                    "Unobtainable",
                    StringComparison.OrdinalIgnoreCase))
            {
                LiveUnobtainableExclusionCount++;
                continue;
            }

            // Systems/items that are explicitly retired and no longer useful as
            // acquisition targets. Keep this count separate from the broader
            // curated exclusion bucket so the policy remains auditable.
            if (!preserveSpecialAcquisition && IsRetiredSystemItem(item.RowId, name))
            {
                RetiredSystemExclusionCount++;
                continue;
            }

            // These exclusions cannot currently be derived from the installed
            // UnobtainableItem dataset (it exposes zero rows in the user's
            // current LuminaSupplemental build). Keep them isolated and counted
            // so they are easy to replace if/when a reliable data source exists.
            if (!preserveSpecialAcquisition && IsCuratedExcluded(item.RowId, name))
            {
                CuratedExclusionCount++;
                continue;
            }

            var category = Classify(item, uiCategory);

            var equipJobs = ExtractEquipJobs(item);

            var target = new TargetEntry(
                item.RowId,
                name,
                category,
                uiCategory,
                equipJobs,
                item.IsUntradable,
                filterGroup);

            targets.Add(target);

            if (!byCategory.TryGetValue(category, out var list))
            {
                list = [];
                byCategory[category] = list;
            }

            list.Add(target);
        }

        // Keep Mounts visible in the category picker even if a future schema
        // change breaks classification; an empty Mounts category is much easier
        // to diagnose than a category that silently disappears.
        if (!byCategory.ContainsKey("Mounts"))
            byCategory["Mounts"] = [];

        if (!byCategory.ContainsKey("Outfits"))
            byCategory["Outfits"] = [];

        foreach (var list in byCategory.Values)
            list.Sort((a, b) =>
                StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    private IReadOnlySet<string> ExtractEquipJobs(Item item)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Item.ClassJobCategory is a RowRef. We use reflection for the
            // generated category row because its boolean job columns can evolve.
            var rowRef = item.ClassJobCategory;
            var valueProperty = rowRef.GetType().GetProperty("Value");
            var categoryRow = valueProperty?.GetValue(rowRef);

            if (categoryRow is null)
                return result;

            var properties = categoryRow
                .GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public);

            foreach (var job in jobNames)
            {
                if (job == "All")
                    continue;

                var property = properties.FirstOrDefault(p =>
                    string.Equals(
                        p.Name,
                        job,
                        StringComparison.OrdinalIgnoreCase));

                if (property is null)
                    continue;

                var raw = property.GetValue(categoryRow);

                if (AsBool(raw))
                    result.Add(job);
            }
        }
        catch
        {
            // Non-gear items commonly have no meaningful ClassJobCategory.
        }

        return result;
    }

    private static bool AsBool(object? value)
    {
        if (value is null)
            return false;

        if (value is bool b)
            return b;

        try
        {
            return Convert.ToInt32(value) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string TryGetUiCategoryName(Item item)
    {
        try
        {
            return item.ItemUICategory.Value.Name.ExtractText().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsVirtualProgressionCounter(uint filterGroup) =>
        filterGroup is 38 or 52 or 53 or 59;

    private static bool IsEphemeralTaskItem(
        uint filterGroup,
        string uiCategory,
        bool isUntradable,
        string name)
    {
        if (filterGroup != 12 || !isUntradable)
            return false;

        if (!string.Equals(uiCategory, "Other", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uiCategory, "Miscellany", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] taskSuffixes =
        [
            " Materials",
            " Components",
            " Ingredients",
            " Supplies",
            " Paraphernalia",
            " Material",
            " Parts",
            " Miscellany",
        ];

        return taskSuffixes.Any(suffix =>
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetiredSystemItem(uint itemId, string name)
    {
        // Patch 5.1 consolidated the old job-specific delineations into
        // Crafter's Delineation.
        if (name.EndsWith(" Delineation", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                name,
                "Crafter's Delineation",
                StringComparison.OrdinalIgnoreCase))
            return true;

        // Patch 5.1 removed the use/effect of the old desynthesis-reset books.
        if (name.EndsWith(" for Beginners", StringComparison.OrdinalIgnoreCase))
            return true;

        // Heavensward's Favor gathering system is retired.
        if (name.StartsWith("Matron's Favor (", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Traders' Favor (", StringComparison.OrdinalIgnoreCase))
            return true;

        string[] retiredFavorMaterials =
        [
            "Red Ooid",
            "Green Ooid",
            "Blue Ooid",
            "Yellow Ooid",
            "Violet Ooid",
            "Red Roundstone",
            "Green Roundstone",
            "Blue Roundstone",
            "Yellow Roundstone",
            "Violet Roundstone",
            "Foxglove",
            "Bladeleaf",
            "Wyvern's Wart",
            "Void Nut",
            "Black Swan Feather",
            "Bitter Foxglove",
            "Swordleaf",
            "Dragon's Wart",
            "Heavensnut",
            "Mute Swan Feather",
        ];

        if (retiredFavorMaterials.Any(x =>
                string.Equals(name, x, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Legacy 1.x-only acquisition families that still exist as Item rows but
        // no longer have a modern acquisition route. These are not useful CQG
        // targets even when old copies can still exist on characters/markets.
        if (name.StartsWith("Allagan Runestone - ", StringComparison.OrdinalIgnoreCase))
            return true;

        string[] legacyDeityRings =
        [
            "Althyk's Ring", "Azeyma's Ring", "Byregot's Ring", "Halone's Ring",
            "Llymlaen's Ring", "Menphina's Ring", "Nald'thal's Ring",
            "Nophica's Ring", "Nymeia's Ring", "Oschon's Ring",
            "Rhalgr's Ring", "Thaliak's Ring",
        ];
        if (legacyDeityRings.Any(x => string.Equals(name, x, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Legacy beast-tribe / hamlet currencies and promissory notes.
        if (name.EndsWith(" Promissory Note", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Amalj'ok", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Psashp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Gobcog", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Ixali ", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("knot", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Sylphic ", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("leaf", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Titan ", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("piece", StringComparison.OrdinalIgnoreCase))
            return true;

        // 1.x crafting intermediates removed from the modern crafting system.
        // The current catalog exposes these as ordinary material rows even though
        // they have no contemporary recipe/gathering/vendor relationship.
        string[] legacyMaterialSuffixes =
        [
            " Plank", " Plate", " Rivets", " Dust", " Branch",
        ];
        if (itemId < 7000 && legacyMaterialSuffixes.Any(suffix =>
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (itemId < 7000 &&
            (name.Contains(" Slag", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(" Moraine", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("Radiant Eye of ", StringComparison.OrdinalIgnoreCase) ||
             name is "Umbral Eye" or "Umbral Moraine"))
            return true;

        string[] legacyMaterialNames =
        [
            "Arrowwood Branch", "Basilisk Leather", "Basilisk Skin",
            "Bee Basket", "Biast Skin", "Buffalo Hide", "Buffalo Horn",
            "Buffalo Leather", "Chalk", "Coal Tar", "Cockatrice Feather",
            "Condor Feather", "Cotton Stuffing", "Dormouse Pelt", "Drake Skin",
            "Dream Boots Materials", "Dream Hat Materials", "Dream Tunic Materials",
            "Firefly Elytron", "Flint Stone", "Gnat Wing", "Goobbue Skin",
            "Hedgemole Spine", "Hellhound Fang", "Hellhound Hide",
            "Hippogryph Talon", "Hog Hide", "Jackal Fang", "Jackal Hide",
            "Ladybug Elytron", "Lindwurm Skin", "Marmot Pelt", "Miser's Mythril",
            "Nakki Leather", "Nakki Skin", "Nephrite", "Rat Pelt", "Ratstool",
            "Rattan Lumber", "Resin", "River Sand", "Rubber Band",
            "Salamander Tail", "Sea Sand", "Silver Leaf", "Squirrel Pelt",
            "Supple Spruce Branch", "Tortoiseshell", "Uncultured Pearl",
            "Vulture Feather", "Weevil Elytron", "Wolf Hide", "Wolf Leather",
        ];
        if (legacyMaterialNames.Any(x => string.Equals(name, x, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Additional verified 1.x / retired-system residue from the v0.30 audit.
        // These rows remain in Item but no longer have a contemporary acquisition
        // path; keeping them as targets only creates permanently-red entries.
        string[] retiredHamletItems =
        [
            "Blessed Decoy", "Blessed Pavis", "Blessed Rations",
            "Decoy Parts (Blessing)", "Decoy Parts (Musk)",
            "Decoy Parts (Reinforcement)", "Decoy Parts (Spikes)",
            "Fortified Rations", "Fortifying Philter",
            "Melancholic Humours", "Musked Decoy", "Musked Pavis",
            "Musked Rations", "Musky Philter",
            "Pavis Parts (Blessing)", "Pavis Parts (Musk)",
            "Pavis Parts (Reinforcement)", "Pavis Parts (Spikes)",
            "Phlegmatic Humours", "Reinforced Decoy", "Reinforced Pavis",
            "Sanctifying Philter", "Sanguine Humours",
            "Spiked Decoy", "Spiked Pavis", "Spiked Rations", "Spiking Philter",
        ];
        if (retiredHamletItems.Any(x =>
                string.Equals(name, x, StringComparison.OrdinalIgnoreCase)))
            return true;

        string[] retiredLegacyRows =
        [
            "Allagan Band", "Aquamarine Cross", "Emerald Bough", "Emerald Leaf",
            "Enchiridion", "Gold Scales", "Lapis Cross", "On the Properties of Beastmen",
            "Oschon's Finger", "Platinum Scales", "Unsealed Memorandum",
            "Wanted: Gauwyn the Gannet",
            // Removed crafting/glamour intermediates.
            "Grade 2 Clear Prism", "Grade 3 Clear Prism",
            "Grade 4 Clear Prism", "Grade 5 Clear Prism",
            "Lunar Curtain",
            // Additional verified Legacy material rows.
            "Black Quartz", "Indigo Quartz", "White Quartz",
            "Chestnut Log", "Chestnut Lumber",
            "Lauan Log", "Lauan Lumber", "Willow Log", "Willow Lumber",
            "Raptor Talon", "Gold Nugget", "Dalamud Nut", "Moon Nut",
            "Sunflower Seeds",
            // Legacy/removed consumables with no renewable acquisition route.
            "Consecrated Chocolate", "Hourglass Biscuit", "Roost Biscuit",
            // Retired base Doman / Ryumyaku weapons whose replacement/Kai forms
            // remain obtainable.
            "Mokusa", "Okanehira", "Gyosei", "Yaita",
            // Retired Doman weapon-upgrade materials removed by later streamlining.
            "Doman Whetstone", "Doman Urushi", "Raw Doman Urushi",
            // Legacy / implementation-only rows that are not meaningful player targets.
            "Wood Wailer's Jacket", "Topaz Carbuncle Earring",
            "Belt of Early Antiquity", "Belt of Golden Antiquity",
            // 1.x / GM-only Judge equipment was never normal player acquisition gear.
            "Judge's Bow", "Judge's Chain Coif", "Judge's Goggles",
            "Judge's Haubergeon", "Judge's Ringbands", "Judge's Shirt",
            "Judge's Skirt", "Judge's Slops", "Judge's Sollerets",
            "Judge's Thighboots", "Judge's Vambraces",
            // Additional verified retired/unobtainable residue (v0.33).
            "Black Odoshi Cord", "Blue Odoshi Cord", "Brass Dish",
            "Deaspected Crystal", "Deaspected Cluster",
            "Goblin Mask", "Gagaroon Luck-die", "Peperoon Fate-die",
            "Ququroon Doom-die", "Goblin Cup", "Goblin Dice",
            "Kupo Nut Charm", "Rope", "The Fury's Anthem",
            "The Keeper's Hymn", "Mission Ceruleum", "Dream of the Fayth",
            // Legacy fashion-accessory item rows superseded by facewear.
            "False Oval Spectacles", "False Shaded Spectacles",
            // Unreleased/future data rows with no current player acquisition.
            "The Faces We Wear - Thick-rimmed Goggles",
            "Goobbue Table Chronometer",
            "Glade Chocobo Stable", "Highland Chocobo Stable",
            "Oasis Chocobo Stable", "Oriental Chocobo Stable",
            "Riviera Chocobo Stable",
            // v0.33 residue: expired/future/retired rows and quest-local inputs.
            "Mandragora Parasol", "Inferno Taper", "Vortex Headdress",
            "Unmarked Keystone", "Sparkling Citrus Refreshment",
            "Guild-forged Ingot", "Hengr Dhalmel Hide", "Large Gagana Skin",
            "Rusted Iron Scrap", "Velodyna Grizzly Bear Hide",
            "Turali Spice Blend", "Yok Kukuru Pod",
            // Additional v0.43 retired residue: 1.x-only / removed currencies and
            // fishing/bait rows with no modern acquisition route.
            "Commemorative Coin", "Gougan Coin", "Mullondeis Coin",
            "Box Turtle", "Miter Shell", "Navigator's Ear", "Nether Newt",
            "Scallop Shell", "Soil Creeper", "Tricorn", "Young Indigo Herring",
        ];
        if (retiredLegacyRows.Any(x =>
                string.Equals(name, x, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Competition/event certificates are proof-of-placement objects rather
        // than repeatable acquisition goals.
        if (name.EndsWith(" Certification", StringComparison.OrdinalIgnoreCase))
            return true;

        // Old Endwalker augmentation items retired by the Poetics upgrade
        // streamlining.
        string[] retiredUpgradeItems =
        [
            "Radiant Coating",
            "Radiant Roborant",
            "Radiant Twine",
            "Moonshine Brine",
            "Moonshine Shine",
            "Moonshine Twine",
        ];

        if (retiredUpgradeItems.Any(x =>
                string.Equals(name, x, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Belts are gone; their dedicated reward coffers are not useful targets.
        if (name.Contains("Waist Gear Coffer", StringComparison.OrdinalIgnoreCase))
            return true;

        // Old Omega belt/weapon-upgrade token rows retired in later
        // streamlining.
        string[] retiredOmegaTokens =
        [
            "Alphascape Chain",
            "Alphascape Crystalloid",
            "Sigmascape Chain",
            "Sigmascape Crystalloid",
        ];

        if (retiredOmegaTokens.Any(x =>
                string.Equals(name, x, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Old red/blue-scrip counterfoil system.
        if (name.StartsWith("Type-1 ", StringComparison.OrdinalIgnoreCase) &&
            (name.EndsWith(" Counterfoil", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(" Receipt", StringComparison.OrdinalIgnoreCase)))
            return true;

        // 1.x Hamlet Defense family.
        if (name.StartsWith("Militia ", StringComparison.OrdinalIgnoreCase))
            return true;

        // 1.x stronghold / Shposhae coffer keys.
        string[] legacyKeyFamilies =
        [
            "Natalan Coffer Key",
            "Shposhae Coffer Key",
            "U'Ghamaro Coffer Key",
            "Zahar'ak Coffer Key",
            "Castrum Coffer Key",
        ];

        if (legacyKeyFamilies.Any(x =>
                name.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Patch 7.5's dye consolidation retired the old individual dye rows.
        if (IsRetiredConsolidatedDye(name))
            return true;

        return false;
    }

    private static bool IsCuratedExcluded(uint itemId, string name)
    {
        return
            (itemId == 1 && name.Equals("Gil", StringComparison.OrdinalIgnoreCase)) ||
            name.Equals("Company-issue Tonic", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Faded Page", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Kabuto Mask", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Rubber Sole", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Silver Goblet", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Team Season Pack Wolf Trophy", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Dated ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Obsolete ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Isleworks ", StringComparison.OrdinalIgnoreCase) ||
            IsKnownRetiredFamily(name) ||
            name.StartsWith("Weathered ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Season ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Tales of Adventure:", StringComparison.OrdinalIgnoreCase) ||
            IsRetiredFeastWolfReward(name) ||
            IsRetiredMateria(name) ||
            IsRetiredCatalyst(name) ||
            IsRetiredScripGear(name) ||
            IsRetiredHeavenswardCurrencyGear(name) ||
            IsRetiredDiademWeapon(name) ||
            IsExpiredPromotionItem(name) ||
            IsRetiredEikonicBaseGear(name) ||
            IsRetiredFeastSeasonTwoGear(name) ||
            IsRetiredCampaignAttire(name) ||
            IsKnownOnlineStoreItem(name) ||
            IsRetiredWeaponRow(itemId) ||
            IsAuthenticStoreFurnishing(name) ||
            IsRetiredResistanceConsumable(name);
    }

    private static bool IsRetiredConsolidatedDye(string name)
    {
        // Patch 7.5 consolidated these pre-existing single-color dye items into
        // Standard Spectrum Dye / Wide Spectrum #2 Dye. The legacy items are no
        // longer obtainable as rewards or from vendors, and existing copies are
        // exchange-only at Calamity Salvagers. They are therefore not meaningful
        // acquisition targets for CQG.
        string[] retiredDyes =
        [
            "Abyssal Blue Dye",
            "Acorn Brown Dye",
            "Adamantoise Green Dye",
            "Azure Blue Dye",
            "Bark Brown Dye",
            "Bright Orange Dye",
            "Cactuar Green Dye",
            "Carmine Red Dye",
            "Ceruleum Blue Dye",
            "Chestnut Brown Dye",
            "Chocolate Brown Dye",
            "Colibri Pink Dye",
            "Cork Brown Dye",
            "Corpse Blue Dye",
            "Gloom Purple Dye",
            "Gobbiebag Brown Dye",
            "Halatali Yellow Dye",
            "Hunter Green Dye",
            "Ice Blue Dye",
            "Lilac Purple Dye",
            "Loam Brown Dye",
            "Lotus Pink Dye",
            "Meadow Green Dye",
            "Metallic Cobalt Green Dye",
            "Metallic Dark Blue Dye",
            "Metallic Pink Dye",
            "Metallic Ruby Red Dye",
            "Midnight Blue Dye",
            "Mole Brown Dye",
            "Mud Green Dye",
            "Neon Green Dye",
            "Neon Pink Dye",
            "Neon Yellow Dye",
            "Nophica Green Dye",
            "Olive Green Dye",
            "Opo-opo Brown Dye",
            "Orchard Brown Dye",
            "Pumpkin Orange Dye",
            "Qiqirn Brown Dye",
            "Raisin Brown Dye",
            "Raw Urushi",
            "Regal Purple Dye",
            "Rolanberry Red Dye",
            "Rose Pink Dye",
            "Royal Blue Dye",
            "Russet Brown Dye",
            "Rust Red Dye",
            "Salmon Pink Dye",
            "Sky Blue Dye",
            "Ul Brown Dye",
            "Violet Purple Dye",
            "Wine Red Dye",
        ];

        return retiredDyes.Any(x =>
            string.Equals(name, x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetiredWeaponRow(uint itemId)
    {
        // Confirmed retired base weapon families whose augmented/replacement
        // forms remain obtainable.
        //
        // 11586-11598: Yasha weapons (HW)
        // 21321-21336: Ryumyaku weapons (SB)
        // 40015-40034: Credendum base weapons (EW)
        return (itemId >= 11586 && itemId <= 11598) ||
               (itemId >= 21321 && itemId <= 21336) ||
               (itemId >= 40015 && itemId <= 40034);
    }

    private static bool IsAuthenticStoreFurnishing(string name)
    {
        // "Authentic" seasonal furnishings are the Online Store versions of
        // old event furnishings. CQG excludes Store-only acquisition targets.
        return name.StartsWith("Authentic ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetiredEikonicBaseGear(string name)
    {
        // Patch 5.3 retired the original i230 Eikonic sets. The visually
        // identical Augmented i240 variants remain obtainable for Poetics.
        string[] prefixes =
        [
            "Hailstorm ",
            "Hellfire ",
            "Torrent ",
            "Slipstream ",
        ];

        if (name.StartsWith("Augmented ", StringComparison.OrdinalIgnoreCase))
            return false;

        return prefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetiredFeastSeasonTwoGear(string name)
    {
        // Exclusive top-10 Feast Season 2 glamour rewards. They are retired;
        // modern recolors exist separately under Eagleclaw / Alpha Wolf.
        return name.StartsWith("Eaglebeak ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Eaglewing ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetiredCampaignAttire(string name)
    {
        // Original 2016/2017 promotional campaign versions. Current Online
        // Store equivalents use different item names (Loyal / Exclusive).
        return name.StartsWith("Butler's ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Housemaid's ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Far Eastern Journey ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownOnlineStoreItem(string name)
    {
        // The supplemental StoreItem dataset does not currently catch every
        // newest Store item, so keep a small verified family list for the
        // current audit. CQG intentionally excludes cash-shop-only targets.
        string[] prefixes =
        [
            "Alternative ",
            "Yozakura's ",
            "Zero's Luminary ",
            "Neo Citizen's ",
        ];

        return prefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetiredScripGear(string name)
    {
        // Heavensward/Stormblood crafter-gatherer scrip gear. The unaugmented
        // versions were retired; the stronger Augmented variants are the
        // versions still obtainable from current scrip vendors.
        string[] fragments =
        [
            "keep's ",
            "king's ",
            "Handmaster's ",
            "Landmaster's ",
        ];

        if (name.StartsWith("Augmented ", StringComparison.OrdinalIgnoreCase))
            return false;

        return fragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetiredHeavenswardCurrencyGear(string name)
    {
        // Retired level-60 base gear. Current acquisition uses the upgraded
        // Fabled / Augmented Eikonic equivalents.
        return name.StartsWith("Ardent ", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Primal ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetiredDiademWeapon(string name)
    {
        // Coven weapons were removed with the old Diadem overhaul.
        return name.StartsWith("Coven ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpiredPromotionItem(string name)
    {
        // Shadowbringers preorder bonus. The redemption window expired in 2019.
        return name.Equals("Aetheryte Earring", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetiredResistanceConsumable(string name)
    {
        // Status-resistance food/potions were retired when the related
        // resistance system was removed. These are not normal current targets.
        string[] exactNames =
        [
            "Bind Ward Potion",
            "Blind Ward Potion",
            "Heavy Ward Potion",
            "Poison Ward Potion",
            "Silence Ward Potion",
            "Sleep Ward Potion",
            "Slow Ward Potion",
            "Stun Ward Potion",
            "Black Drop",
            "Blue Drop",
            "Clear Drop",
            "Green Drop",
            "Purple Drop",
            "Red Drop",
            "White Drop",
            "Yellow Drop",
            "Over-aspected Crystal",
        ];

        return exactNames.Any(x =>
            string.Equals(name, x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetiredMateria(string name)
    {
        string[] legacyPrefixes =
        [
            "Strength Materia ",
            "Dexterity Materia ",
            "Vitality Materia ",
            "Intelligence Materia ",
            "Mind Materia ",
            "Fire Materia ",
            "Ice Materia ",
            "Wind Materia ",
            "Earth Materia ",
            "Lightning Materia ",
            "Water Materia ",
            "Cracked Materia ",
            "Aurelia Kiss Materia ",
            "Bison Hoof Materia ",
            "Cactuar Foot Materia ",
            "Chocobo Down Materia ",
            "Coeurl Eye Materia ",
            "Funguar Shriek Materia ",
            "Treant Root Materia ",
        ];

        return legacyPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetiredCatalyst(string name)
    {
        if (name.Contains("Glamour Prism (", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Contains("Grade 2 Glamour Prism", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Grade 3 Glamour Prism", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Grade 4 Glamour Prism", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Grade 5 Glamour Prism", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.Equals("Grade 2 Carbonized Matter", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Grade 3 Carbonized Matter", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Grade 4 Carbonized Matter", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetiredFeastWolfReward(string name)
    {
        string[] exactPrefixes =
        [
            "Lone Wolf ",
            "Pack Wolf ",
            "Bronze Pack Wolf ",
            "Silver Pack Wolf ",
            "Gold Pack Wolf ",
            "Platinum Pack Wolf ",
            "Diamond Pack Wolf ",
        ];

        return exactPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnownRetiredFamily(string name)
    {
        // Retired tomestone / currency gear where the augmented form is the
        // surviving obtainable version.
        string[] retiredPrefixes =
        [
            "Ironworks ",
            "Shire ",
            "Scaevan ",
            "Cryptlurker's ",
            "Crystarium ",
            "Deepshadow ",
            "Credendum ",
            "Lunar Envoy's ",
            "Ryumyaku ",
            "Yasha ",
            "Lost Allagan ",
            "Augmented Ala Mhigan ",

            // Old Diadem random-stat gear removed during the Diadem overhaul.
            "Mistfall ",
            "Deepmist ",
            "Mistbreak ",
            "Sunstreak ",
            "Sunburst ",
        ];

        return retiredPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string Classify(Item item, string uiCategory)
    {
        var c = uiCategory.Trim().ToLowerInvariant();

        // Mount-unlock items are NOT given a useful ItemUICategory by FFXIV;
        // most are simply "Other". The authoritative relationship is ItemAction.
        //
        // ItemAction type 1322 = register/unlock a Mount, and Data[0] is the
        // Mount row ID. This catches whistles, horns, identification keys,
        // resonators, prisms, etc. without relying on item-name suffixes.
        try
        {
            if (item.ItemAction.IsValid)
            {
                // ItemAction's generated property name is schema-version
                // dependent. Some Lumina generations expose this discriminator
                // as Action rather than Type, so do not bind to one generated
                // member at compile time.
                var actionRow = item.ItemAction.Value;

                // Current EXDSchema defines ItemAction.Action as a LINK to the
                // Action sheet, not as a numeric "Type" field. For mount-unlock
                // items that linked Action row is 1322.
                //
                // Older CQG builds tried to Convert.ToInt64(Action), which fails
                // because Action is a RowRef and silently caused every mount item
                // to fall back to "Other Items".
                var linkedActionId = ReflectionUtil.GetNestedRowId(
                    actionRow,
                    "Action");

                if (linkedActionId == 1322)
                    return "Mounts";

                // Keep compatibility with any older/different generated schema
                // that exposes a raw discriminator instead.
                var actionType = ReflectionUtil.GetNumericProperty(
                    actionRow,
                    "Type",
                    "ActionType");

                if (actionType == 1322)
                    return "Mounts";
            }
        }
        catch
        {
            // If the generated ItemAction schema changes again, continue
            // through the normal category logic instead of breaking startup.
        }

        // ---- Housing first ----
        // This is intentionally before Mounts. "Wall-mounted" contains "mount"
        // as text but is clearly a housing decoration.
        if (IsAny(c,
                "wall-mounted", "tabletop", "furnishing", "outdoor furnishing",
                "rug", "interior wall", "flooring", "chandelier",
                "roof", "exterior wall", "window", "door", "fence",
                "exterior fixture", "interior fixture", "aquarium"))
            return "Decorations & Housing";

        // Collectible/unlock item categories. Name-backed rules come first for
        // registrable unlocks whose ItemUICategory is often just "Other".
        // Expose the thing a player conceptually wants to target instead of
        // forcing them to know which generic item bucket contains its unlock item.
        if (item.Name.ExtractText().Trim().StartsWith("Modern Aesthetics -", StringComparison.OrdinalIgnoreCase))
            return "Hairstyles";
        if (item.Name.ExtractText().Trim().StartsWith("The Faces We Wear -", StringComparison.OrdinalIgnoreCase))
            return "Facewear";
        if (item.Name.ExtractText().Trim().EndsWith(" Barding", StringComparison.OrdinalIgnoreCase))
            return "Chocobo Barding";
        if (item.Name.ExtractText().Trim().EndsWith(" Riding Map", StringComparison.OrdinalIgnoreCase))
            return "Riding Maps";
        if (item.Name.ExtractText().Trim().EndsWith(" Framer's Kit", StringComparison.OrdinalIgnoreCase))
            return "Framer's Kits";

        if (c == "mount" || c == "mounts")
            return "Mounts";
        if (c == "minion" || c == "minions")
            return "Minions";
        if (c.Contains("orchestrion"))
            return "Orchestrion Rolls";
        if (c.Contains("triple triad") || c == "card")
            return "Triple Triad Cards";
        if (c.Contains("fashion accessory"))
            return "Fashion Accessories";

        // Weapons and tools.
        if (ContainsAny(c,
                "gladiator's arm", "marauder's arm", "dark knight's arm",
                "gunbreaker's arm", "lancer's arm", "pugilist's arm",
                "rogue's arm", "samurai's arm", "reaper's arm", "viper's arm",
                "archer's arm", "machinist's arm", "dancer's arm",
                "thaumaturge's arm", "arcanist's grimoire", "red mage's arm",
                "blue mage's arm", "pictomancer's arm", "conjurer's arm",
                "scholar's arm", "astrologian's arm", "sage's arm",
                "shield"))
            return "Weapons";

        if (c.Contains("primary tool") || c.Contains("secondary tool"))
            return "Tools";

        // Gear.
        if (IsAny(c, "head", "body", "hands", "legs", "feet"))
            return "Armor";

        if (IsAny(c, "earrings", "necklace", "bracelets", "ring"))
            return "Accessories";

        // Common item families.
        if (c.Contains("materia"))
            return "Materia";

        if (c.Contains("dye"))
            return "Dyes";

        if (ContainsAny(c,
                "ingredient", "stone", "metal", "lumber",
                "cloth", "leather", "bone", "reagent"))
            return "Materials";

        // Must run before generic "food" matching: "seafood" contains "food".
        if (ContainsAny(c, "fish", "seafood", "fishing tackle"))
            return "Fish";

        if (ContainsAny(c,
                "medicine", "meal", "food", "potion",
                "battle item", "catalyst"))
            return "Consumables";

        if (ContainsAny(c,
                "crystal", "currency", "tomestone",
                "company seal", "scrip", "soul crystal"))
            return "Currencies & Tokens";

        if (ContainsAny(c, "seed", "gardening"))
            return "Gardening";

        if (ContainsAny(c, "key item"))
            return "Key / Special Items";

        return "Other Items";
    }

    private static bool IsAny(string value, params string[] options) =>
        options.Any(o => string.Equals(value, o, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(value.Contains);

    private static int CategorySortKey(string category) => category switch
    {
        "Weapons" => 0,
        "Tools" => 1,
        "Armor" => 2,
        "Accessories" => 3,
        "Mounts" => 4,
        "Minions" => 5,
        "Fashion Accessories" => 6,
        "Emotes" => 7,
        "Hairstyles" => 8,
        "Facewear" => 9,
        "Chocobo Barding" => 10,
        "Riding Maps" => 11,
        "Framer's Kits" => 12,
        "Orchestrion Rolls" => 13,
        "Triple Triad Cards" => 14,
        "Decorations & Housing" => 20,
        "Materials" => 21,
        "Fish" => 22,
        "Gardening" => 23,
        "Consumables" => 24,
        "Materia" => 25,
        "Dyes" => 26,
        "Currencies & Tokens" => 27,
        _ => 100,
    };
}
