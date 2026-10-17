using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

// ===== SourceResolver.BroadSystemSources.cs =====
public sealed partial class SourceResolver
{

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

}

// ===== SourceResolver.SystemFallbacks.cs =====
public sealed partial class SourceResolver
{

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

}

// ===== SourceResolver.SpecializedProviders.cs =====
public sealed partial class SourceResolver
{
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

    private static string NormalizeMonsterName(string name)
    {
        var normalized = name.Trim();
        return normalized.StartsWith("Monster: ", StringComparison.OrdinalIgnoreCase)
            ? normalized["Monster: ".Length..].Trim()
            : normalized;
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
        // Re-querying Garland cannot make them actionable until that changes.
        if (SpecialAcquisitionCatalog.TryGetSource(target.Name)?.Kind.Equals(
                "Future / Unknown Availability",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        var existingGarland = garlandSources.GetSources(target.ItemId);
        var providerSources = GetNonGarlandProviderSources(target);

        // Monster drops have one completion contract: identity + level +
        // actionable context (exact overworld location or one named duty). Do not
        // let a known name, a known level, or an unrelated
        // Garland route independently declare the item "enriched". Merge every
        // provider's view of each monster first, then ask whether the resulting
        // monster object is complete.
        var monsters = BuildMonsterCompletionView(providerSources, existingGarland);
        if (monsters.Count > 0)
        {
            foreach (var monster in monsters.Where(MonsterResolutionPolicy.NeedsEnrichment))
            {
                // The item document is the only authority for Garland's raw mob
                // document key. If no provider key has been discovered yet, the
                // item document is still needed even when local game data already
                // knows a BNpcBase/BNpcName pair. Once the key exists, validate
                // that exact provider document rather than reconstructing a lookup
                // key from normalized game identity.
                if (string.IsNullOrWhiteSpace(monster.EnrichmentKey))
                    return true;

                if (!garlandSources.IsMobMetadataValidated(monster.EnrichmentKey))
                    return true;
            }
        }

        // If Garland has already supplied a named/actionable route and the
        // monster completion contract above has no outstanding request, do not
        // ask for the same item again merely because a weaker provider is vague.
        if (existingGarland.Any(IsActionableItemSpecificSource))
            return false;

        if (providerSources.Any(IsIncompleteShopSource))
            return true;

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

        return true;
    }

    /// <summary>
    /// Builds the same merged monster objects used by the player-facing resolver
    /// without resolving unrelated acquisition families. This is the single
    /// quality gate for monster enrichment, so provider-specific checks cannot
    /// fight over whether a half-populated monster is "done".
    /// </summary>
    private IReadOnlyList<SourceEntry> BuildMonsterCompletionView(
        IEnumerable<SourceEntry> localSources,
        IEnumerable<SourceEntry> garlandSourcesForItem)
    {
        var allSources = localSources.Concat(garlandSourcesForItem).ToArray();
        var monsters = allSources
            .Where(SourceClassification.IsMonster)
            .Where(MonsterResolutionPolicy.IsConcreteMonster)
            .Select(ToPlayerFacingSource)
            .Select(EnrichMonsterMetadata)
            .GroupBy(SourceDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(MergeDuplicateSources)
            // Completion checks must inspect the same post-merge normalized object
            // that the UI receives; otherwise stale provider metadata can survive
            // deduplication and falsely affect completeness.
            .Select(EnrichMonsterMetadata)
            .ToList();

        var context = allSources.ToList();
        context.AddRange(monsters);
        AttachUniqueDutyContextToLocationlessMonsters(context);

        var dutyByIdentity = context
            .Where(SourceClassification.IsMonster)
            .Where(source => !string.IsNullOrWhiteSpace(source.MonsterDutyName))
            .GroupBy(SourceDedupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().MonsterDutyName!, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < monsters.Count; i++)
        {
            var key = SourceDedupKey(monsters[i]);
            if (dutyByIdentity.TryGetValue(key, out var dutyName))
                monsters[i] = monsters[i] with { MonsterDutyName = dutyName };
        }

        return monsters
            .OrderBy(source => source.MonsterLevel ?? uint.MaxValue)
            .ThenBy(source => source.MonsterZoneLevel ?? uint.MaxValue)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// True when this item has one or more concrete monster-drop sources whose
    /// merged identity/level/location contract is not yet complete. Providers
    /// contribute facts; this family-level contract is the only authority that
    /// decides whether Monster Drop is resolved.
    /// </summary>
    public IReadOnlyList<SourceEntry> GetMonsterEnrichmentCandidates(TargetEntry target)
    {
        return BuildMonsterCompletionView(
                GetNonGarlandProviderSources(target),
                garlandSources.GetSources(target.ItemId))
            .Where(MonsterResolutionPolicy.NeedsEnrichment)
            .ToArray();
    }

    public bool HasIncompleteMonsterResolution(TargetEntry target)
    {
        var monsters = BuildMonsterCompletionView(
            GetNonGarlandProviderSources(target),
            garlandSources.GetSources(target.ItemId));

        // The player-facing Monster Drop result is actionable once the primary
        // recommended monster (the first/lowest-level row) has identity, level,
        // and actionable context (exact location or a unique named duty). Higher-level alternatives remain useful
        // enrichment targets, but they must not keep the primary source labeled
        // incomplete forever.
        return monsters.Count > 0 && MonsterResolutionPolicy.NeedsEnrichment(monsters[0]);
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

}
