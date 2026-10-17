using System;
using System.Collections.Generic;
using System.Linq;

namespace CustomQuestGuide;

// ===== AcquisitionRouteAvailabilityPolicy.cs =====
/// <summary>
/// Player-facing route availability policy.
///
/// Availability belongs to an acquisition route, not to the item globally. An
/// item can simultaneously have a dead historical event source, a current
/// player-economy source, and a paid Online Store source. This policy keeps those
/// concepts separate so one stale route cannot incorrectly make the whole item
/// look unobtainable.
/// </summary>
internal static class AcquisitionRouteAvailabilityPolicy
{
    internal sealed record RouteSummary(
        SourceEntry? CurrentGameplaySource,
        SourceEntry? HistoricalEventSource,
        EventOccurrenceCatalog.EventOccurrence? HistoricalOccurrence,
        SourceEntry? KnownOnlineStoreSource,
        bool GameplayIsConditional);

    public static RouteSummary Build(TargetEntry target, IReadOnlyList<SourceEntry> sources)
    {
        var normalized = sources
            .Select(EventSourcePolicy.Normalize)
            .ToList();

        var gameplay = normalized
            .Where(source => IsCurrentGameplayRoute(target, source))
            .OrderBy(source => source.Priority)
            .FirstOrDefault();

        var historical = normalized
            .Where(EventSourcePolicy.IsEventDerived)
            .OrderBy(source => EventOccurrenceCatalog.TryMatch(source, out _) ? 0 : 1)
            .ThenBy(source => source.Priority)
            .FirstOrDefault();

        EventOccurrenceCatalog.EventOccurrence? occurrence = null;
        if (historical is not null && EventOccurrenceCatalog.TryMatch(historical, out var matched))
            occurrence = matched;

        var store = normalized
            .Where(OnlineStoreSourcePolicy.IsOnlineStore)
            .OrderBy(source => source.Priority)
            .FirstOrDefault();

        return new RouteSummary(
            gameplay,
            historical,
            occurrence,
            store,
            gameplay is not null && IsConditionalGameplayRoute(gameplay));
    }

