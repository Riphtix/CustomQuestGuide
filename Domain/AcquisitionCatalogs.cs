using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CustomQuestGuide;

// ===== AreaAccessGuidanceCatalog.cs =====
/// <summary>
/// One ordered gate in a special-area progression path. A gate is normally a
/// named quest plus an optional non-quest requirement that becomes relevant
/// only when that gate itself is the player's actionable step.
/// </summary>
public sealed record AreaAccessGate(
    string QuestName,
    string? RequirementHint = null);

/// <summary>
/// Explicit registry for special field-operation / exploration locales whose maps
/// are not actionable until the character has passed a known ordered progression.
///
/// The ordered gates are deliberately data, not resolver code. This lets CQG ask
/// the same generic question for every special locale: which gate is first unmet,
/// and what formal prerequisite of that gate is actually actionable right now?
/// </summary>
public sealed record AreaAccessGuidance(
    string AreaName,
    IReadOnlyList<AreaAccessGate> Gates,
    IReadOnlyList<string> Aliases)
{
    public string UnlockQuestName => Gates.Count > 0 ? Gates[^1].QuestName : string.Empty;
}

public static class AreaAccessGuidanceCatalog
{
    private static readonly AreaAccessGuidance[] Entries =
    [
        // The Forbidden Land, Eureka. These are intentionally cumulative. A Pyros
        // target must first pass Eureka entry, then Pagos entry, then Pyros entry.
        new(
            "The Forbidden Land, Eureka Anemos",
            [
                new("And We Shall Call it Eureka",
                    "Requires a level 70 combat job and completion of the Stormblood main scenario."),
            ],
            ["Eureka Anemos", "The Forbidden Land, Eureka Anemos"]),
        new(
            "The Forbidden Land, Eureka Pagos",
            [
                new("And We Shall Call it Eureka",
                    "Requires a level 70 combat job and completion of the Stormblood main scenario."),
                new("And We Shall Call It Pagos",
                    "Requires the Eureka Anemos story to be complete and Elemental Level 20."),
            ],
            ["Eureka Pagos", "The Forbidden Land, Eureka Pagos"]),
        new(
            "The Forbidden Land, Eureka Pyros",
            [
                new("And We Shall Call it Eureka",
                    "Requires a level 70 combat job and completion of the Stormblood main scenario."),
                new("And We Shall Call It Pagos",
                    "Requires the Eureka Anemos story to be complete and Elemental Level 20."),
                new("And We Shall Call It Pyros",
                    "Requires the Eureka Pagos story to be complete and Elemental Level 35."),
            ],
            ["Eureka Pyros", "The Forbidden Land, Eureka Pyros"]),
        new(
            "The Forbidden Land, Eureka Hydatos",
            [
                new("And We Shall Call it Eureka",
                    "Requires a level 70 combat job and completion of the Stormblood main scenario."),
                new("And We Shall Call It Pagos",
                    "Requires the Eureka Anemos story to be complete and Elemental Level 20."),
                new("And We Shall Call It Pyros",
                    "Requires the Eureka Pagos story to be complete and Elemental Level 35."),
                new("And We Shall Call It Hydatos",
                    "Requires the Eureka Pyros story to be complete and Elemental Level 50."),
            ],
            ["Eureka Hydatos", "The Forbidden Land, Eureka Hydatos"]),

        // Save the Queen field operations. Where the game-data prerequisite graph
        // already owns the chain, a single gate is enough: QuestGuidanceIndex will
        // walk backwards to the first concrete unmet predecessor.
        new(
            "The Bozjan Southern Front",
            [new("Where Eagles Nest", "Requires a level 80 combat job.")],
            ["Bozjan Southern Front", "The Bozjan Southern Front"]),
        new(
            "Zadnor",
            [new("A New Playing Field", "Requires a level 80 combat job.")],
            ["Zadnor"]),

        // Dawntrail field operations.
        new(
            "The Occult Crescent: South Horn",
            [new("Unfamiliar Territory", "Requires a level 100 combat job and average item level 690.")],
            ["Occult Crescent: South Horn", "The Occult Crescent: South Horn", "South Horn"]),
        new(
            "The Occult Crescent: North Horn",
            [new("Occult Reunion", "Requires a level 100 combat job and completion of the South Horn story through Mysteries Abide.")],
            ["Occult Crescent: North Horn", "The Occult Crescent: North Horn", "North Horn"]),
    ];

