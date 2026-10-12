using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace CustomQuestGuide;

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
