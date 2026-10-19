using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace CustomQuestGuide;

public sealed partial class Plugin
{
    // A character's FirstClass is authoritative. Build once per job/starting class,
    // not on every UI frame or completion event. A new character changes the key.
    private readonly Dictionary<(uint JobId, uint FirstClassId), IReadOnlyList<JobQuestStep>> applicableProgressionCache = new();

    // All progression consumers use these classifications. Excluded quest records
    // are NEVER counted as completed or eligible as the next progression step.
    private enum ClassQuestRole
    {
        ApplicableProgression,
        UnlockPrerequisite,
        ClassUnlock,
        InapplicableVariant,
        Unrelated,
    }

    private sealed record ClassifiedClassQuest(JobQuestStep Step, ClassQuestRole Role, string Reason);

    private readonly Dictionary<(uint JobId, uint FirstClassId), IReadOnlyList<ClassifiedClassQuest>> classifiedProgressionCache = new();

    private IReadOnlyList<ClassifiedClassQuest> GetClassifiedProgression(JobProgressionTarget job)
    {
        // Do not cache provisional eligibility while character data is unavailable.
        var loaded = PlayerStateService.IsLoaded;
        var firstClass = loaded ? PlayerStateService.FirstClass.RowId : 0u;
        var key = (job.ClassJobId, firstClass);
        if (loaded && jobCatalog.GetVerifiedApplicableQuestIds(job.ClassJobId, firstClass) is not null &&
            classifiedProgressionCache.TryGetValue(key, out var cached))
            return cached;

        // The catalog generator resolves quest eligibility and variants offline.
        // Do not apply language-specific quest-title rules during gameplay.
        var applicableIds = loaded ? jobCatalog.GetVerifiedApplicableQuestIds(job.ClassJobId, firstClass) : null;
        var matchingSteps = applicableIds is null
            ? Array.Empty<JobQuestStep>()
            : applicableIds.Select(id => job.Progression.FirstOrDefault(q => q.QuestId == id))
                .Where(q => q is not null).Select(q => q!).ToArray();
        // Verified data is authoritative. Otherwise resolve a conservative journal
        // prefix at use time: never pick an unobserved variant when alternatives
        // exist, and never bridge a stage outside the terminal graph.
        if (applicableIds is null && loaded)
            return ResolveObservedJournalPrefix(job).Steps.Select(step =>
                new ClassifiedClassQuest(step, ClassQuestRole.ApplicableProgression,
                    "Journal stage with unambiguous character eligibility")).ToArray();

        // A missing catalog stage fails closed.
        var result = applicableIds is not null && matchingSteps.Length == applicableIds.Count
            ? matchingSteps.Select(quest => new ClassifiedClassQuest(quest,
                ClassQuestRole.ApplicableProgression, "Verified catalog membership")).ToArray()
            : Array.Empty<ClassifiedClassQuest>();
        if (loaded && applicableIds is not null)
            classifiedProgressionCache[key] = result;
        return result;
    }

    private sealed record JournalPrefix(IReadOnlyList<JobQuestStep> Steps, bool CompleteCoverage,
        string Detail);