    public static AreaAccessGuidance? GetByZoneName(string? zoneName)
    {
        if (string.IsNullOrWhiteSpace(zoneName))
            return null;

        var normalized = zoneName.Trim();
        return Entries.FirstOrDefault(entry =>
            entry.AreaName.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            entry.Aliases.Any(alias => alias.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
    }
}

// ===== EventOccurrenceCatalog.cs =====
/// <summary>
/// Static knowledge for event occurrences whose rewards are tied to one specific
/// run of a recurring seasonal/collaboration event. This is intentionally not a
/// live event calendar and does not make network requests.
///
/// Why this exists: provider data often exposes only the event quest name. A
/// quest such as "Earning Your Water Wings" is technically an exact reward
/// source, but presenting it as a permanent quest is misleading once the event
/// has ended. The occurrence is therefore presentation/availability context for
/// that exact quest, not a replacement acquisition source.
/// </summary>
public static class EventOccurrenceCatalog
{
    public sealed record EventOccurrence(
        string Family,
        string DisplayName,
        string Classification,
        string Availability,
        IReadOnlySet<string> QuestNames);

    private static readonly EventOccurrence[] Occurrences =
    [
        new(
            Family: "Moonfire Faire",
            DisplayName: "Moonfire Faire 2026",
            Classification: "Historical Seasonal Event",
            Availability: "Event Ended / Currently Unobtainable",
            QuestNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Earning Your Water Wings",
            }),
    ];

    public static bool TryMatch(SourceEntry source, out EventOccurrence occurrence)
    {
        var sourceName = StripSourcePrefix(source.Name);
        var questName = StripQuestLevel(sourceName);

        foreach (var candidate in Occurrences)
        {
            if (candidate.QuestNames.Contains(questName))
            {
                occurrence = candidate;
                return true;
            }
        }

        occurrence = null!;
        return false;
    }

    public static bool TryMatchQuestName(string? sourceName, out EventOccurrence occurrence)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            occurrence = null!;
            return false;
        }

        var questName = StripQuestLevel(StripSourcePrefix(sourceName));
        foreach (var candidate in Occurrences)
        {
            if (candidate.QuestNames.Contains(questName))
            {
                occurrence = candidate;
                return true;
            }
        }

        occurrence = null!;
        return false;
    }

    public static string GetQuestDisplayName(SourceEntry source)
    {
        return StripSourcePrefix(source.Name);
    }

    private static string StripSourcePrefix(string value)
    {
        var name = value.Trim();
        foreach (var prefix in new[] { "Quest: ", "Event: " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..].Trim();
        }

        return name;
    }

    private static string StripQuestLevel(string value)
    {
        return Regex.Replace(value.Trim(), @"\s*\(Lv\.?\s*\d+\)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
    }
}

// ===== HistoricalAcquisitionCatalog.cs =====
/// <summary>
/// Curated historical provenance that is not represented by a current live-game
/// reverse relationship. These rows are context only: they must never outrank a
/// current actionable route such as the Market Board, crafting, a vendor, or a
/// duty.
///
/// This catalog exists because some legacy seasonal rewards remain tradable long
/// after the event relationship disappeared from current client/provider data.
/// Keeping provenance separate from current acquisition prevents source ranking
/// from either losing history or incorrectly calling the item unobtainable.
/// </summary>
internal static class HistoricalAcquisitionCatalog
{
    private static readonly Dictionary<string, SourceEntry[]> Sources = new(
        StringComparer.OrdinalIgnoreCase)
    {
        // Legacy Moonfire Faire beachwear remains tradable, so the player economy
        // is the current acquisition route while Moonfire Faire is provenance.
        ["Sea Breeze Summer Halter"] =
        [
            HistoricalEvent("Moonfire Faire", "Original seasonal-event reward; current copies may still circulate through player trade."),
        ],
        ["Sea Breeze Summer Pareo"] =
        [
            HistoricalEvent("Moonfire Faire", "Original seasonal-event reward; current copies may still circulate through player trade."),
        ],
    };

    public static IReadOnlyList<SourceEntry> GetSources(string itemName) =>
        Sources.TryGetValue(itemName, out var sources)
            ? sources
            : Array.Empty<SourceEntry>();

