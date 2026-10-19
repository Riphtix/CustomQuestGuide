using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

// ===== Plugin.Navigation.cs =====
public sealed partial class Plugin
{
    private NavigationState CaptureNavigationState() => new(
        activeTab,
        config.SelectedCategory ?? string.Empty,
        config.SelectedJob ?? string.Empty,
        config.SelectedItemId,
        config.SelectedSetId ?? string.Empty,
        config.SelectedRelicJob ?? string.Empty,
        config.SelectedEmoteId,
        config.SelectedTitleId,
        config.SelectedProgressionCategory ?? string.Empty,
        config.SelectedClassJobId,
        config.SelectedDutyCategory ?? string.Empty,
        config.SelectedDutyId,
        config.SelectedChainIndex);

    private void NavigateTo(System.Action selectionChange)
    {
        if (restoringNavigationState)
        {
            selectionChange();
            SaveConfig();
            return;
        }

        var before = CaptureNavigationState();
        selectionChange();
        var after = CaptureNavigationState();

        if (before != after)
        {
            PushNavigationState(navigationBack, before);
            navigationForward.Clear();
        }

        SaveConfig();
    }

    private static void PushNavigationState(List<NavigationState> stack, NavigationState state)
    {
        if (stack.Count > 0 && stack[^1] == state)
            return;

        stack.Add(state);
        if (stack.Count > NavigationHistoryLimit)
            stack.RemoveAt(0);
    }

    private void DrawNavigationHistoryControls()
    {
        ImGui.Spacing();

        if (navigationBack.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.Button("< Back##NavigationBack"))
            NavigateHistoryBack();
        if (navigationBack.Count == 0)
            ImGui.EndDisabled();

        ImGui.SameLine();

        if (navigationForward.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.Button("Forward >##NavigationForward"))
            NavigateHistoryForward();
        if (navigationForward.Count == 0)
            ImGui.EndDisabled();
    }

    private void NavigateHistoryBack()
    {
        if (navigationBack.Count == 0)
            return;

        var current = CaptureNavigationState();
        var target = navigationBack[^1];
        navigationBack.RemoveAt(navigationBack.Count - 1);
        PushNavigationState(navigationForward, current);
        RestoreNavigationState(target);
    }

    private void NavigateHistoryForward()
    {
        if (navigationForward.Count == 0)
            return;

        var current = CaptureNavigationState();
        var target = navigationForward[^1];
        navigationForward.RemoveAt(navigationForward.Count - 1);
        PushNavigationState(navigationBack, current);
        RestoreNavigationState(target);
    }

    private void RestoreNavigationState(NavigationState state)
    {
        restoringNavigationState = true;
        try
        {
            activeTab = state.Tab;
            config.SelectedCategory = state.SelectedCategory;
            config.SelectedJob = state.SelectedJob;
            config.SelectedItemId = state.SelectedItemId;
            config.SelectedSetId = state.SelectedSetId;
            config.SelectedRelicJob = state.SelectedRelicJob;
            config.SelectedEmoteId = state.SelectedEmoteId;
            config.SelectedTitleId = state.SelectedTitleId;
            config.SelectedProgressionCategory = state.SelectedProgressionCategory;
            config.SelectedClassJobId = state.SelectedClassJobId;
            config.SelectedDutyCategory = state.SelectedDutyCategory;
            config.SelectedDutyId = state.SelectedDutyId;
            config.SelectedChainIndex = state.SelectedChainIndex;
            targetSearch = string.Empty;
            SaveConfig();
        }
        finally
        {
            restoringNavigationState = false;
        }

        RefreshRestoredTarget();
    }