    private JournalPrefix ResolveObservedJournalPrefix(JobProgressionTarget job)
    {
        if (!PlayerStateService.IsLoaded)
            return new(Array.Empty<JobQuestStep>(), false, "Character data unavailable");
        var stages = jobCatalog.GetJournalStages(job.ClassJobId);
        if (stages.Count == 0)
            return new(Array.Empty<JobQuestStep>(), false, "No journal candidates");
        var selected = new List<JobQuestStep>(stages.Count);
        var selectedIds = new HashSet<uint>();
        var usedInferredVariant = false;
        foreach (var stage in stages)
        {
            if (!stage.OnTerminalRoute)
                return new(selected, false, $"Off-route stage at Lv. {stage.Level}");
            var observed = stage.Variants.Where(v =>
                GetQuestStateByRowId(v.QuestId) is QuestProgressState.Completed or QuestProgressState.InProgress)
                .ToArray();
            if (observed.Length > 1)
                return new(selected, false, $"Conflicting variants at Lv. {stage.Level}");
            // Only a single available variant or a directly observed variant is usable.
            var chosen = observed.Length == 1 ? observed[0] :
                stage.Variants.Count == 1 ? stage.Variants[0] : null;
            if (chosen is null)
            {
                // Distinguish *branch-specific* predecessors from predecessors shared
                // by every variant. Shared steps cannot identify a player's branch.
                // E.g. PGL 66091/66234 both reference 66090, but the former also
                // references the alternate entry 66069 while the latter references
                // the observed branch 66089. Only unique, already selected branch
                // evidence may break a tie. This remains provisional because the
                // game's PreviousQuest join/lock semantics are not independently
                // certified by the journal graph.
                var commonIds = stage.Variants
                    .Select(v => v.PreviousQuestIds.ToHashSet())
                    .Aggregate((a, b) => { a.IntersectWith(b); return a; });
                var branchMatches = stage.Variants
                    .Select(v => new
                    {
                        Variant = v,
                        UniquePrior = v.PreviousQuestIds.Where(id => !commonIds.Contains(id)).ToArray()
                    })
                    .Where(v => v.UniquePrior.Any(selectedIds.Contains))
                    .ToArray();
                if (branchMatches.Length == 1 &&
                    stage.Variants.Count > 1 &&
                    stage.Variants.All(v => v.PreviousQuestIds.Any(id => !commonIds.Contains(id))))
                {
                    chosen = branchMatches[0].Variant;
                    usedInferredVariant = true;
                }
            }
            if (chosen is null)
                return new(selected, false, $"Unresolved variants at Lv. {stage.Level}: {stage.Name}");
            // A linked predecessor must be part of the observed prior route. Data
            // sometimes contains external unlock requirements; those are checked
            // by actual quest flags, not assumed from job level.
            if (selected.Count > 0 && chosen.PreviousQuestIds.Count > 0 &&
                !chosen.PreviousQuestIds.Any(id => selectedIds.Contains(id) ||
                    GetQuestStateByRowId(id) == QuestProgressState.Completed))
                return new(selected, false, $"Unconfirmed prerequisite for {stage.Name}");
            selected.Add(chosen.Step);
            selectedIds.Add(chosen.QuestId);
        }
        return new(selected, !usedInferredVariant, usedInferredVariant
            ? "Provisional branch chosen from character-route prerequisites; mandatory membership not verified"
            : "Every discovered journal stage has an eligible variant");
    }

    private bool HasCompleteProgressionCoverage(JobProgressionTarget job) =>
        jobCatalog.GetVerifiedApplicableQuestIds(job.ClassJobId,
            PlayerStateService.IsLoaded ? PlayerStateService.FirstClass.RowId : 0u) is not null ||
        ResolveObservedJournalPrefix(job).CompleteCoverage;

    private IReadOnlyList<JobQuestStep> GetApplicableProgression(JobProgressionTarget job) =>
        GetClassifiedProgression(job)
            .Where(entry => entry.Role == ClassQuestRole.ApplicableProgression)
            .Select(entry => entry.Step)
            .ToArray();

