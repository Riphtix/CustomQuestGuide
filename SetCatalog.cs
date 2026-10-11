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
