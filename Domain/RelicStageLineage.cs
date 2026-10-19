using System;
using System.Collections.Generic;
using System.Linq;

namespace CustomQuestGuide;

/// <summary>
/// Infers completed, consumed stages of linear relic upgrades. This never changes
/// inventory ownership. A later stage only implies an earlier stage when BOTH
/// item names reduce to the same weapon identity within a declared relic family.
/// Ambiguous names, branching upgrades, and unclassified stages are not inferred.
/// </summary>
internal static class RelicStageLineage
{
    private sealed record StagePattern(string Token, bool Prefix);

    // Ordered from earliest to latest. Stage tokens are declarative so future
    // relic additions do not require changes in the Watchlist or HUD code.
    // These patterns provide conservative evidence, not a complete relic catalog.
    private static readonly Dictionary<string, StagePattern[]> Stages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["relic-phantom"] =
        [
            new("Penumbrae", false), new("Umbrae", false),
            new("Obscurum", false), new("Eclipticum", false), new("Occultum", false),
        ],
        ["relic-zodiac"] =
        [
            new("Unfinished", true), new("Atma", false), new("Animus", false),
            new("Novus", false), new("Nexus", false), new("Zeta", false),
        ],
        ["relic-anima"] =
        [
            new("Animated", true), new("Awoken", false),
            new("Hyperconductive", true), new("Sharpened", true), new("Lux", false),
        ],
        ["relic-eureka"] =
        [
            new("Anemos", false), new("Pagos", false), new("Elemental", true),
            new("Pyros", true), new("Hydatos", true), new("Eureka", false),
            new("Physeos", false),
        ],
        ["relic-resistance"] =
        [
            new("Recollection", false), new("Law's Order", true),
            new("Augmented Law's Order", true), new("Blade's", true),
        ],
        ["relic-manderville"] =
        [
            new("Manderville", true), new("Amazing Manderville", true),
            new("Majestic Manderville", true), new("Mandervillous", true),
        ],
        ["tool-skysteel"] =
        [
            new("Dragonsung", true), new("Augmented Dragonsung", true),
            new("Skysung", true), new("Skybuilders'", true),
        ],
        ["tool-splendorous"] =
        [
            new("Splendorous", true), new("Augmented Splendorous", true),
            new("Crystalline", true), new("Chora-Zoi's", true),
            new("Brilliant", true), new("Vrandtic", true), new("Lodestar", true),
        ],
    };

    internal static bool UsesStages(string setId) => Stages.ContainsKey(setId);

    internal static HashSet<uint> InferAttainedStages(
        string setId,
        IReadOnlyList<TargetEntry> members,
        IReadOnlySet<uint> physicallyOwned)
    {
        var result = new HashSet<uint>(physicallyOwned);
        if (!Stages.TryGetValue(setId, out var patterns))
            return result;

        var grouped = new Dictionary<string, List<(uint Id, int Stage)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            if (!TryClassify(member.Name, patterns, out var identity, out var stage))
                continue;
            if (!grouped.TryGetValue(identity, out var entries))
                grouped[identity] = entries = [];
            entries.Add((member.ItemId, stage));
        }

        foreach (var entries in grouped.Values)
        {
            // Do not guess when multiple different items occupy the same
            // progression stage of an apparently identical weapon identity.
            if (entries.GroupBy(entry => entry.Stage).Any(group => group.Count() > 1))
                continue;
            var highest = entries.Where(entry => physicallyOwned.Contains(entry.Id))
                .Select(entry => entry.Stage).DefaultIfEmpty(-1).Max();
            if (highest < 0)
                continue;
            foreach (var entry in entries)
                if (entry.Stage <= highest)
                    result.Add(entry.Id);
        }
        return result;
    }

    private static bool TryClassify(string name, IReadOnlyList<StagePattern> patterns,
        out string identity, out int stage)
    {
        identity = string.Empty;
        stage = -1;
        if (name.Contains("Replica", StringComparison.OrdinalIgnoreCase))
            return false;

        // Check longest matches first: e.g. Augmented Dragonsung before Dragonsung.
        foreach (var candidate in patterns.Select((pattern, index) => (pattern, index))
                     .OrderByDescending(pair => pair.pattern.Token.Length))
        {
            var token = candidate.pattern.Token;
            var part = candidate.pattern.Prefix ? token + " " : " " + token;
            if (candidate.pattern.Prefix
                    ? name.StartsWith(part, StringComparison.OrdinalIgnoreCase)
                    : name.EndsWith(part, StringComparison.OrdinalIgnoreCase))
            {
                identity = candidate.pattern.Prefix ? name[part.Length..] : name[..^part.Length];
                identity = identity.Trim();
                stage = candidate.index;
                return identity.Length > 0;
            }
        }
        return false;
    }
}
