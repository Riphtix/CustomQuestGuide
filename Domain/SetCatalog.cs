using System;
using System.Collections.Generic;
using System.Linq;

namespace CustomQuestGuide;

/// <summary>
/// Broad target groups that let the player work toward a family rather than
/// selecting one inventory item at a time.
///
/// Sets are intentionally built from the live CQG target catalog. If an item is
/// filtered as Store-only / retired / unavailable, it will not leak back into
/// a set.
/// </summary>
public sealed class SetCatalog
{
    private readonly TargetCatalog catalog;
    private readonly Dictionary<string, TargetSet> byId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, uint> itemByName =
        new(StringComparer.OrdinalIgnoreCase);

    public SetCatalog(TargetCatalog catalog)
    {
        this.catalog = catalog;

        foreach (var target in catalog.Targets)
            itemByName.TryAdd(target.Name, target.ItemId);

        BuildExtremeMountSets();
        BuildSavageMountSets();
        BuildRidingMapSets();
        BuildRelicSets();
        BuildRelicToolSets();
    }

    public IReadOnlyList<TargetSet> Sets =>
        byId.Values
            .OrderBy(s => SetGroupSortKey(s.Group))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public TargetSet? Get(string id) =>
        string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out var set)
            ? null
            : set;

    private void BuildExtremeMountSets()
    {
        AddNamedSet(
            "mount-extreme-arr",
            "Mount Sets",
            "A Realm Reborn Extreme Mounts",
            "The six ARR Extreme trial horses plus Kirin, the completion reward.",
            [
                "Xanthos Whistle",
                "Gullfaxi Whistle",
                "Aithon Whistle",
                "Enbarr Whistle",
                "Markab Whistle",
                "Boreas Whistle",
                "Kirin Fife",
            ],
            ["Kirin Fife"]);

        AddNamedSet(
            "mount-extreme-hw",
            "Mount Sets",
            "Heavensward Extreme Mounts",
            "The seven Extreme-trial Lanners plus Firebird.",
            [
                "White Lanner Whistle",
                "Rose Lanner Whistle",
                "Round Lanner Whistle",
                "Warring Lanner Whistle",
                "Dark Lanner Whistle",
                "Sophic Lanner Whistle",
                "Demonic Lanner Whistle",
                "Firebird Whistle",
            ],
            ["Firebird Whistle"]);

        AddNamedSet(
            "mount-extreme-sb",
            "Mount Sets",
            "Stormblood Extreme Mounts",
            "The seven Extreme-trial Kamuy plus Kamuy of the Nine Tails.",
            [
                "Reveling Kamuy Fife",
                "Blissful Kamuy Fife",
                "Legendary Kamuy Fife",
                "Auspicious Kamuy Fife",
                "Lunar Kamuy Fife",
                "Euphonious Kamuy Fife",
                "Hallowed Kamuy Fife",
                "Fabled Kamuy Fife",
            ],
            ["Fabled Kamuy Fife"]);

        AddNamedSet(
            "mount-extreme-shb",
            "Mount Sets",
            "Shadowbringers Extreme Mounts",
            "The seven Extreme-trial Gwibers plus Landerwaffe.",
            [
                "Fae Gwiber Trumpet",
                "Innocent Gwiber Trumpet",
                "Shadow Gwiber Trumpet",
                "Ruby Gwiber Trumpet",
                "Gwiber of Light Trumpet",
                "Emerald Gwiber Trumpet",
                "Diamond Gwiber Trumpet",
                "Landerwaffe Flute",
            ],
            ["Landerwaffe Flute"]);

        AddNamedSet(
            "mount-extreme-ew",
            "Mount Sets",
            "Endwalker Extreme Mounts",
            "The seven Extreme-trial Lynxes plus Apocryphal Bahamut.",
            [
                "Lynx of Eternal Darkness Flute",
                "Lynx of Divine Light Flute",
                "Bluefeather Lynx Flute",
                "Lynx of Imperious Wind Flute",
                "Lynx of Righteous Fire Flute",
                "Lynx of Fallen Shadow Flute",
                "Lynx of Abyssal Grief Flute",
                "Apocryphal Bahamut Horn",
            ],
            ["Apocryphal Bahamut Horn"]);

        AddNamedSet(
            "mount-extreme-dt",
            "Mount Sets",
            "Dawntrail Extreme Mounts",
            "The seven Dawntrail Extreme-trial Wings plus Wings of Legacy.",
            [
                "Wings of Ruin",
                "Wings of Resolve",
                "Wings of Eternity",
                "Wings of the Knighthood",
                "Wings of Death",
                "Wings of Mist",
                "Wings of Nihility",
                "Wings of Legacy",
            ],
            ["Wings of Legacy"]);
    }


    // Verified guaranteed Savage raid mount rewards, grouped by expansion.
    // The complete member list is required before any set becomes visible.
    // Alexander Midas has no mount reward and ARR Coil has no Savage mount.
    private void BuildSavageMountSets()
    {
        AddCompleteNamedSet(
            "mount-savage-hw", "Mount Sets", "Heavensward Savage Raid Mounts",
            "Alexander Gordias and Creator Savage mount rewards (Midas has no mount).",
            ["Gobwalker Gear", "Arrhidaeus Master Key"]);

        AddCompleteNamedSet(
            "mount-savage-sb", "Mount Sets", "Stormblood Savage Raid Mounts",
            "Omega Deltascape, Sigmascape and Alphascape Savage mount rewards.",
            ["Alte Roite Prism", "Air Force Identification Key", "Model O Identification Key"]);

        AddCompleteNamedSet(
            "mount-savage-shb", "Mount Sets", "Shadowbringers Savage Raid Mounts",
            "Eden Gate, Verse and Promise Savage mount rewards.",
            ["Skyslipper Key", "Ramuh Crystal", "Eden Crystal"]);

        AddCompleteNamedSet(
            "mount-savage-ew", "Mount Sets", "Endwalker Savage Raid Mounts",
            "Pandaemonium Asphodelos, Abyssos and Anabaseios Savage mount rewards.",
            ["Demi-Phoinix Horn", "Sunforged Crystal", "Megaloambystoma Horn"]);

        AddCompleteNamedSet(
            "mount-savage-dt", "Mount Sets", "Dawntrail Savage Raid Mounts",
            "Arcadion Light-heavyweight, Cruiserweight and Heavyweight Savage mount rewards.",
            ["Monowheel S1 Identification Key", "Air-wheeler C9 Identification Key", "Lowrider T1RANT Identification Key"]);
    }

    private void AddCompleteNamedSet(
        string id, string group, string name, string description,
        IReadOnlyList<string> requiredItemNames)
    {
        // Unlike legacy catalog-backed sets, do not offer incomplete collections
        // when a named member is absent or excluded from the target catalog.
        if (requiredItemNames.Count == 0 ||
            requiredItemNames.Any(itemName => !itemByName.ContainsKey(itemName)))
            return;

        var ids = requiredItemNames.Select(itemName => itemByName[itemName]).Distinct().ToArray();
        if (ids.Length != requiredItemNames.Count)
            return;

        Add(id, group, name, description, ids, Array.Empty<uint>());
    }

    private void BuildRidingMapSets()
    {
        AddNamedSet(
            "riding-maps-arr",
            "Riding Map Sets",
            "A Realm Reborn Riding Maps",
            "Riding maps for the A Realm Reborn overworld zones.",
            [
                "Middle La Noscea Riding Map",
                "Lower La Noscea Riding Map",
                "Eastern La Noscea Riding Map",
                "Western La Noscea Riding Map",
                "Upper La Noscea Riding Map",
                "Outer La Noscea Riding Map",
                "Central Shroud Riding Map",
                "East Shroud Riding Map",
                "South Shroud Riding Map",
                "North Shroud Riding Map",
                "Western Thanalan Riding Map",
                "Central Thanalan Riding Map",
                "Eastern Thanalan Riding Map",
                "Southern Thanalan Riding Map",
                "Northern Thanalan Riding Map",
                "Coerthas Central Highlands Riding Map",
                "Mor Dhona Riding Map",
            ]);

        AddNamedSet(
            "riding-maps-hw",
            "Riding Map Sets",
            "Heavensward Riding Maps",
            "Riding maps for the six Heavensward field zones.",
            [
                "Coerthas Western Highlands Riding Map",
                "Dravanian Forelands Riding Map",
                "Dravanian Hinterlands Riding Map",
                "Churning Mists Riding Map",
                "Sea Of Clouds Riding Map",
                "Azys Lla Riding Map",
            ]);

        AddNamedSet(
            "riding-maps-sb",
            "Riding Map Sets",
            "Stormblood Riding Maps",
            "Riding maps for the six Stormblood field zones.",
            [
                "Fringes Riding Map",
                "Peaks Riding Map",
                "Lochs Riding Map",
                "Ruby Sea Riding Map",
                "Yanxia Riding Map",
                "Azim Steppe Riding Map",
            ]);

        AddNamedSet(
            "riding-maps-shb",
            "Riding Map Sets",
            "Shadowbringers Riding Maps",
            "All Shadowbringers riding maps: the six Bicolor Gemstone field-zone maps plus the Bozjan Southern Front and Zadnor maps.",
            [
                "Lakeland Riding Map",
                "Kholusia Riding Map",
                "Amh Araeng Riding Map",
                "Il Mheg Riding Map",
                "Rak'tika Greatwood Riding Map",
                "Tempest Riding Map",
                "Bozjan Southern Front Riding Map",
                "Zadnor Riding Map",
            ]);

        AddNamedSet(
            "riding-maps-shb-field-ops",
            "Riding Map Sets",
            "Shadowbringers Field Operations Riding Maps",
            "Riding maps for the Bozjan Southern Front and Zadnor.",
            [
                "Bozjan Southern Front Riding Map",
                "Zadnor Riding Map",
            ]);

        AddNamedSet(
            "riding-maps-ew",
            "Riding Map Sets",
            "Endwalker Riding Maps",
            "Shared FATE Rank 2 riding maps for the six Endwalker field zones.",
            [
                "Labyrinthos Riding Map",
                "Thavnair Riding Map",
                "Garlemald Riding Map",
                "Mare Lamentorum Riding Map",
                "Elpis Riding Map",
                "Ultima Thule Riding Map",
            ]);

        AddNamedSet(
            "riding-maps-dt",
            "Riding Map Sets",
            "Dawntrail Riding Maps",
            "All Dawntrail riding maps: the six Shared FATE Rank 2 field-zone maps plus the Occult Crescent field-operation maps.",
            [
                "Urqopacha Riding Map",
                "Kozama'uka Riding Map",
                "Yak T'el Riding Map",
                "Shaaloani Riding Map",
                "Heritage Found Riding Map",
                "Living Memory Riding Map",
                "South Horn Riding Map",
                "North Horn Riding Map",
            ]);

        AddNamedSet(
            "riding-maps-dt-field-ops",
            "Riding Map Sets",
            "Dawntrail Field Operations Riding Maps",
            "Riding maps for the Occult Crescent field-operation zones.",
            [
                "South Horn Riding Map",
                "North Horn Riding Map",
            ]);
    }

    private void BuildRelicSets()
    {
        AddPredicateSet(
            "relic-zodiac",
            "Relic Sets",
            "ARR Zodiac Relic Progression",
            "Zodiac weapon stages present in the live item catalog.",
            target =>
                (target.ItemId >= 9491 && target.ItemId <= 9501) ||
                Starts(target.Name, "Unfinished ") ||
                EndsAny(target.Name, " Atma", " Animus", " Novus", " Nexus", " Zeta"));

        AddPredicateSet(
            "relic-anima",
            "Relic Sets",
            "Heavensward Anima Relic Progression",
            "Anima weapon stages present in the live item catalog.",
            target =>
                (target.ItemId >= 13223 && target.ItemId <= 13236) ||
                (target.ItemId >= 15251 && target.ItemId <= 15264) ||
                StartsAny(target.Name, "Animated ", "Hyperconductive ", "Sharpened ") ||
                EndsAny(target.Name, " Awoken", " Lux"));

        AddPredicateSet(
            "relic-eureka",
            "Relic Sets",
            "Stormblood Eureka Relic Progression",
            "Eureka weapon stages present in the live item catalog.",
            target =>
                (target.ItemId >= 22941 && target.ItemId <= 22956) ||
                (target.ItemId >= 24675 && target.ItemId <= 24690) ||
                StartsAny(target.Name, "Hydatos ", "Pyros ", "Elemental ") ||
                EndsAny(target.Name, " Anemos", " Pagos", " Eureka", " Physeos"));

        AddPredicateSet(
            "relic-resistance",
            "Relic Sets",
            "Shadowbringers Resistance Relic Progression",
            "Resistance weapon stages present in the live item catalog.",
            target =>
                (target.ItemId >= 30228 && target.ItemId <= 30245) ||
                (target.ItemId >= 30767 && target.ItemId <= 30784) ||
                StartsAny(target.Name, "Law's Order ", "Augmented Law's Order ", "Blade's ") ||
                target.Name.EndsWith(" Recollection", StringComparison.OrdinalIgnoreCase));

        AddPredicateSet(
            "relic-manderville",
            "Relic Sets",
            "Endwalker Manderville Relic Progression",
            "Manderville weapon stages present in the live item catalog.",
            target => StartsAny(
                target.Name,
                "Amazing Manderville ",
                "Majestic Manderville ",
                "Mandervillous "));


        AddPredicateSet(
            "relic-phantom",
            "Relic Sets",
            "Dawntrail Phantom Relic Progression",
            "Phantom weapon stages present in the live item catalog.",
            target =>
                target.Name.StartsWith(
                    "Phantom ",
                    StringComparison.OrdinalIgnoreCase) &&
                !target.Name.Contains(
                    "Replica",
                    StringComparison.OrdinalIgnoreCase) &&
                EndsAny(
                    target.Name,
                    " Penumbrae",
                    " Umbrae",
                    " Obscurum",
                    " Eclipticum",
                    " Occultum"));
    }

    private void BuildRelicToolSets()
    {
        AddPredicateSet(
            "tool-skysteel",
            "Relic Tool Sets",
            "Skysteel Tool Progression",
            "Skysteel/Dragonsung/Skysung/Skybuilders' tool stages.",
            target =>
                target.Category == "Tools" &&
                StartsAny(
                    target.Name,
                    "Dragonsung ",
                    "Augmented Dragonsung ",
                    "Skysung ",
                    "Skybuilders' "));

        AddPredicateSet(
            "tool-splendorous",
            "Relic Tool Sets",
            "Splendorous Tool Progression",
            "Splendorous through Lodestar tool stages.",
            target =>
                target.Category == "Tools" &&
                StartsAny(
                    target.Name,
                    "Splendorous ",
                    "Augmented Splendorous ",
                    "Crystalline ",
                    "Chora-Zoi's ",
                    "Brilliant ",
                    "Vrandtic ",
                    "Lodestar "));

        AddPredicateSet(
            "tool-cosmic",
            "Relic Tool Sets",
            "Cosmic Tool Progression",
            "Cosmic Exploration tool stages currently present in the live catalog.",
            target =>
                target.Category == "Tools" &&
                (target.Name.Contains("Hyper", StringComparison.OrdinalIgnoreCase) ||
                 target.Name.EndsWith(" of Stars", StringComparison.OrdinalIgnoreCase)));
    }

    private void AddNamedSet(
        string id,
        string group,
        string name,
        string description,
        IReadOnlyList<string> itemNames,
        IReadOnlyList<string>? completionRewardNames = null)
    {
        var ids = itemNames
            .Where(itemByName.ContainsKey)
            .Select(itemName => itemByName[itemName])
            .Distinct()
            .ToArray();

        var completionRewardIds = (completionRewardNames ?? Array.Empty<string>())
            .Where(itemByName.ContainsKey)
            .Select(itemName => itemByName[itemName])
            .ToHashSet();

        Add(id, group, name, description, ids, completionRewardIds);
    }

    private void AddPredicateSet(
        string id,
        string group,
        string name,
        string description,
        Func<TargetEntry, bool> predicate)
    {
        var ids = catalog.Targets
            .Where(predicate)
            .Select(t => t.ItemId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        Add(id, group, name, description, ids, Array.Empty<uint>());
    }

    private void Add(
        string id,
        string group,
        string name,
        string description,
        IReadOnlyList<uint> itemIds,
        IEnumerable<uint> completionRewardItemIds)
    {
        // Do not show empty sets caused by a future catalog/schema change.
        if (itemIds.Count == 0)
            return;

        byId[id] = new TargetSet(
            id,
            group,
            name,
            description,
            itemIds,
            completionRewardItemIds.ToHashSet());
    }

    private static int SetGroupSortKey(string group) => group switch
    {
        "Mount Sets" => 0,
        "Riding Map Sets" => 5,
        "Relic Sets" => 10,
        "Relic Tool Sets" => 20,
        _ => 100,
    };

    private static bool Starts(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool StartsAny(string value, params string[] prefixes) =>
        prefixes.Any(prefix =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool EndsAny(string value, params string[] suffixes) =>
        suffixes.Any(suffix =>
            value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