    public static bool IsCurrentGameplayRoute(TargetEntry target, SourceEntry source)
    {
        if (EventSourcePolicy.IsEventDerived(source) ||
            OnlineStoreSourcePolicy.IsOnlineStore(source) ||
            EventSourcePolicy.IsLegacyEvent(source))
        {
            return false;
        }

        var combined = $"{source.Kind} {source.Name} {source.Detail}";
        if (source.Kind.StartsWith("Out-of-game", StringComparison.OrdinalIgnoreCase) ||
            source.Kind.Equals("Future / Unknown Availability", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("contest", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("sweepstakes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Market Board / player trade is a real, current in-game route only for
        // tradable items. The resolver normally enforces this already, but keep
        // the route policy independently truthful.
        if (SourceClassification.IsMarket(source))
            return !target.IsUntradable;

        // Resolver dependency/provenance rows are useful diagnostics but are not
        // direct ways to acquire the target itself. Never let them override an
        // ended event simply because they happen to be non-event source kinds.
        if (source.Kind.Equals("Item Conversion", StringComparison.OrdinalIgnoreCase) ||
            source.Kind.Equals("Currency Source", StringComparison.OrdinalIgnoreCase) ||
            source.Name.Equals("Item conversion", StringComparison.OrdinalIgnoreCase) ||
            source.Name.StartsWith("Source for ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Dead event currencies should never become a permanent route merely
        // because a provider exposed them as an exchange dependency.
        if (LooksLikeObsoleteEventCurrency(source))
            return false;

        return true;
    }

    public static string DescribeGameplayRoute(SourceEntry source)
    {
        if (SourceClassification.IsMarket(source))
            return "Market Board / Player Trade";

        if (SourceClassification.IsCrafting(source))
            return "Crafting";

        if (source.Name.StartsWith("Special / Currency Shop:", StringComparison.OrdinalIgnoreCase))
            return source.Name["Special / Currency Shop:".Length..].Trim();

        return source.Name;
    }

    public static bool IsConditionalGameplayRoute(SourceEntry source)
    {
        var combined = $"{source.Kind} {source.Name} {source.Detail}";
        return combined.Contains("Recompense Officer", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("Calamity Salvager", StringComparison.OrdinalIgnoreCase) &&
               combined.Contains("event", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeObsoleteEventCurrency(SourceEntry source)
    {
        var combined = $"{source.Kind} {source.Name} {source.Detail}";
        if (EventSourcePolicy.IsEventDerived(source))
            return true;

        return combined.Contains("Faire Voucher", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("seasonal voucher", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("event token", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("event currency", StringComparison.OrdinalIgnoreCase);
    }
}

// ===== EventSourcePolicy.cs =====
/// <summary>
/// Central policy for static event-source classification.
///
/// This deliberately answers only "is this an event-derived acquisition route?"
/// and "what should we call it?". It does not try to determine whether an event
/// is active right now; live availability is a separate, on-demand concern.
/// </summary>
public static class EventSourcePolicy
{
    private static readonly string[] KnownEventNames =
    [
        "Hatching-tide",
        "Moonfire Faire",
        "Firefall Faire",
        "The Rising",
        "All Saints' Wake",
        "Starlight Celebration",
        "Heavensturn",
        "Valentione's Day",
        "Little Ladies' Day",
        "Make It Rain Campaign",
        "Moogle Treasure Trove",
        "Yo-kai Watch",
        "A Nocturne for Heroes",
        "FINAL FANTASY XV",
        "The Maiden's Rhapsody",
        "FINAL FANTASY XI",
        "Breaking Brick Mountains",
        "Dragon Quest",
        "Lightning Strikes",
        "FINAL FANTASY XIII",
        "GARO",
        "Fall Guys",
        "Blunderville",
    ];

    public static SourceEntry Normalize(SourceEntry source)
    {
        if (!IsEventDerived(source))
            return source;

        var kind = source.Kind.Trim();
        var name = source.Name.Trim();

        // FFXIV Collect commonly emits "Event: <useful source text>". Once the
        // source family is known, the prefix is redundant and makes the UI look
        // like a provider dump rather than acquisition guidance.
        if (name.StartsWith("Event: ", StringComparison.OrdinalIgnoreCase))
            name = name["Event: ".Length..].Trim();

        // Moogle Treasure Trove is normalized by FFXIV Collect as its own kind,
        // but player-facing CQG policy treats it as an event family. Keep the
        // useful source text while giving all event routes one coherent kind.
        if (kind.Equals("Moogle Treasure Trove", StringComparison.OrdinalIgnoreCase))
            kind = "Event";

        // Generic provider kind + a concrete event name is named system context,
        // not an unresolved placeholder. Do not invent an exact event when only
        // the generic umbrella is known.
        var specificity = source.SpecificityHint;
        if (IsConcreteNamedEvent(name))
            specificity ??= "Named";

        return source with
        {
            Kind = kind,
            Name = name,
            SpecificityHint = specificity,
        };
    }

    public static bool IsEventDerived(SourceEntry source)
    {
        // Exact event quests can arrive from providers simply as Quest sources.
        // Preserve that source for quest guidance, but classify it as event-derived
        // for availability and presentation policy.
        if (EventOccurrenceCatalog.TryMatch(source, out _))
            return true;

        var kind = source.Kind.Trim();
        if (kind.Equals("Event", StringComparison.OrdinalIgnoreCase) ||
            kind.Equals("Seasonal Event", StringComparison.OrdinalIgnoreCase) ||
            kind.Equals("Legacy Event", StringComparison.OrdinalIgnoreCase) ||
            kind.Equals("Moogle Treasure Trove", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var combined = $"{source.Kind} {source.Name} {source.Detail}";
        return ContainsKnownEventName(combined);
    }

    public static bool IsLegacyEvent(SourceEntry source) =>
        source.Kind.Equals("Legacy Event", StringComparison.OrdinalIgnoreCase) ||
        source.Kind.Equals("Legacy Item", StringComparison.OrdinalIgnoreCase);

    public static bool IsConcreteNamedEvent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            text.Equals("Event", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Seasonal Event", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Seasonal / Collaboration Event", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ContainsKnownEventName(text);
    }

    private static bool ContainsKnownEventName(string text)
    {
        foreach (var eventName in KnownEventNames)
        {
            if (text.Contains(eventName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

// ===== OnlineStoreSourcePolicy.cs =====
internal static class OnlineStoreSourcePolicy
{
    public static bool IsOnlineStore(SourceEntry? source)
    {
        if (source is null)
            return false;

        var combined = $"{source.Kind} {source.Name} {source.Detail}";
        return combined.Contains("Online Store", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("Mog Station", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("Mogstation", StringComparison.OrdinalIgnoreCase);
    }
}

// ===== SourceClassification.cs =====
/// <summary>
/// Semantic source-family checks shared by the resolver and UI. Keeping these
/// in one place prevents source naming conventions from drifting between layers.
/// </summary>
internal static class SourceClassification
{
    public static bool IsMonster(SourceEntry? source) =>
        source is not null &&
        source.Kind.Equals("Monster", StringComparison.OrdinalIgnoreCase);

    public static bool IsLevequest(SourceEntry? source) =>
        source is not null &&
        (source.Kind.Equals("Levequest", StringComparison.OrdinalIgnoreCase) ||
         source.Name.StartsWith("Levequest:", StringComparison.OrdinalIgnoreCase));

    public static bool IsMarket(SourceEntry? source) =>
        source is not null &&
        source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase) &&
        source.Name.Equals("Market Board / Player Trade", StringComparison.OrdinalIgnoreCase);

    public static bool IsCrafting(SourceEntry? source) =>
        source is not null &&
        (source.Kind.Equals("Crafting", StringComparison.OrdinalIgnoreCase) ||
         source.Name.Equals("Crafted", StringComparison.OrdinalIgnoreCase));

    public static bool IsExchange(SourceEntry? source)
    {
        if (source is null)
            return false;

        if (source.Name.StartsWith("Special / Currency Shop", StringComparison.OrdinalIgnoreCase))
            return true;

        return source.Kind.Equals("Vendor", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Tomestones", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Scrip Exchange", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("PvP", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Hunts", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Shared FATE", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Gold Saucer", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Wondrous Tails", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Faux Hollows", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Ishgardian Restoration", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Island Sanctuary", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Cosmic Exploration", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Field Operation", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Allied Society", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Equals("Achievement Certificate", StringComparison.OrdinalIgnoreCase);
    }
}

// ===== SourcePresentationPolicy.cs =====
/// <summary>
/// Player-facing ordering rules for acquisition sources.
///
/// CQG intentionally prefers a concrete, repeatable monster farm when one is
/// known. Indirect repeatables (ventures/leves) and player trade remain useful
/// alternatives, but should not displace the direct farming route.
/// </summary>
internal static class SourcePresentationPolicy
{
    public static int GetGroup(SourceEntry source, bool hasMonsterSource)
    {
        if (!hasMonsterSource)
            return SourceClassification.IsMarket(source) ? 2 : 0;

        if (SourceClassification.IsMonster(source))
            return 0;

        if (source.Kind.Equals("Retainer Venture", System.StringComparison.OrdinalIgnoreCase) ||
            SourceClassification.IsLevequest(source))
        {
            return 2;
        }

        if (SourceClassification.IsMarket(source))
            return 3;

        return 1;
    }

    public static int GetEffectivePriority(SourceEntry source)
    {
        // When an old event reward later appears on the paid store, the event
        // occurrence remains the provenance/source family and Online Store is
        // presented as current alternate availability beneath it.
        if (EventOccurrenceCatalog.TryMatch(source, out _))
            return -100;

        if (OnlineStoreSourcePolicy.IsOnlineStore(source))
            return 10;

        return SourceClassification.IsMonster(source) ? 50 : source.Priority;
    }
}

// ===== MonsterResolutionPolicy.cs =====
/// <summary>
/// Defines the completion contract for a concrete monster-drop source.
///
/// Monster acquisition is one cohesive answer, not a collection of unrelated
/// enrichment flags. For an overworld monster drop CQG needs to know which
/// monster drops the item, the monster's level, and at least one exact actionable
/// spawn before that overworld monster source is considered complete. Context-only
/// monsters may instead be actionable through a named duty, FATE, or battle
/// levequest; those relationships do not require an overworld coordinate or a
/// synthetic monster level. Provider representative coordinates remain useful
/// fallbacks but do not satisfy the exact-spawn contract.
/// </summary>
internal static class MonsterResolutionPolicy
{
    public static bool IsConcreteMonster(SourceEntry source)
    {
        if (!SourceClassification.IsMonster(source))
            return false;

        var name = NormalizeName(source.Name);
        return !string.IsNullOrWhiteSpace(name) &&
               !name.Equals("Monster Drop", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("Monster", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsComplete(SourceEntry source) =>
        IsConcreteMonster(source) &&
        source.MonsterBaseId is > 0 &&
        source.MonsterNameId is > 0 &&
        ((source.MonsterLevel.HasValue && HasExactLocation(source.MonsterLocations)) ||
         HasDutyContext(source) ||
         HasFateContext(source) ||
         HasLeveContext(source));

    public static bool HasDutyContext(SourceEntry source) =>
        !string.IsNullOrWhiteSpace(source.MonsterDutyName);

    public static bool HasFateContext(SourceEntry source) =>
        source.MonsterFateNames is { Count: > 0 };

    public static bool HasLeveContext(SourceEntry source) =>
        source.MonsterLeves is { Count: > 0 };

    public static bool HasExactLocation(IReadOnlyList<MonsterSpawnLocation>? locations)
    {
        if (locations is null)
            return false;

        foreach (var location in locations)
        {
            if (!location.IsFallback &&
                !string.IsNullOrWhiteSpace(location.ZoneName) &&
                location.X > 0 &&
                location.Y > 0)
            {
                return true;
            }
        }

        return false;
    }

    public static bool NeedsEnrichment(SourceEntry source) =>
        IsConcreteMonster(source) && !IsComplete(source);

    public static string NormalizeName(string name)
    {
        var value = name.Trim();
        return value.StartsWith("Monster: ", StringComparison.OrdinalIgnoreCase)
            ? value["Monster: ".Length..].Trim()
            : value;
    }

    /// <summary>
    /// Returns whether the player-facing primary monster is actionable. Higher-
    /// level alternatives are optional detail and do not control the completion
    /// state of the Monster Drop source shown to the player.
    /// </summary>
    public static bool PrimaryComplete(IReadOnlyList<SourceEntry> monsters) =>
        monsters.Count > 0 && IsComplete(monsters[0]);
}