    // Display the underlying evidence, including ambiguity that blocks a complete chain.
    private void DrawJournalProgressionPreview(JobProgressionTarget job)
    {
        var stages = jobCatalog.GetJournalStages(job.ClassJobId);
        if (stages.Count == 0)
            return;

        var evidence = stages.Select(stage => new JournalStageEvidenceResolver.Stage(
            stage.Level, stage.Name, stage.OnTerminalRoute,
            stage.Variants.Select(v => new JournalStageEvidenceResolver.Variant(
                v.QuestId, v.PreviousQuestIds,
                GetQuestStateByRowId(v.QuestId) == QuestProgressState.Completed,
                GetQuestStateByRowId(v.QuestId) == QuestProgressState.InProgress)).ToArray()))
            .ToArray();
        var inspection = JournalStageEvidenceResolver.Inspect(evidence);

        ImGui.Spacing();
        if (!ImGui.TreeNode($"Journal stage candidates ({stages.Count})##journal-stages-{job.ClassJobId}"))
            return;
        ImGui.TextWrapped("Advisory local journal evidence — not a verified mandatory chain. " +
            "Variant completion and prerequisite relationships narrow the possible next stage, " +
            "but cannot authorize completion, progression pins, or automatic handoffs.");
        if (PlayerStateService.IsLoaded)
            ImGui.TextDisabled($"Character starting class ID: {PlayerStateService.FirstClass.RowId}");
        var selectedPrefix = ResolveObservedJournalPrefix(job);
        ImGui.TextDisabled($"Resolved stage prefix: {selectedPrefix.Steps.Count}/{stages.Count} — {selectedPrefix.Detail}");
        foreach (var stage in stages.Where(stage => stage.Variants.Count > 1))
        {
            var selectedQuest = selectedPrefix.Steps.FirstOrDefault(step =>
                stage.Variants.Any(variant => variant.QuestId == step.QuestId));
            if (selectedQuest is not null)
                ImGui.TextWrapped($"Variant at Lv. {stage.Level}: selected quest #{selectedQuest.QuestId} " +
                    "(character observation or provisional prerequisite evidence; not independent proof of eligibility)");
            else
                ImGui.TextDisabled($"Variant at Lv. {stage.Level}: unresolved");
        }
        if (inspection.NextCandidateIndex is int nextIndex)
            ImGui.TextWrapped($"Next journal candidate (unverified): Lv. {stages[nextIndex].Level} — {stages[nextIndex].Name}");
        else
            ImGui.TextDisabled($"Next journal candidate: unresolved — {inspection.Reason}");

        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            var status = inspection.Stages[i];
            var state = status.State switch
            {
                JournalStageEvidenceResolver.StageState.ObservedComplete => "Observed complete",
                JournalStageEvidenceResolver.StageState.InProgress => "In progress",
                JournalStageEvidenceResolver.StageState.ConflictingEvidence => "Conflicting variant evidence",
                _ => "Unverified"
            };
            var label = $"Lv. {stage.Level} — {stage.Name} [{state}]" +
                (stage.OnTerminalRoute ? "" : " [Off terminal route]");
            if (stage.Variants.Count == 1)
            {
                ImGui.BulletText(label);
                ImGui.TextDisabled($"    Quest ID: {stage.Variants[0].QuestId}");
            }
            else if (ImGui.TreeNode($"{label} ({stage.Variants.Count} variants)##stage-{job.ClassJobId}-{stage.Variants[0].QuestId}"))
            {
                foreach (var variant in stage.Variants)
                {
                    var questState = GetQuestStateByRowId(variant.QuestId);
                    var note = status.ObservedVariantIds.Contains(variant.QuestId)
                        ? " [character evidence]" : "";
                    ImGui.BulletText($"{variant.QuestId}: {questState}{note}");
                    if (variant.PreviousQuestIds.Count > 0)
                        ImGui.TextDisabled($"    Prior IDs: {string.Join(", ", variant.PreviousQuestIds)} (join semantics unverified)");
                }
                ImGui.TreePop();
            }
        }
        ImGui.TreePop();
    }

    private int CountConfirmedProgressionQuests(JobProgressionTarget job) =>
        GetApplicableProgression(job).Count(step => step.QuestId != 0 &&
            GetQuestStateByRowId(step.QuestId) == QuestProgressState.Completed);

    private JobQuestStep? GetNextConfirmedProgressionQuest(JobProgressionTarget job) =>
        GetApplicableProgression(job).FirstOrDefault(step => step.QuestId != 0 &&
            GetQuestStateByRowId(step.QuestId) != QuestProgressState.Completed);
}
