using System;
using System.Collections.Generic;

namespace CustomQuestGuide;

public sealed record TargetEntry(
    uint ItemId,
    string Name,
    string Category,
    string GameUiCategory,
    IReadOnlySet<string> EquipJobs,
    bool IsUntradable,
    uint FilterGroup);


public sealed record EmoteTarget(
    ushort EmoteId,
    string Name,
    string Command,
    uint UnlockLink,
    uint BackingItemId,
    uint QuestId);

public sealed record TitleTarget(
    ushort TitleId,
    string Name,
    uint AchievementId,
    string AchievementName,
    string AchievementDescription);

public sealed record SharedFateProgressSnapshot(
    uint TerritoryTypeId,
    string ZoneName,
    byte CurrentRank,
    byte MaxRank,
    ushort FateProgress,
    ushort NeededFates);

public enum MonsterCoordinateConfidence
{
    Verified = 0,
    Unverified = 1,
}

public sealed record MonsterSpawnLocation(
    uint TerritoryTypeId,
    string ZoneName,
    float X,
    float Y,
    uint? ZoneLevel = null,
    uint? MapId = null,
    float? WorldX = null,
    float? WorldZ = null,
    bool IsFallback = false,
    MonsterCoordinateConfidence Confidence = MonsterCoordinateConfidence.Verified);

public sealed record MonsterLeveContext(
    uint LeveId,
    string Name,
    uint Level);

public sealed record SourceEntry(
    string Kind,
    string Name,
    string? Detail = null,
    bool? IsUnlocked = null,
    int Priority = 100,
    string? SpecificityHint = null,
    uint? NpcId = null,
    uint? MonsterLevel = null,
    uint? MonsterZoneLevel = null,
    IReadOnlyList<MonsterSpawnLocation>? MonsterLocations = null,
    uint? MonsterNameId = null,
    uint? MonsterBaseId = null,
    string? EnrichmentKey = null,
    string? MonsterDutyName = null,
    IReadOnlyList<string>? MonsterFateNames = null,
    IReadOnlyList<MonsterLeveContext>? MonsterLeves = null);

public sealed record CraftingIngredientInfo(
    uint ItemId,
    string Name,
    uint Amount);

public sealed record CraftingRecipeInfo(
    uint RecipeId,
    string Job,
    byte Level,
    uint AmountResult,
    IReadOnlyList<CraftingIngredientInfo> Ingredients);


public sealed record ExchangeDependencyInfo(
    uint CostItemId,
    string CostItemName,
    uint Amount,
    string ShopName,
    IReadOnlyList<string> NpcNames);

public sealed record AcquisitionResult(
    TargetEntry Target,
    IReadOnlyList<SourceEntry> Sources,
    CuratedGuide? Guide);

public sealed record SourceActionGuidance(
    string ActionLabel,
    string ActionText,
    string GoTo,
    string QuestStartedBy,
    SourceEntry? Source)
{
    public static SourceActionGuidance Empty { get; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty, null);

    public bool IsEmpty => Source is null;
}

public sealed record GuideStep(
    string QuestName,
    string QuestGiver,
    string Zone,
    float X,
    float Y,
    string? Note = null);

public sealed record GuideChain(
    string Name,
    string Description,
    IReadOnlyList<GuideStep> Steps);

public sealed record CuratedGuide(
    string Name,
    IReadOnlyList<GuideChain> Chains);


public sealed record TargetSet(
    string Id,
    string Group,
    string Name,
    string Description,
    IReadOnlyList<uint> ItemIds,
    IReadOnlySet<uint> CompletionRewardItemIds);

public sealed record JobQuestStep(
    uint QuestId,
    string Name,
    ushort Level,
    string? QuestGiver,
    string? Location);

public sealed record JobQuestRequirement(
    uint QuestId,
    string Name);

public sealed record JobProgressionTarget(
    uint ClassJobId,
    string Name,
    string Abbreviation,
    string Category,
    uint ParentClassJobId,
    string UnlockResolution,
    uint ClassJobUnlockQuestId,
    JobQuestStep? UnlockQuest,
    IReadOnlyList<JobQuestStep> Progression,
    IReadOnlyList<JobQuestRequirement> UnlockPrerequisites);


public enum QuestProgressState
{
    NotStarted,
    InProgress,
    Completed,
}

public sealed record QuestGuidanceInfo(
    uint QuestId,
    string Name,
    ushort Level,
    uint IssuerNpcId,
    string? QuestGiver,
    string? Location,
    IReadOnlyList<uint> PreviousQuestIds);

public sealed record QuestPrerequisiteResolution(
    string TargetQuestName,
    QuestGuidanceInfo? Target,
    QuestGuidanceInfo? Actionable,
    QuestProgressState ActionableState,
    IReadOnlyList<QuestGuidanceInfo> Path,
    bool IsComplete,
    bool HasUnresolvedPrerequisite)
{
    public static QuestPrerequisiteResolution Unresolved(string targetQuestName, QuestGuidanceInfo? target = null) =>
        new(targetQuestName, target, null, QuestProgressState.NotStarted, Array.Empty<QuestGuidanceInfo>(), false, true);

    public static QuestPrerequisiteResolution Complete(QuestGuidanceInfo target) =>
        new(target.Name, target, null, QuestProgressState.Completed, [target], true, false);

    public static QuestPrerequisiteResolution Action(
        QuestGuidanceInfo target,
        QuestGuidanceInfo actionable,
        QuestProgressState state,
        IReadOnlyList<QuestGuidanceInfo> path,
        bool hasUnresolvedPrerequisite = false) =>
        new(target.Name, target, actionable, state, path, false, hasUnresolvedPrerequisite);
}

public sealed record QuestSequenceEntry(
    string Name,
    QuestGuidanceInfo? Info,
    QuestProgressState RawState,
    QuestProgressState EffectiveState);

public sealed record QuestSequenceStatus(
    IReadOnlyList<QuestSequenceEntry> Entries,
    int ActiveIndex,
    int CurrentOrNextIndex,
    int AfterActiveIndex,
    bool IsComplete)
{
    public static QuestSequenceStatus Empty { get; } =
        new(Array.Empty<QuestSequenceEntry>(), -1, -1, -1, false);

    public QuestSequenceEntry? Active =>
        ActiveIndex >= 0 && ActiveIndex < Entries.Count ? Entries[ActiveIndex] : null;

    public QuestSequenceEntry? CurrentOrNext =>
        CurrentOrNextIndex >= 0 && CurrentOrNextIndex < Entries.Count ? Entries[CurrentOrNextIndex] : null;

    public QuestSequenceEntry? AfterActive =>
        AfterActiveIndex >= 0 && AfterActiveIndex < Entries.Count ? Entries[AfterActiveIndex] : null;
}