    private static SourceEntry HistoricalEvent(string name, string detail) => new(
        "Legacy Event",
        name,
        detail,
        Priority: 96,
        SpecificityHint: "Named historical provenance");
}

// ===== SpecialAcquisitionCatalog.cs =====
/// <summary>
/// Acquisition families that intentionally sit outside ordinary live-game
/// reverse relationships (duty/shop/recipe/quest/etc.), plus client-visible
/// items whose release source is not live yet.
///
/// Keeping this data in one place is important for two reasons:
///  1. these items must not be discarded merely because a game/supplemental
///     sheet calls them unobtainable, and
///  2. the resolver can describe the real external/historical availability
///     instead of leaving them red.
/// </summary>
public static class SpecialAcquisitionCatalog
{
    private static readonly HashSet<string> ContestOrSweepstakesPrizes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Ahriman Choker",
        "Bluebird Earring",
        "Mandragora Choker",
        "Gaelicap",
        "Scarf of Wondrous Wit",
        "Noble Barding",
        "Modern Aesthetics - Strife",
        "Loporrit Earrings",
        "Great Paraserpent",
        "Housing Design Award MMXXV Plaque",
        "Lauded Framer's Kit",
        "Wrapped Present",
    };

    private static readonly Dictionary<string, SourceEntry> ExternalSources = new(
        StringComparer.OrdinalIgnoreCase)
    {
        // Physical official merchandise / product-code bonuses.
        ["Ballroom Etiquette - Omega Simulations"] = new(
            "Out-of-game Merchandise Bonus",
            "Omega Meister Quality Figure item code",
            "Official physical-merchandise bonus code; no normal in-game acquisition.",
            Priority: 80, SpecificityHint: "Named"),
        ["Hatchling Lamp"] = new(
            "Out-of-game Merchandise Bonus",
            "Wayward Hatchling Plush bonus code",
            "Official physical-merchandise bonus furnishing.",
            Priority: 80, SpecificityHint: "Named"),
        ["Stuffed Baby Behemoth"] = new(
            "Out-of-game Merchandise Bonus",
            "Baby Behemoth Plush bonus code",
            "Official physical-merchandise bonus furnishing.",
            Priority: 80, SpecificityHint: "Named"),
        ["Stuffed Behelmeted Serpent"] = new(
            "Out-of-game Merchandise Bonus",
            "Behelmeted Serpent of Ronka Plushie bonus code",
            "Official physical-merchandise bonus furnishing.",
            Priority: 80, SpecificityHint: "Named"),
        ["Stuffed Loporrit"] = new(
            "Out-of-game Merchandise Bonus",
            "Loporrit Plushie bonus code",
            "Official physical-merchandise bonus furnishing.",
            Priority: 80, SpecificityHint: "Named"),
        ["Stuffed Paissa Patissier"] = new(
            "Out-of-game Merchandise Bonus",
            "Paissa Patissier Plushie bonus code",
            "Official physical-merchandise bonus furnishing.",
            Priority: 80, SpecificityHint: "Named"),
        ["Stuffed Spriggan"] = new(
            "Out-of-game Merchandise Bonus",
            "Spriggan Plushie bonus code",
            "Official physical-merchandise bonus furnishing.",
            Priority: 80, SpecificityHint: "Named"),

        // Streaming / partner campaigns.
        ["Buttery Mogbiscuit"] = new(
            "Out-of-game Twitch Promotion",
            "FFXIV Twitch Viewer Rewards",
            "Distributed through official Twitch viewer-reward campaigns; campaign availability is time-limited.",
            Priority: 80, SpecificityHint: "Named"),
        ["Fat Choco Choco"] = new(
            "Out-of-game Twitch Promotion",
            "FFXIV Twitch Viewer Rewards",
            "Distributed through official Twitch viewer-reward campaigns; campaign availability is time-limited.",
            Priority: 80, SpecificityHint: "Named"),
        ["Honey Bun"] = new(
            "Out-of-game Partner Promotion",
            "Mrs. Freshley's Honey Bun promotional campaign",
            "Promotional item-code reward; no permanent normal in-game source.",
            Priority: 80, SpecificityHint: "Named"),

        // Client-visible entries with no currently published acquisition route.
        // These are deliberately NOT called unobtainable/retired: they can be
        // activated by a later event, promotion, patch, or shop update.
        ["Botanist's Earrings"] = FutureUnknown("Client data item; no published live acquisition source yet."),
        ["Fisher's Earrings"] = FutureUnknown("Client data item; no published live acquisition source yet."),
        ["Cookie Earring"] = FutureUnknown("Client data item; no published live acquisition source yet."),
        ["Halone's Earring"] = FutureUnknown("Client data item; no published live acquisition source yet."),
        ["Wind-up Köttr"] = FutureUnknown("Patch 7.55 minion present in client data; no known live source yet."),
        ["Wind-up Vincent"] = FutureUnknown("Patch 7.55 minion present in client data; no known live source yet."),
        ["Ballroom Etiquette - Breaking the Monotony"] = FutureUnknown("Emote unlock item is present in client data, but no live source is currently known."),
    };

    public static bool IsContestOrSweepstakesPrize(string name) =>
        ContestOrSweepstakesPrizes.Contains(name);

    public static bool ShouldPreserveAsTarget(string name) =>
        IsContestOrSweepstakesPrize(name) || ExternalSources.ContainsKey(name);

    public static SourceEntry? TryGetSource(string name)
    {
        if (ContestOrSweepstakesPrizes.Contains(name))
        {
            return new SourceEntry(
                "Out-of-game Contest / Sweepstakes",
                "Official FFXIV community contest / sweepstakes",
                "No normal in-game acquisition. Prize may return in future official promotions.",
                Priority: 80, SpecificityHint: "Named");
        }

        return ExternalSources.TryGetValue(name, out var source)
            ? source
            : null;
    }

    private static SourceEntry FutureUnknown(string detail) => new(
        "Future / Unknown Availability",
        "No published acquisition source yet",
        detail,
        Priority: 94);
}