    private void RefreshRestoredTarget()
    {
        if (activeTab == MainTab.Sets && !string.IsNullOrWhiteSpace(config.SelectedSetId) &&
            setCatalog.Get(config.SelectedSetId) is { } set)
        {
            var members = set.ItemIds
                .Select(catalog.GetById)
                .Where(x => x is not null)
                .Cast<TargetEntry>()
                .ToArray();
            PromoteActiveScanTargets(
                set.ItemIds,
                members.SelectMany(GetRelatedDutyNames).Distinct(StringComparer.OrdinalIgnoreCase),
                $"restored set: {set.Name}");
            return;
        }

        if (activeTab == MainTab.Items && catalog.GetById(config.SelectedItemId) is { } item)
        {
            // History navigation restores an existing target session. Do not
            // treat it as a fresh selection or enqueue the target again. Any
            // in-flight target-scoped enrichment continues independently, and
            // DrawItemsTab must not auto-request the same item on the next frame.
            autoEnrichmentRequestedItemId = item.ItemId;
            PromoteActiveScanTargets(
                new[] { item.ItemId },
                GetRelatedDutyNames(item),
                $"restored item: {item.Name}");
            return;
        }

        if (activeTab == MainTab.Items &&
            config.SelectedCategory.Equals("Emotes", StringComparison.OrdinalIgnoreCase) &&
            emoteCatalog.Get(config.SelectedEmoteId) is { } emote &&
            emote.BackingItemId != 0 && catalog.GetById(emote.BackingItemId) is { } teachingItem)
        {
            RequestTargetScopedSourceEnrichment(teachingItem);
            PromoteActiveScanTargets(
                new[] { teachingItem.ItemId },
                GetRelatedDutyNames(teachingItem),
                $"restored emote: {emote.Name}");
            return;
        }

        if (activeTab == MainTab.Duties && dutyCatalog.Get(config.SelectedDutyId) is { } duty)
        {
            PromoteActiveScanTargets(null, new[] { duty.Name }, $"restored duty: {duty.Name}");
        }
    }

}

// ===== Plugin.TargetTabs.cs =====
public sealed partial class Plugin
{
    private void DrawItemsTab()
    {
        DrawCommonTargetControls();

        if (config.SelectedCategory.Equals("Emotes", StringComparison.OrdinalIgnoreCase))
        {
            var selectedEmote = emoteCatalog.Get(config.SelectedEmoteId);
            if (selectedEmote is null)
            {
                ImGui.Separator();
                ImGui.TextWrapped("No emote is selected.");
                DrawDebugPanel(null, null);
                return;
            }

            ImGui.Separator();
            DrawEmoteSummary(selectedEmote);
            DrawDebugPanel(null, null);
            return;
        }

        if (config.SelectedCategory.Equals("Titles", StringComparison.OrdinalIgnoreCase))
        {
            var selectedTitle = titleCatalog.Get(config.SelectedTitleId);
            if (selectedTitle is null)
            {
                ImGui.Separator();
                ImGui.TextWrapped("No title is selected.");
                DrawDebugPanel(null, null);
                return;
            }

            ImGui.Separator();
            DrawTitleSummary(selectedTitle);
            DrawDebugPanel(null, null);
            return;
        }

        var selected = catalog.GetById(config.SelectedItemId);

        if (selected is null)
        {
            ImGui.Separator();
            ImGui.TextWrapped("No target is selected for the current filters.");
            DrawDebugPanel(null, null);
            return;
        }

        ImGui.Separator();

        DrawCraftingBreadcrumbs(selected.ItemId);

        // A target can remain selected across plugin reloads. New enrichment
        // requirements (for example 1.3.4 monster locations) must therefore be
        // evaluated once even when the user has not manually re-selected the
        // item. This is still strictly selected-target/on-demand behavior: one
        // item per session, no background sweep or persistent polling.
        if (autoEnrichmentRequestedItemId != selected.ItemId)
        {
            autoEnrichmentRequestedItemId = selected.ItemId;
            RequestTargetScopedSourceEnrichment(selected);
        }

        var result = resolver.Resolve(selected);
        DrawTargetSummary(result);
        DrawRidingMapProgress(selected);

        if (config.Detailed)
            DrawDetailed(result);

        DrawDebugPanel(result, null);
    }

