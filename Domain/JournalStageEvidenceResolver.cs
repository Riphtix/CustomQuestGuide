using System;
using System.Collections.Generic;
using System.Linq;

namespace CustomQuestGuide;

/// <summary>
/// Character-aware interpretation of journal candidates. This is deliberately
/// NON-authoritative: quest completion establishes observed evidence only.
/// The method never asserts that any journal stage is mandatory.
/// </summary>
internal static class JournalStageEvidenceResolver
{
    internal sealed record Variant(uint QuestId, IReadOnlyList<uint> PreviousQuestIds,
        bool Completed, bool InProgress);
    internal sealed record Stage(ushort Level, string Name, bool OnTerminalRoute,
        IReadOnlyList<Variant> Variants);
    internal enum StageState { Unverified, ObservedComplete, InProgress, ConflictingEvidence }
    internal sealed record StageInspection(StageState State, IReadOnlySet<uint> ObservedVariantIds);
    internal sealed record Result(IReadOnlyList<StageInspection> Stages,
        int? NextCandidateIndex, string Reason);

    internal static Result Inspect(IReadOnlyList<Stage> stages)
    {
        var inspections = new List<StageInspection>(stages.Count);
        foreach (var stage in stages)
        {
            var observed = stage.Variants.Where(v => v.Completed || v.InProgress)
                .Select(v => v.QuestId).ToHashSet();
            var completed = stage.Variants.Count(v => v.Completed);
            var active = stage.Variants.Count(v => v.InProgress);
            var state = completed + active > 1
                ? StageState.ConflictingEvidence
                : completed == 1 ? StageState.ObservedComplete
                : active == 1 ? StageState.InProgress
                : StageState.Unverified;
            inspections.Add(new StageInspection(state, observed));
        }

        // Only a *contiguous, observed* completed prefix permits a next-stage hint.
        // This is a display hint, not an assertion of mandatory membership.
        // Do not bridge optional/off-route nodes or conflicting variant observations.
        if (inspections.Any(i => i.State == StageState.ConflictingEvidence))
            return new(inspections, null, "multiple variants have conflicting character evidence");
        if (stages.Count == 0)
            return new(inspections, null, "no journal stages");
        if (inspections[0].State is not (StageState.ObservedComplete or StageState.InProgress))
            return new(inspections, null, "no observed entry-stage evidence");
        for (var i = 0; i < stages.Count; i++)
        {
            if (!stages[i].OnTerminalRoute)
                return new(inspections, null, "optional or off-route stage requires review");
            if (inspections[i].State == StageState.InProgress)
                return new(inspections, i, "observed active quest");
            if (inspections[i].State == StageState.Unverified)
                return new(inspections, i, "first unobserved stage after completed prefix");
        }
        return new(inspections, null, "all candidate stages have observed completion; membership remains unverified");
    }
}
