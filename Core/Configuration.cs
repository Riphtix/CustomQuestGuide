using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using System.Linq;

namespace CustomQuestGuide;

// ===== Configuration.cs =====
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 12;

    public bool Detailed { get; set; } = false;
    public bool Debug { get; set; } = false;

    // Set Target is optional. Empty means normal single-item targeting.
    public string SelectedSetId { get; set; } = string.Empty;

    // The normal single-item selectors.
    public string SelectedCategory { get; set; } = "Weapons";
    public uint SelectedItemId { get; set; } = 0;

    // Non-item unlock target selections. Emotes and titles are represented by
    // their own permanent-unlock rows rather than by the consumable item that
    // may teach/award them.
    public ushort SelectedEmoteId { get; set; } = 0;
    public ushort SelectedTitleId { get; set; } = 0;

    // Gear-only filter. Empty / "All" means do not filter by class/job.
    public string SelectedJob { get; set; } = "All";

    // Detailed-mode prerequisite chain selection for curated targets.
    public int SelectedChainIndex { get; set; } = 0;

    // Jobs & Progression tab selection. These are navigation preferences, not
    // progression state; progression itself is always read from live quest data.
    public string SelectedProgressionCategory { get; set; } = "Combat Classes & Jobs";
    public uint SelectedClassJobId { get; set; } = 0;

    // Duties tab selection. Duty IDs are ContentFinderCondition row IDs.
    public string SelectedDutyCategory { get; set; } = "Trials";
    public uint SelectedDutyId { get; set; } = 0;

    // Relic-set view selection. Empty means pick the first job represented by the set.
    public string SelectedRelicJob { get; set; } = string.Empty;

    // Unified priority queue. List order is the user's priority order.
    public List<PinnedTargetConfig> PinnedTargets { get; set; } = [];

    // Per-character snapshot of Glamour Dresser contents. The game clears its
    // live dresser cache on zone changes, so relying only on ItemFinderModule
    // makes dresser ownership disappear as soon as the player leaves the inn.
    // Key = character ContentId as an invariant string.
    public Dictionary<string, List<uint>> GlamourDresserSnapshots { get; set; } = [];
}

[Serializable]
public sealed class PinnedTargetConfig
{
    // "Item", "Emote", "Title", "Set", "JobUnlock", "JobProgression", or "Duty".
    public string Kind { get; set; } = string.Empty;

    // Item ID or ClassJob row ID depending on Kind.
    public uint NumericId { get; set; } = 0;

    // Set ID for Kind == "Set".
    public string StringId { get; set; } = string.Empty;

    // Optional subtype/variant, currently used for a pinned relic set's selected job.
    public string Variant { get; set; } = string.Empty;
}

// ===== ConfigurationMigrator.cs =====
/// <summary>
/// Centralized configuration normalization and schema migration. Migrations are
/// intentionally non-destructive: user pins and per-character Glamour Dresser
/// snapshots are preserved whenever they are structurally valid.
/// </summary>
public static class ConfigurationMigrator
{
    public const int CurrentVersion = 12;

    private static readonly HashSet<string> ValidPinKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Item",
        "Set",
        "JobUnlock",
        "JobProgression",
        "Duty",
        "Emote",
        "Title",
    };

    public static bool Migrate(Configuration config)
    {
        var changed = false;

        if (config.PinnedTargets is null)
        {
            config.PinnedTargets = [];
            changed = true;
        }

        if (config.GlamourDresserSnapshots is null)
        {
            config.GlamourDresserSnapshots = [];
            changed = true;
        }

        if (config.SelectedSetId is null)
        {
            config.SelectedSetId = string.Empty;
            changed = true;
        }
        if (config.SelectedCategory is null)
        {
            config.SelectedCategory = "Weapons";
            changed = true;
        }
        if (config.SelectedJob is null)
        {
            config.SelectedJob = "All";
            changed = true;
        }
        if (config.SelectedProgressionCategory is null)
        {
            config.SelectedProgressionCategory = "Combat Classes & Jobs";
            changed = true;
        }
        if (config.SelectedDutyCategory is null)
        {
            config.SelectedDutyCategory = "Trials";
            changed = true;
        }
        if (config.SelectedRelicJob is null)
        {
            config.SelectedRelicJob = string.Empty;
            changed = true;
        }

        // v10 hardens malformed/old configuration data. Invalid pins are removed
        // rather than allowing one bad record to break the priority tab.
        var normalizedPins = config.PinnedTargets
            .Where(pin => pin is not null)
            .Where(pin => ValidPinKinds.Contains(pin.Kind ?? string.Empty))
            .Where(pin =>
                string.Equals(pin.Kind, "Set", StringComparison.OrdinalIgnoreCase)
                    ? !string.IsNullOrWhiteSpace(pin.StringId)
                    : pin.NumericId != 0)
            .Select(pin =>
            {
                pin.Kind ??= string.Empty;
                pin.StringId ??= string.Empty;
                pin.Variant ??= string.Empty;
                return pin;
            })
            .ToList();

        if (normalizedPins.Count != config.PinnedTargets.Count)
        {
            config.PinnedTargets = normalizedPins;
            changed = true;
        }

        // Snapshot payloads persist across versions. Remove impossible IDs and
        // duplicates and keep deterministic ordering so config writes stay stable.
        foreach (var key in config.GlamourDresserSnapshots.Keys.ToArray())
        {
            var values = config.GlamourDresserSnapshots[key] ?? [];
            var normalized = values.Where(id => id != 0).Distinct().OrderBy(id => id).ToList();
            if (!values.SequenceEqual(normalized))
            {
                config.GlamourDresserSnapshots[key] = normalized;
                changed = true;
            }
        }

        if (config.SelectedChainIndex < 0)
        {
            config.SelectedChainIndex = 0;
            changed = true;
        }

        if (config.Version < CurrentVersion)
        {
            config.Version = CurrentVersion;
            changed = true;
        }

        return changed;
    }
}