    private void DrawCraftingBreadcrumbs(uint selectedItemId)
    {
        if (craftingBreadcrumbPath.Count < 2)
            return;

        // Breadcrumbs are only valid while the selected item is on the active
        // crafting trail. General navigation/search selections are not coerced
        // into this model.
        var selectedIndex = craftingBreadcrumbPath.IndexOf(selectedItemId);
        if (selectedIndex < 0)
        {
            craftingBreadcrumbPath.Clear();
            return;
        }

        if (selectedIndex + 1 < craftingBreadcrumbPath.Count)
            craftingBreadcrumbPath.RemoveRange(selectedIndex + 1, craftingBreadcrumbPath.Count - selectedIndex - 1);

        if (craftingBreadcrumbPath.Count < 2)
            return;

        ImGui.TextDisabled("Crafting path:");
        ImGui.SameLine();

        for (var i = 0; i < craftingBreadcrumbPath.Count; i++)
        {
            var itemId = craftingBreadcrumbPath[i];
            var target = catalog.GetById(itemId);
            var name = target?.Name ?? $"Item {itemId}";
            var isCurrent = i == craftingBreadcrumbPath.Count - 1;

            if (isCurrent)
            {
                ImGui.TextUnformatted(name);
            }
            else if (ImGui.SmallButton($"{name}##craft-breadcrumb-{i}-{itemId}"))
            {
                NavigateCraftingBreadcrumbTo(i);
            }

            if (i + 1 < craftingBreadcrumbPath.Count)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(">");
                ImGui.SameLine();
            }
        }

