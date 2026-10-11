using System;
using System.Collections.Generic;

namespace CustomQuestGuide;

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
