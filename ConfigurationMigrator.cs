using System;
using System.Collections.Generic;
using System.Linq;

namespace CustomQuestGuide;

/// <summary>
/// Centralized configuration normalization and schema migration. Migrations are
/// intentionally non-destructive: user pins and per-character Glamour Dresser
/// snapshots are preserved whenever they are structurally valid.
/// </summary>
public static class ConfigurationMigrator
{
    public const int CurrentVersion = 11;

    private static readonly HashSet<string> ValidPinKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Item",
        "Set",
        "JobUnlock",
        "JobProgression",
        "Duty",
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