        ImGui.Spacing();
    }

    private void DrawJobsAndProgressionTab()
    {
        ImGui.TextWrapped(
            "Target formal class/job unlocking and quest progression. " +
            "Leveling methods, rotations, gear, role-quest guidance, achievements, and relics are intentionally outside this tab's scope.");
        ImGui.Spacing();

        var categories = jobCatalog.Categories;
        if (categories.Count == 0)
        {
            ImGui.TextDisabled("No class/job progression data could be built from the current game sheets.");
            return;
        }

        var categoryIndex = IndexOf(categories, config.SelectedProgressionCategory);
        if (categoryIndex < 0)
            categoryIndex = 0;

        var categoryNames = categories.ToArray();
        SetNextControlWidth(StandardComboWidth);
        if (ImGui.Combo("Category##ProgressionCategory", ref categoryIndex, categoryNames, categoryNames.Length))
        {
            config.SelectedProgressionCategory = categoryNames[categoryIndex];
            config.SelectedClassJobId = 0;
            SelectFirstProgressionJob();
            SaveConfig();
        }

        var jobs = jobCatalog.GetByCategory(config.SelectedProgressionCategory);
        if (jobs.Count == 0)
        {
            ImGui.TextDisabled("No classes/jobs are available in this category.");
            return;
        }

        var selectedIndex = 0;
        for (var i = 0; i < jobs.Count; i++)
        {
            if (jobs[i].ClassJobId == config.SelectedClassJobId)
            {
                selectedIndex = i;
                break;
            }
        }

        var selectedJobLabel = $"{jobs[selectedIndex].Name} ({jobs[selectedIndex].Abbreviation})";
        SetNextControlWidth(JobComboWidth);
        if (ImGui.BeginCombo("Class / Job##ProgressionJob", selectedJobLabel))
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                var candidate = jobs[i];
                var isSelected = i == selectedIndex;
                var visualState = GetJobVisualState(candidate);
                if (visualState != JobVisualState.Locked)
                    ImGui.PushStyleColor(ImGuiCol.Text, visualState == JobVisualState.Complete ? OwnedGreen : PartialBlue);
                if (ImGui.Selectable($"{candidate.Name} ({candidate.Abbreviation})", isSelected))
                {
                    selectedIndex = i;
                    SelectJobTarget(candidate);
                }
                if (visualState != JobVisualState.Locked)
                    ImGui.PopStyleColor();
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var job = jobCatalog.Get(config.SelectedClassJobId) ?? jobs[0];
        if (config.SelectedClassJobId != job.ClassJobId)
        {
            config.SelectedClassJobId = job.ClassJobId;
            SaveConfig();
        }

        ImGui.Separator();
        ImGui.Spacing();
        DrawJobStateText($"{job.Name.ToUpperInvariant()} ({job.Abbreviation})", GetJobVisualState(job));

        if (job.ParentClassJobId != 0 && job.ParentClassJobId != job.ClassJobId)
        {
            var parent = jobCatalog.Get(job.ParentClassJobId);
            if (parent is not null)
                ImGui.TextDisabled($"Base class: {parent.Name} ({parent.Abbreviation})");
        }

        var unlockState = job.UnlockQuest is null
            ? QuestProgressState.NotStarted
            : GetQuestStateByRowId(job.UnlockQuest.QuestId);
        var jobUnlocked = ownership.IsJobUnlocked(job.ClassJobId);
        var unlockComplete = jobUnlocked; // Unlock is authoritative ownership, never an inferred quest-state shortcut.
        var progressionState = ResolveJobQuestSequence(GetApplicableProgression(job));
        var classProgressionProven = AreAllJobQuestsConfirmedComplete(job);
        var completedProgression = CountConfirmedProgressionQuests(job);
        // Guidance must follow the first quest not actually confirmed complete.
        // Inferred sequence states are helpful elsewhere, but cannot prove
        // formal class/job completion or skip an outstanding quest here.
        var nextStep = GetNextConfirmedProgressionQuest(job);
        var nextRawState = nextStep is null ? QuestProgressState.Completed :
            GetQuestStateByRowId(nextStep.QuestId);

        // Only present actions that are actionable for the current unlock state.
        // A locked derived job first guides the player through any outstanding
        // base-class progression gate instead of advertising its own job chain.
        var parentJob = job.ParentClassJobId != 0 && job.ParentClassJobId != job.ClassJobId
            ? jobCatalog.Get(job.ParentClassJobId)
            : null;
        var missingParentUnlock = GetMissingBaseClassUnlock(job);
        var missingParentProgression = GetMissingBaseClassProgression(job) is not null;

        ImGui.Spacing();
        if (!unlockComplete)
        {
            if (missingParentUnlock is not null)
            {
                ImGui.TextUnformatted($"Requires {missingParentUnlock.Name} Unlock");
                ImGui.SameLine();
                if (ImGui.Button($"Open##job-base-unlock-{job.ClassJobId}"))
                {
                    SelectJobTarget(missingParentUnlock);
                    targetSearch = string.Empty;
                }
                ImGui.SameLine();
                DrawPinToggle("JobUnlock", missingParentUnlock.ClassJobId, string.Empty,
                    "Pin Unlock", "Unpin Unlock",
                    prerequisiteForKind: "JobUnlock", prerequisiteForNumericId: job.ClassJobId);
            }
            else if (missingParentProgression && parentJob is not null)
            {
                ImGui.TextUnformatted($"Requires {parentJob.Name} Progression");
                ImGui.SameLine();
                if (ImGui.Button($"Open##job-requires-{job.ClassJobId}-{parentJob.ClassJobId}"))
                {
                    SelectJobTarget(parentJob);
                    targetSearch = string.Empty;
                }
                ImGui.SameLine();
                // The selected job is locked: this is explicitly a prerequisite action,
                // not the locked job's own progression control. Only the unlocked
                // parent class may receive a progression pin.
                if (ownership.IsJobUnlocked(parentJob.ClassJobId))
                    DrawPinToggle("JobProgression", parentJob.ClassJobId, string.Empty,
                        "Pin Prerequisite", "Unpin Prerequisite",
                        prerequisiteForKind: "JobUnlock", prerequisiteForNumericId: job.ClassJobId);
            }
            else
                DrawPinToggle("JobUnlock", job.ClassJobId, string.Empty, "Pin Unlock", "Unpin Unlock");
        }
        else if (!classProgressionProven)
            DrawPinToggle("JobProgression", job.ClassJobId, string.Empty, "Pin Progression", "Unpin Progression");

        ImGui.Spacing();
        DrawColoredText("Unlock:", unlockComplete);
        ImGui.SameLine();
        if (unlockComplete)
            DrawColoredText("Unlocked", true);
        else if (missingParentUnlock is not null)
            ImGui.TextDisabled($"Requires {missingParentUnlock.Name} unlock.");
        else if (missingParentProgression)
        {
            ImGui.TextDisabled("Prerequisite class progression required.");
            // Show independent prerequisites without advertising the job unlock
            // quest giver before the base-class gate is met.
            foreach (var requirement in job.UnlockPrerequisites.Where(requirement =>
                !IsQuestCompleteByRowId(requirement.QuestId) &&
                (parentJob is null || !GetApplicableProgression(parentJob)
                    .Any(step => step.QuestId == requirement.QuestId))))
                ImGui.BulletText($"[ ] {requirement.Name}");
        }
        else if (job.UnlockQuest is null)
            ImGui.TextDisabled("Unlock quest location unavailable.");
        else
        {
            var levelText = job.UnlockQuest.Level > 0 ? $"Lv. {job.UnlockQuest.Level} — " : string.Empty;
            DrawColoredText($"{levelText}{job.UnlockQuest.Name}", false);
            ImGui.SameLine();
            ImGui.TextDisabled(unlockState == QuestProgressState.InProgress ? "[In progress]" : "[Not started]");
            if (!string.IsNullOrWhiteSpace(job.UnlockQuest.QuestGiver))
            {
                var prefix = unlockState == QuestProgressState.InProgress ? "Quest started by" : "Go to";
                if (!string.IsNullOrWhiteSpace(job.UnlockQuest.Location))
                    DrawNpcLocationLine(prefix, job.UnlockQuest.QuestGiver!, job.UnlockQuest.Location, $"job-unlock-{job.ClassJobId}");
                else
                    ImGui.TextDisabled($"{prefix}: {job.UnlockQuest.QuestGiver}");
            }
            if (job.UnlockPrerequisites.Count > 0)
            {
                ImGui.TextDisabled("Prerequisite quest(s):");
                foreach (var prerequisite in job.UnlockPrerequisites)
                {
                    var complete = IsQuestCompleteByRowId(prerequisite.QuestId);
                    ImGui.BulletText($"{(complete ? "[X]" : "[ ]")} {prerequisite.Name}");
                }
            }
        }

        // A locked job must not offer independent job progression guidance.
        // The unlock quest belongs to unlock guidance until actually completed.
        if (!unlockComplete)
        {
            ImGui.Spacing();
            ImGui.Separator();
            if (ImGui.TreeNode($"Debug / journal evidence##job-debug-{job.ClassJobId}"))
            {
                ImGui.TextDisabled($"ClassJob row: {job.ClassJobId}");
                ImGui.TextDisabled($"Snapshot job unlocked: {jobUnlocked}");
                ImGui.TextWrapped(ownership.GetClassJobUnlockDiagnostics(job.ClassJobId));
                ImGui.TextDisabled($"Unlock resolution: {job.UnlockResolution}");
                ImGui.TextDisabled($"Unlock quest ID: {job.UnlockQuest?.QuestId}");
                ImGui.TextDisabled($"Base class progression gate: {missingParentProgression}");
                DrawJournalProgressionPreview(job);
                ImGui.TreePop();
            }
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        var completeCoverage = HasCompleteProgressionCoverage(job);
        var progressionSteps = GetApplicableProgression(job);
        if (progressionSteps.Count == 0)
            ImGui.TextDisabled("Progression: Quest sequence not yet resolved");
        else
        {
            var summary = $"Progression: {completedProgression} / {progressionSteps.Count} quests complete";
            if (!completeCoverage)
                summary += " (partial sequence)";
            DrawColoredText(summary, completeCoverage && classProgressionProven);
        }
        if (nextStep is not null)
        {
            var nextLevel = nextStep.Level > 0 ? $"Lv. {nextStep.Level} — " : string.Empty;
            var active = nextRawState == QuestProgressState.InProgress;
            ImGui.TextWrapped($"{(active ? "Current quest" : "Next quest")}: {nextLevel}{nextStep.Name}");
            if (!string.IsNullOrWhiteSpace(nextStep.QuestGiver))
            {
                var prefix = active ? "Quest started by" : "Go to";
                if (!string.IsNullOrWhiteSpace(nextStep.Location))
                    DrawNpcLocationLine(prefix, nextStep.QuestGiver!, nextStep.Location, $"job-next-{job.ClassJobId}-{nextStep.QuestId}");
                else
                    ImGui.TextDisabled($"{prefix}: {nextStep.QuestGiver}");
            }
        }
        else if (progressionSteps.Count > 0)
        {
            ImGui.TextDisabled(classProgressionProven
                ? "All class/job progression quests complete."
                : completeCoverage ? "No next quest available; completion not confirmed." :
                    "Further quests are unresolved; see Debug / journal evidence.");
        }

        ImGui.Spacing();
        for (var i = 0; i < progressionSteps.Count; i++)
        {
            var step = progressionSteps[i];
            var state = GetQuestStateByRowId(step.QuestId);
            var marker = state == QuestProgressState.Completed ? "[X]" :
                state == QuestProgressState.InProgress ? "[>]" : "[ ]";
            var level = step.Level > 0 ? $"Lv. {step.Level} — " : string.Empty;
            DrawColoredText($"{marker} {level}{step.Name}", state == QuestProgressState.Completed);

            if (state != QuestProgressState.Completed &&
                !string.IsNullOrWhiteSpace(step.QuestGiver) &&
                nextStep?.QuestId == step.QuestId)
            {
                ImGui.Indent();
                var prefix = state == QuestProgressState.InProgress ? "Quest started by" : "Go to";
                if (!string.IsNullOrWhiteSpace(step.Location))
                    DrawNpcLocationLine(prefix, step.QuestGiver!, step.Location, $"job-step-{job.ClassJobId}-{step.QuestId}");
                else
                    ImGui.TextDisabled($"{prefix}: {step.QuestGiver}");
                ImGui.Unindent();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.TreeNode($"Debug / journal evidence##job-debug-{job.ClassJobId}"))
        {
            ImGui.TextDisabled($"ClassJob row: {job.ClassJobId}");
            if (job.UnlockQuest is not null)
                ImGui.TextDisabled($"Unlock quest: {job.UnlockQuest.QuestId} — {job.UnlockQuest.Name}");
            ImGui.TextDisabled($"Unlock prerequisites: {job.UnlockPrerequisites.Count}");
            ImGui.TextDisabled($"Live job unlocked: {ownership.IsJobUnlocked(job.ClassJobId)}");
            ImGui.TextDisabled(ownership.GetClassJobUnlockDiagnostics(job.ClassJobId));
            ImGui.TextDisabled($"Unlock resolution: {job.UnlockResolution}");
            ImGui.TextDisabled($"ClassJob.UnlockQuest row: {job.ClassJobUnlockQuestId}");
            ImGui.TextDisabled($"Formal quest nodes: {progressionSteps.Count}");
            ImGui.TextDisabled($"Complete progression coverage: {completeCoverage}");
            ImGui.TextDisabled($"Verified catalog: {jobCatalog.GetVerifiedApplicableQuestIds(job.ClassJobId, PlayerStateService.IsLoaded ? PlayerStateService.FirstClass.RowId : 0u) is not null}");
            var prefix = ResolveObservedJournalPrefix(job);
            ImGui.TextDisabled($"Resolved prefix: {prefix.Steps.Count} quests — {prefix.Detail}");
            ImGui.TextDisabled($"Journal candidates: {jobCatalog.GetJournalCandidates(job.ClassJobId).Count} (membership not independently verified)");
            var journalAnalysis = jobCatalog.AnalyzeJournalPath(job.ClassJobId);
            ImGui.TextWrapped($"Journal graph: {journalAnalysis.Status} — {journalAnalysis.Detail}");
            ImGui.TextDisabled("Detailed route decisions are in Debug > journal audits.");
            DrawJournalProgressionPreview(job);
            ImGui.TreePop();
        }

    }

    private void DrawDutiesTab()
    {
        ImGui.TextWrapped("Select and pin instanced duties directly. Unlocked duties use CQG's green completed/unlocked state; locked duties show the best known unlock quest and contact route.");
        ImGui.Spacing();

        var categories = dutyCatalog.Categories;
        if (categories.Count == 0)
        {
            ImGui.TextDisabled("No duty data could be built from the current Content Finder sheets.");
            return;
        }

        var categoryIndex = IndexOf(categories, config.SelectedDutyCategory);
        if (categoryIndex < 0)
            categoryIndex = 0;
        var categoryNames = categories.ToArray();
        SetNextControlWidth(StandardComboWidth);
        if (ImGui.Combo("Category##DutyCategory", ref categoryIndex, categoryNames, categoryNames.Length))
        {
            config.SelectedDutyCategory = categoryNames[categoryIndex];
            config.SelectedDutyId = dutyCatalog.GetByCategory(config.SelectedDutyCategory).FirstOrDefault()?.ContentFinderConditionId ?? 0;
            SaveConfig();
        }

        var duties = dutyCatalog.GetByCategory(config.SelectedDutyCategory);
        var duty = dutyCatalog.Get(config.SelectedDutyId);
        if (duty is null || !duty.Category.Equals(config.SelectedDutyCategory, StringComparison.OrdinalIgnoreCase))
        {
            duty = duties.FirstOrDefault();
            if (duty is not null && config.SelectedDutyId != duty.ContentFinderConditionId)
            {
                config.SelectedDutyId = duty.ContentFinderConditionId;
                SaveConfig();
            }
        }

        ImGui.TextDisabled("Use the Target control above to search or choose a duty in this category.");
        if (duty is null)
            return;

        ImGui.Separator();
        ImGui.Spacing();

        var advice = dutyUnlocks.GetAdvice(duty.Name);
        var unlocked = advice?.IsUnlocked ?? dutyUnlocks.IsDutyUnlocked(duty.Name);
        if (unlocked == true)
            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
        ImGui.TextUnformatted(duty.DisplayLabel.ToUpperInvariant());
        if (unlocked == true)
            ImGui.PopStyleColor();

        ImGui.SameLine();
        DrawPinToggle("Duty", duty.ContentFinderConditionId, string.Empty, "Pin Duty", "Unpin Duty");

        ImGui.TextDisabled($"Category: {duty.Category}");
        ImGui.Spacing();

        if (unlocked == true)
        {
            DrawColoredText("Unlocked", true);
            ImGui.TextWrapped("This duty is available on the current character.");
            return;
        }

        ImGui.TextUnformatted("Locked");
        if (advice is null)
        {
            ImGui.TextDisabled("CQG can identify this duty but could not resolve its unlock quest from the current game data.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(advice.QuestName))
        {
            ImGui.TextUnformatted("Unlock quest:");
            ImGui.SameLine();
            ImGui.TextUnformatted(advice.QuestName);
        }
        else
        {
            ImGui.TextDisabled("Unlock quest was not resolved.");
        }

        if (!string.IsNullOrWhiteSpace(advice.Contact))
            DrawNpcLocationLine("Go to", advice.Contact!, advice.Location, $"duty-{duty.ContentFinderConditionId}");
        else if (!string.IsNullOrWhiteSpace(advice.Location))
            DrawLocationOnlyLine("Location", advice.Location!, $"duty-{duty.ContentFinderConditionId}");

        if (!string.IsNullOrWhiteSpace(advice.Note))
            ImGui.TextDisabled(advice.Note);
    }

}