// ===== CuratedGuides.cs =====
/// <summary>
/// Optional hand-curated prerequisite guides for targets where "where it drops"
/// is not enough.
///
/// Generic items do NOT require a curated guide. They can still appear in the
/// catalog and resolve activity sources. Curated guides are only added when a
/// target needs a real prerequisite chain.
/// </summary>
public static class CuratedGuides
{
    public static CuratedGuide? ForTarget(TargetEntry target)
    {
        // First-stage Endwalker relic weapons use the Manderville prerequisite
        // chain. Keep this narrow so later Amazing/Majestic/Mandervillous stages
        // are not accidentally described as the first stage.
        if (target.Category == "Weapons" &&
            target.Name.StartsWith("Manderville ", StringComparison.OrdinalIgnoreCase))
        {
            return EndwalkerMandervilleFirstStage;
        }

        return null;
    }

    public static readonly CuratedGuide EndwalkerMandervilleFirstStage =
        new(
            "Endwalker Manderville Relic Weapon",
            [
                new GuideChain(
                    "Hildibrand Adventures — A Realm Reborn",
                    "A Realm Reborn Hildibrand prerequisite chain.",
                    [
                        new("The Rise and Fall of Gentlemen", "Wymond", "Ul'dah - Steps of Nald", 9.8f, 8.7f),
                        new("Back in the Saddle", "Hildibrand", "Southern Thanalan", 21.5f, 39.1f),
                        new("After Her Own Heart", "Wymond", "Ul'dah - Steps of Nald", 9.8f, 8.7f),
                        new("The Immaculate Deception", "Ellie", "Ul'dah - Steps of Nald", 8.3f, 11.1f),
                        new("The Science of Deduction", "Ellie", "Eastern Thanalan", 10.9f, 16.4f),
                        new("The Hammer", "Hildibrand", "Western Thanalan", 20.1f, 24.6f),
                        new("Manderville Men", "Hildibrand", "Northern Thanalan", 23.6f, 23.9f),
                        new("The Three Collectors", "Hildibrand", "Western Thanalan", 13.1f, 14.1f),
                        new("The Business of Betrothal", "Ellie", "Western Thanalan", 13.0f, 14.1f),
                        new("A Burst of Inspiration", "Briardien", "Eastern La Noscea", 32.0f, 32.6f),
                        new("Seeds of Rebellion", "Tiny Trader", "Western La Noscea", 27.7f, 28.3f),
                        new("A Case of Indecency", "Hildibrand", "Eastern La Noscea", 20.5f, 21.6f),
                        new("Eight-armed and Dangerous", "Ellie", "Eastern La Noscea", 32.0f, 30.4f),
                        new("What Price Victory", "Hildibrand", "Ul'dah - Steps of Nald", 8.3f, 11.1f),
                        new("The Trouble with Truffles", "Hildibrand", "Ul'dah - Steps of Nald", 8.3f, 11.1f),
                        new("The Coliseum Conundrum", "Briardien", "Ul'dah - Steps of Thal", 14.0f, 10.0f),
                        new("Shades of Sil'dih", "Ellie", "Ul'dah - Steps of Thal", 9.8f, 11.2f),
                        new("Sibling Strife", "Hildibrand", "Western Thanalan", 28.7f, 24.9f),
                        new("Beneath the Mask", "Hildibrand", "Ul'dah - Steps of Thal", 12.0f, 11.7f),
                        new("Truths Untold", "Hyuran woman", "Ul'dah - Steps of Thal", 12.0f, 11.8f),
                        new("Her Last Vow", "Julyan", "Ul'dah - Steps of Thal", 12.0f, 11.8f),
                    ]),

                new GuideChain(
                    "Further Hildibrand Adventures — Heavensward",
                    "Heavensward Hildibrand prerequisite chain.",
                    [
                        new("A Gentleman Falls, Rather than Flies", "Nashu Mhakaracca", "The Pillars", 5.9f, 9.9f),
                        new("Don't Call It a Comeback", "Cyr", "Coerthas Western Highlands", 18.1f, 10.9f),
                        new("The Gigi Situation", "Cyr", "Foundation", 9.9f, 11.4f),
                        new("The Measure of a Mammet", "Hildibrand", "The Pillars", 6.0f, 9.9f),
                        new("A Gazebo to Call Our Own", "Cyr", "Foundation", 9.9f, 11.4f),
                        new("Don't Trust Anyone over Sixty", "Hildibrand", "Idyllshire", 6.3f, 7.8f),
                        new("The Proud and the Pointy-eyed", "Cyr", "Foundation", 9.9f, 11.4f),
                        new("If I Could Turn Back Time", "Hildibrand", "The Dravanian Forelands", 20.3f, 22.1f),
                    ]),

                new GuideChain(
                    "Even Further Hildibrand Adventures — Stormblood",
                    "Stormblood Hildibrand prerequisite chain.",
                    [
                        new("A Hingan Tale: Nashu Goes East", "Conspicuously Inconspicuous Man", "Kugane", 10.6f, 9.8f),
                        new("Life Imitates Art Imitates Life", "Nashu Mhakaracca", "Kugane", 11.7f, 8.8f),
                        new("Of Wolves and Gentlemen", "Shigure", "The Ruby Sea", 38.1f, 38.1f),
                        new("In the Eye of the Hingan", "Shigure", "Kugane", 13.2f, 13.3f),
                        new("The Blade Mislaid", "Shigure", "Kugane", 10.0f, 7.8f),
                        new("The Black Heart Beneath", "Shigure", "Kugane", 12.5f, 8.1f),
                        new("Good Swords, Good Dogs", "Hildibrand", "Kugane", 12.1f, 5.9f),
                        new("The Past Is Never Past", "Nashu Mhakaracca", "Kugane", 10.5f, 8.1f),
                        new("Don't Do the Dewprism", "Hildibrand", "Kugane", 11.7f, 8.8f),
                    ]),

                new GuideChain(
                    "Somehow Further Hildibrand Adventures — Endwalker",
                    "Endwalker Hildibrand quests required for the first Manderville weapon.",
                    [
                        new("The Sleeping Gentleman", "Excitable Youth", "Radz-at-Han", 11.8f, 11.2f),
                        new("A Soulful Reunion", "Hildibrand", "Lakeland", 25.8f, 26.0f),
                        new("Lunar Conspiracy", "Nashu Mhakaracca", "Radz-at-Han", 11.8f, 11.2f),
                        new("The Imperfect Gentleman", "Hildibrand", "Mare Lamentorum", 27.9f, 35.7f),
                    ]),

                new GuideChain(
                    "Manderville Weapon Unlock",
                    "Unlock and receive the first Manderville weapon.",
                    [
                        new(
                            "Make It a Manderville",
                            "House Manderville Manservant",
                            "Radz-at-Han",
                            11.8f,
                            11.2f,
                            "Requires a level 90 Disciple of War or Magic and 3 Manderium Meteorites."),
                    ]),
            ]);
}
