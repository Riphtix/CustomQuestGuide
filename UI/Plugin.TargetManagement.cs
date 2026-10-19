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

// ===== Plugin.ItemSets.cs =====
public sealed partial class Plugin
{
    private void DrawItemsNavigation()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Browse Filters");
        ImGui.Separator();
        DrawItemFilterControls();

        ImGui.Spacing();
        ImGui.TextUnformatted("Target");
        ImGui.Separator();
        DrawUnifiedTargetSelector();
    }

    private IReadOnlyList<string> GetBrowseCategories()
    {
        var categories = catalog.Categories.ToList();
        if (emoteCatalog.Emotes.Count > 0 && !categories.Contains("Emotes", StringComparer.OrdinalIgnoreCase))
        {
            var insertAt = categories.FindIndex(x => x.Equals("Hairstyles", StringComparison.OrdinalIgnoreCase));
            if (insertAt < 0)
                insertAt = categories.FindIndex(x => x.Equals("Decorations & Housing", StringComparison.OrdinalIgnoreCase));
            if (insertAt < 0)
                categories.Add("Emotes");
            else
                categories.Insert(insertAt, "Emotes");
        }
        if (titleCatalog.Titles.Count > 0 && !categories.Contains("Titles", StringComparer.OrdinalIgnoreCase))
        {
            var insertAt = categories.FindIndex(x => x.Equals("Decorations & Housing", StringComparison.OrdinalIgnoreCase));
            if (insertAt < 0)
                categories.Add("Titles");
            else
                categories.Insert(insertAt, "Titles");
        }
        return categories;
    }

    private void DrawItemFilterControls()
    {
        var categories = GetBrowseCategories();
        var categoryIndex = IndexOf(categories, config.SelectedCategory);
        if (categoryIndex < 0)
            categoryIndex = 0;

        var categoryNames = categories.ToArray();
        SetNextControlWidth(StandardComboWidth);
        if (ImGui.Combo("Category##ItemBrowseCategory", ref categoryIndex, categoryNames, categoryNames.Length))
        {
            config.SelectedSetId = string.Empty;
            config.SelectedCategory = categoryNames[categoryIndex];
            config.SelectedJob = "All";
            config.SelectedItemId = 0;
            config.SelectedEmoteId = 0;
            config.SelectedTitleId = 0;
            config.SelectedChainIndex = 0;
            SelectFirstTarget();
            SaveConfig();
        }

        if (TargetCatalog.IsGearCategory(config.SelectedCategory))
        {
            var jobs = catalog.Jobs;
            var jobIndex = IndexOf(jobs, config.SelectedJob);
            if (jobIndex < 0)
                jobIndex = 0;

            var jobNames = jobs.ToArray();
            SetNextControlWidth(JobComboWidth);
            if (ImGui.Combo("Class / Job##ItemBrowseJob", ref jobIndex, jobNames, jobNames.Length))
            {
                config.SelectedSetId = string.Empty;
                config.SelectedJob = jobNames[jobIndex];
                config.SelectedItemId = 0;
                config.SelectedChainIndex = 0;
                SelectFirstTarget();
                SaveConfig();
            }
        }
        else
        {
            ImGui.TextDisabled("Class / Job filter applies to gear categories.");
        }
    }

    private void DrawCommonTargetControls()
    {
        var detailed = config.Detailed;

        if (ImGui.Checkbox("Detailed", ref detailed))
        {
            config.Detailed = detailed;
            SaveConfig();
        }

        ImGui.SameLine();

        var sourceRefreshCoolingDown = DateTime.UtcNow < nextManualSourceRefreshUtc;
        if (collectibleSources.IsLoading || garlandSources.IsLoading)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Refreshing acquisition sources...");
            ImGui.EndDisabled();
        }
        else if (sourceRefreshCoolingDown)
        {
            ImGui.BeginDisabled();
            var seconds = Math.Max(1, (int)Math.Ceiling((nextManualSourceRefreshUtc - DateTime.UtcNow).TotalSeconds));
            ImGui.Button($"Refresh available in {seconds}s");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("Refresh acquisition sources"))
        {
            // Manual broad refreshes are deliberately rate-limited so repeated
            // clicks cannot hammer community services or create avoidable
            // network load. Target repair is a separate, item-scoped action.
            nextManualSourceRefreshUtc = DateTime.UtcNow.AddMinutes(5);
            coverageMetricsExpiresUtc = DateTime.MinValue;
            _ = collectibleSources.RefreshAsync();
            // Garland is intentionally target-scoped. A broad refresh must not
            // fan out into thousands of per-item HTTP requests.
        }

        // A selected target needs a deterministic escape hatch from stale or
        // partially-populated enrichment. Unlike the broad refresh above, this
        // invalidates only the selected item's external source record and its
        // related monster metadata, then immediately re-runs normal target-
        // scoped enrichment. It is not subject to the five-minute broad refresh
        // cooldown because it cannot fan out across the catalog.
        if (catalog.GetById(config.SelectedItemId) is { } selectedTarget)
        {
            ImGui.SameLine();
            if (garlandSources.IsLoading ||
                garlandSources.IsRequestInFlight(selectedTarget.ItemId) ||
                garlandSources.IsMonsterEnrichmentInFlight(selectedTarget.ItemId))
            {
                ImGui.BeginDisabled();
                ImGui.Button("Rebuilding selected source...");
                ImGui.EndDisabled();
            }
            else if (ImGui.Button("Rebuild selected source"))
            {
                // Capture the currently-merged monster identities for transient
                // retry cleanup. Rebuilding one item deliberately preserves the
                // global monster cache because monster facts are not item-owned.
                var currentSources = resolver.Resolve(selectedTarget).Sources;
                if (garlandSources.RebuildTargetCache(selectedTarget.ItemId, currentSources))
                {
                    coverageMetricsExpiresUtc = DateTime.MinValue;

                    // Reset the per-session "already evaluated" marker as part of
                    // the manual invalidation. The request below is forced once,
                    // so a user-initiated rebuild always reacquires the selected
                    // item's external answer rather than accepting a locally-
                    // complete result immediately after its cache was cleared.
                    autoEnrichmentRequestedItemId = 0;
                    ResetSelectedEnrichmentRecovery(selectedTarget.ItemId);
                    RequestTargetScopedSourceEnrichment(
                        selectedTarget,
                        forceSelectedItemRequest: true);
                    autoEnrichmentRequestedItemId = selectedTarget.ItemId;
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Repairs only the selected target: deletes that target's persisted external acquisition-source cache and forces one fresh selected-target network request. " +
                    "Global monster metadata is preserved because it is shared by every item that references that monster. Other caches, ownership state, and the Glamour Dresser snapshot are also preserved.");
            }
        }
    }

    private void DrawSetSummary(TargetSet set)
    {
        var members = set.ItemIds
            .Select(catalog.GetById)
            .Where(t => t is not null)
            .Cast<TargetEntry>()
            .ToArray();

        if (IsRelicSet(set))
        {
            DrawRelicSetSummary(set, members);
            return;
        }

        var resolved = members.Count(resolver.HasDirectSource);
        var ownedCount = members.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned);

        ImGui.Spacing();
        DrawColoredText(set.Name.ToUpperInvariant(), members.Length > 0 && ownedCount == members.Length);
        ImGui.SameLine();
        DrawPinToggle("Set", 0, set.Id);
        ImGui.Separator();
        ImGui.TextWrapped(set.Description);
        ImGui.Spacing();
        DrawProgressBar("Collection progress", ownedCount, members.Length, "owned");
        if (config.Detailed)
            ImGui.TextDisabled($"Source coverage: {resolved} / {members.Length} members resolved");

        DrawSetFrontierGuidance(set, members);
        ImGui.Spacing();

        var prerequisiteMembers = members.Where(x => !IsCompletionReward(set, x)).ToArray();
        var prerequisiteOwned = prerequisiteMembers.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned);
        var prerequisitesComplete = prerequisiteMembers.Length == 0 || prerequisiteOwned == prerequisiteMembers.Length;

        foreach (var member in members)
        {
            // Owned members need no acquisition-source resolution. Avoid potentially
            // expensive resolver work during every rendered frame.
            var owned = ownership.GetItemStatus(member.ItemId);

            if (owned.IsOwned)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, OwnedTextColor(owned));
                ImGui.BulletText($"{member.Name} — Owned ({owned.Source})");
                ImGui.PopStyleColor();
                continue;
            }

            if (IsCompletionReward(set, member) && !prerequisitesComplete)
            {
                ImGui.TextDisabled($"• {member.Name} — Locked until prerequisite collection is complete ({prerequisiteOwned} / {prerequisiteMembers.Length})");
                continue;
            }

            var result = resolver.Resolve(member);
            var primary = result.Sources.FirstOrDefault();
            var action = actionGuidance.Resolve(result);
            if (!action.IsEmpty)
            {
                var run = action.ActionLabel.Equals("Run", StringComparison.OrdinalIgnoreCase);

                // The member/item name reflects ownership of the item itself. An unlocked
                // acquisition duty does not make the item owned, so only the duty/action
                // portion uses the green unlocked visual language.
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted($"{member.Name} — ");
                ImGui.SameLine(0.0f, 0.0f);
                if (run)
                    ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                ImGui.TextUnformatted($"{action.ActionLabel}: {action.ActionText}");
                if (run)
                    ImGui.PopStyleColor();

                if (!string.IsNullOrWhiteSpace(action.GoTo))
                {
                    ImGui.Indent();
                    DrawCombinedWhoWhereLine("Go to", action.GoTo, $"set-member-{member.ItemId}");
                    ImGui.Unindent();
                }
                if (member.Category.Equals("Riding Maps", StringComparison.OrdinalIgnoreCase))
                {
                    ImGui.Indent();
                    DrawSharedFateRequirement(member, compact: true);
                    ImGui.Unindent();
                }
                continue;
            }

            if (primary is null)
                ImGui.PushStyleColor(ImGuiCol.Text, UnresolvedRed);

            var sourceText = primary is null
                ? "No source resolved yet"
                : primary.Name;

            ImGui.BulletText($"{member.Name} — {sourceText}");

            if (primary is null)
                ImGui.PopStyleColor();

            if (member.Category.Equals("Riding Maps", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.Indent();
                DrawSharedFateRequirement(member, compact: true);
                ImGui.Unindent();
            }
        }
    }

    private static void DrawProgressBar(string label, int current, int required, string unit = "")
    {
        if (required <= 0)
            return;

        var fraction = Math.Clamp(current / (float)required, 0f, 1f);
        var suffix = string.IsNullOrWhiteSpace(unit) ? string.Empty : $" {unit}";
        ImGui.TextUnformatted(label);
        var width = GetControlWidth(ProgressBarWidth);
        ImGui.ProgressBar(fraction, new Vector2(width, 0f), $"{current} / {required}{suffix}");
    }

    private void DrawRidingMapProgress(TargetEntry target)
    {
        if (!target.Category.Equals("Riding Maps", StringComparison.OrdinalIgnoreCase) ||
            ownership.GetItemStatus(target.ItemId).IsOwned)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted("Progress");
        ImGui.Separator();
        DrawSharedFateRequirement(target, compact: false);
    }

    private void DrawSharedFateRequirement(TargetEntry target, bool compact)
    {
        // Only Endwalker and Dawntrail normal field-zone riding maps are gated
        // by Shared FATE Rank 2. Shadowbringers traders expose their riding maps
        // without that rank gate, even though those zones also have Shared FATE data.
        if (!IsSharedFateExpansionMap(target.Name))
            return;

        var progress = sharedFates.GetForRidingMap(target.Name);
        if (progress is null)
        {
            ImGui.TextDisabled(sharedFates.HasSnapshot
                ? "Shared FATE progress for this zone was not found in the current snapshot."
                : sharedFates.InitializationAttempted
                    ? "Shared FATE progress is not currently available from the client. CQG attempted to initialize the Shared FATE data during this /cqg scan."
                    : "Shared FATE progress is not currently available from the client.");
            return;
        }

        const int requiredRank = 2;
        if (progress.CurrentRank >= requiredRank)
        {
            DrawColoredText($"Shared FATE Rank {requiredRank} requirement met", true);
            return;
        }

        var needed = progress.NeededFates > 0 ? progress.NeededFates : (ushort)6;
        var current = Math.Clamp((int)progress.FateProgress, 0, (int)needed);
        if (!compact)
            ImGui.TextUnformatted($"{progress.ZoneName} — Shared FATE Rank {progress.CurrentRank} / {requiredRank}");
        DrawProgressBar(compact ? "Shared FATE" : "Progress toward Rank 2", current, needed, "FATEs");
        var remaining = Math.Max(0, needed - current);
        ImGui.TextDisabled($"{remaining} qualifying FATE{(remaining == 1 ? string.Empty : "s")} remaining.");
    }

    private static bool IsSharedFateExpansionMap(string itemName)
    {
        // Shadowbringers Gemstone Traders sell their riding maps without a
        // Shared FATE rank gate. Rank 2 is specifically required for the normal
        // Endwalker and Dawntrail field-zone maps.
        string[] zones =
        [
            "Labyrinthos", "Thavnair", "Garlemald", "Mare Lamentorum", "Elpis", "Ultima Thule",
            "Urqopacha", "Kozama'uka", "Yak T'el", "Shaaloani", "Heritage Found", "Living Memory",
        ];
        return zones.Any(zone => itemName.Equals($"{zone} Riding Map", StringComparison.OrdinalIgnoreCase));
    }

    private void DrawEmoteSummary(EmoteTarget emote)
    {
        var unlocked = emoteCatalog.IsUnlocked(emote.EmoteId);
        ImGui.Spacing();
        if (unlocked == true)
            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
        ImGui.TextUnformatted(emote.Name.ToUpperInvariant());
        if (unlocked == true)
            ImGui.PopStyleColor();
        ImGui.SameLine();
        DrawPinToggle("Emote", emote.EmoteId, string.Empty);
        ImGui.Separator();

        if (!string.IsNullOrWhiteSpace(emote.Command))
            ImGui.TextDisabled($"Command: {emote.Command}");

        if (unlocked == true)
            DrawColoredText("Unlocked", true);
        else if (unlocked == false)
            ImGui.TextUnformatted("Locked");
        else
            ImGui.TextDisabled("Ownership state is unavailable in the current one-shot snapshot.");

        if (emote.BackingItemId != 0 && catalog.GetById(emote.BackingItemId) is { } item)
        {
            var result = resolver.Resolve(item);
            ImGui.Spacing();
            ImGui.TextUnformatted("Unlock item:");
            ImGui.SameLine();
            ImGui.TextUnformatted(item.Name);

            var primary = result.Sources.FirstOrDefault();
            ImGui.TextUnformatted("Source:");
            ImGui.SameLine();
            if (primary is null)
                ImGui.TextDisabled("No source resolved yet");
            else
            {
                ImGui.TextUnformatted(primary.Name);
                if (!string.IsNullOrWhiteSpace(primary.Detail))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"— {primary.Detail}");
                }
            }

            DrawDutyDependency(result, $"emote-{emote.EmoteId}");
            DrawExchangePrerequisiteControls(item.ItemId);
            if (result.Guide is null)
                DrawSourceActionGuidance(result);

            if (config.Detailed)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"Teaching item ID: {item.ItemId}");
                ImGui.TextDisabled($"Emote ID: {emote.EmoteId}");
                if (emote.UnlockLink != 0)
                    ImGui.TextDisabled($"Unlock link: {emote.UnlockLink}");
            }
            return;
        }

        if (emote.QuestId != 0)
        {
            var quest = questGuidance.GetById(emote.QuestId);
            ImGui.Spacing();
            ImGui.TextUnformatted("Unlock quest:");
            ImGui.SameLine();
            ImGui.TextUnformatted(quest?.Name ?? $"Quest #{emote.QuestId}");
            if (quest is not null)
            {
                if (!string.IsNullOrWhiteSpace(quest.QuestGiver) && !string.IsNullOrWhiteSpace(quest.Location))
                    DrawNpcLocationLine("Go to", quest.QuestGiver!, quest.Location, $"emote-quest-{emote.EmoteId}");
            }
            return;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("No item or quest source was resolved for this emote yet.");
        if (config.Detailed && emote.UnlockLink != 0)
            ImGui.TextDisabled($"Unlock link: {emote.UnlockLink}");
    }

    private void DrawTitleSummary(TitleTarget title)
    {
        var unlocked = titleCatalog.IsUnlocked(title.TitleId);
        ImGui.Spacing();
        if (unlocked == true)
            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
        ImGui.TextUnformatted(title.Name.ToUpperInvariant());
        if (unlocked == true)
            ImGui.PopStyleColor();
        ImGui.SameLine();
        DrawPinToggle("Title", title.TitleId, string.Empty);
        ImGui.Separator();

        if (unlocked == true)
            DrawColoredText("Unlocked", true);
        else if (unlocked == false)
            ImGui.TextUnformatted("Locked");
        else
            ImGui.TextDisabled("Ownership state is unavailable in the current one-shot snapshot.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Achievement");
        ImGui.Separator();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(title.AchievementName)
            ? $"Achievement #{title.AchievementId}"
            : title.AchievementName);
        if (!string.IsNullOrWhiteSpace(title.AchievementDescription))
            ImGui.TextWrapped(title.AchievementDescription);
    }

    private static bool IsRelicSet(TargetSet set) =>
        set.Group.Equals("Relic Sets", StringComparison.OrdinalIgnoreCase);

    private void DrawRelicSetSummary(TargetSet set, IReadOnlyList<TargetEntry> members)
    {
        var jobs = members
            .SelectMany(x => x.EquipJobs)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (jobs.Length == 0)
        {
            ImGui.TextDisabled("This relic set does not expose a job/weapon relationship in the current catalog.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SelectedRelicJob) ||
            !jobs.Contains(config.SelectedRelicJob, StringComparer.OrdinalIgnoreCase))
        {
            config.SelectedRelicJob = jobs[0];
            SaveConfig();
        }

        var jobIndex = Array.FindIndex(jobs, x => x.Equals(config.SelectedRelicJob, StringComparison.OrdinalIgnoreCase));
        if (jobIndex < 0) jobIndex = 0;
        var labels = jobs.Select(x => $"{x} — {RelicWeaponType(x)}").ToArray();

        ImGui.Spacing();
        ImGui.TextUnformatted(set.Name.ToUpperInvariant());
        ImGui.Separator();
        SetNextControlWidth(JobComboWidth);
        if (ImGui.Combo("Job / Weapon##RelicJob", ref jobIndex, labels, labels.Length))
        {
            var selectedRelicJob = jobs[jobIndex];
            NavigateTo(() => config.SelectedRelicJob = selectedRelicJob);
        }

        var selectedJob = jobs[jobIndex];
        ImGui.Spacing();
        DrawPinToggle("Set", 0, set.Id, $"Pin {selectedJob}", $"Unpin {selectedJob}", selectedJob);
        ImGui.SameLine();
        ImGui.TextDisabled($"{RelicWeaponType(selectedJob)} route only");

        var filtered = members
            .Where(x => x.EquipJobs.Contains(selectedJob))
            .ToArray();

        ImGui.Spacing();
        if (set.Id.Equals("relic-phantom", StringComparison.OrdinalIgnoreCase))
            DrawPhantomRelicGuidance(selectedJob, filtered);
        else
            DrawFilteredRelicGuidance(set, filtered);
    }

    private void DrawFilteredRelicGuidance(TargetSet set, IReadOnlyList<TargetEntry> members)
    {
        var sequence = ResolveRelicQuestSequence(members);
        if (sequence.Entries.Count > 0)
        {
            if (sequence.IsComplete)
            {
                ImGui.TextUnformatted("Formal relic quest progression complete for this selected route.");
            }
            else if (sequence.CurrentOrNext is { } actionable)
            {
                var active = sequence.Active is not null;
                ImGui.TextUnformatted(active ? "Current quest:" : "Next quest:");
                ImGui.SameLine();
                ImGui.TextUnformatted(actionable.Name);

                var who = FormatWhoWhere(actionable.Info);
                if (!string.IsNullOrWhiteSpace(who))
                    ImGui.TextDisabled($"{(active ? "Quest started by" : "Go to")}: {who}");

                if (active && sequence.AfterActive is { } after)
                    ImGui.TextDisabled($"After this: {after.Name}");
            }

            if (config.Detailed)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Relic quest route:");
                foreach (var entry in sequence.Entries)
                {
                    var marker = entry.EffectiveState == QuestProgressState.Completed ? "[X]" :
                        entry.RawState == QuestProgressState.InProgress ? "[>]" : "[ ]";
                    ImGui.BulletText($"{marker} {entry.Name}");
                }
            }
            return;
        }

        var attainedStages = GetEffectivelyOwnedSetItems(set, members);

        // No quest-backed stage relationship was exposed. Keep the resolver
        // fallback truthful rather than pretending a progression graph exists.
        foreach (var member in members)
        {
            var result = resolver.Resolve(member);
            var source = result.Sources.FirstOrDefault();
            var owned = ownership.GetItemStatus(member.ItemId);
            var attained = attainedStages.Contains(member.ItemId);
            if (attained)
                ImGui.PushStyleColor(ImGuiCol.Text, owned.IsOwned ? OwnedTextColor(owned) : OwnedGreen);
            ImGui.BulletText(attained
                ? owned.IsOwned ? $"{member.Name} — Owned ({owned.Source})" : $"{member.Name} — Earlier stage completed through upgrade"
                : source is null
                    ? $"{member.Name} — No source resolved"
                    : $"{member.Name} — {source.Name}");
            if (attained)
                ImGui.PopStyleColor();
        }
    }

    private QuestSequenceStatus ResolveRelicQuestSequence(IReadOnlyList<TargetEntry> members)
    {
        var names = new List<string>();
        foreach (var member in members.OrderBy(x => x.ItemId))
        {
            var result = resolver.Resolve(member);
            foreach (var source in result.Sources)
            {
                var extracted = QuestGuidanceIndex.ExtractQuestNames(source);
                if (extracted.Count == 0)
                    continue;

                foreach (var name in extracted)
                {
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
                break;
            }
        }

        return questGuidance.ResolveSequence(names, GetQuestStateByRowId);
    }

    private string FormatQuestPinAction(string label, string questName, bool goTo)
    {
        var info = questGuidance.GetBestByName(questName);
        var who = FormatWhoWhere(info);
        if (string.IsNullOrWhiteSpace(who))
            return $"{label}: {questName}";
        return goTo
            ? $"{label}: {questName} — Go to {who}"
            : $"{label}: {questName} — started by {who}";
    }

    private void DrawPhantomRelicGuidance(string job, IReadOnlyList<TargetEntry> members)
    {
        var progress = GetPhantomProgress();

        ImGui.TextUnformatted($"{job} — {RelicWeaponType(job)}");
        ImGui.TextDisabled("Account-wide one-time Phantom progression");

        if (progress.AllStagesUnlocked)
        {
            ImGui.TextUnformatted("All one-time Phantom unlock quests are complete.");
            ImGui.TextWrapped("CQG will now treat every stage as a repeat acquisition for the selected job.");
        }
        else if (!string.IsNullOrWhiteSpace(progress.ActiveQuest))
        {
            ImGui.TextUnformatted($"Current quest: {progress.ActiveQuest}");
            var currentWho = FormatWhoWhere(questGuidance.GetBestByName(progress.ActiveQuest));
            if (!string.IsNullOrWhiteSpace(currentWho))
                ImGui.TextDisabled($"Quest started by: {currentWho}");
            if (!string.IsNullOrWhiteSpace(progress.ActiveStage))
                ImGui.TextDisabled($"Stage: {progress.ActiveStage}");
            if (!string.IsNullOrWhiteSpace(progress.NextQuestAfterActive))
            {
                var afterWho = FormatWhoWhere(questGuidance.GetBestByName(progress.NextQuestAfterActive));
                ImGui.TextWrapped(string.IsNullOrWhiteSpace(afterWho)
                    ? $"After this: {progress.NextQuestAfterActive}"
                    : $"After this: {progress.NextQuestAfterActive} — go to {afterWho}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(progress.NextQuest))
        {
            ImGui.TextUnformatted($"Next quest: {progress.NextQuest}");
            var nextWho = FormatWhoWhere(questGuidance.GetBestByName(progress.NextQuest));
            if (!string.IsNullOrWhiteSpace(nextWho))
                ImGui.TextDisabled($"Go to: {nextWho}");
            if (!string.IsNullOrWhiteSpace(progress.NextStage))
                ImGui.TextDisabled($"Stage: {progress.NextStage}");
        }
        else
        {
            ImGui.TextUnformatted("Could not determine the next one-time Phantom quest.");
            ImGui.TextDisabled("Use Detailed > Debug > Export relic progression trace and send the CSV for diagnosis.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Selected weapon route");

        var attainedStages = GetEffectivelyOwnedSetItems(setCatalog.Get("relic-phantom")!, members);

        // Show only stages that are currently actionable for this account. Previous
        // stages become repeat exchanges; the current frontier remains a quest
        // step. Future locked stages stay hidden from the default view.
        foreach (var stage in PhantomStages)
        {
            var stageItems = members.Where(x => x.Name.EndsWith(" " + stage.Suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (stageItems.Length == 0)
                continue;

            var state = progress.StageStates.FirstOrDefault(x => x.Stage.Label.Equals(stage.Label, StringComparison.OrdinalIgnoreCase));
            if (state is null)
                continue;

            var itemNames = string.Join(" + ", stageItems.Select(x => x.Name));
            var stageOwned = stageItems.All(x => attainedStages.Contains(x.ItemId));

            if (state.Unlocked)
            {
                DrawColoredText($"{stage.Label}: {itemNames}", stageOwned);
                ImGui.Indent();
                ImGui.TextWrapped(stage.RepeatRoute);
                ImGui.Unindent();
                ImGui.Spacing();
                continue;
            }

            if (state.IsFrontier)
            {
                DrawColoredText($"{stage.Label}: {itemNames}", stageOwned);
                ImGui.Indent();
                if (!string.IsNullOrWhiteSpace(state.ActiveQuest))
                {
                    ImGui.TextWrapped($"Current one-time quest: {state.ActiveQuest}");
                    var who = FormatWhoWhere(questGuidance.GetBestByName(state.ActiveQuest));
                    if (!string.IsNullOrWhiteSpace(who))
                        ImGui.TextDisabled($"Quest started by: {who}");
                }
                else if (!string.IsNullOrWhiteSpace(state.NextQuest))
                {
                    ImGui.TextWrapped($"Next one-time quest: {state.NextQuest}");
                    var who = FormatWhoWhere(questGuidance.GetBestByName(state.NextQuest));
                    if (!string.IsNullOrWhiteSpace(who))
                        ImGui.TextDisabled($"Go to: {who}");
                }
                ImGui.Unindent();
                ImGui.Spacing();
            }
        }

        if (config.Detailed)
        {
            var future = progress.StageStates.Where(x => !x.Unlocked && !x.IsFrontier).Select(x => x.Stage.Label).ToArray();
            if (future.Length > 0)
                ImGui.TextDisabled($"Later locked stages: {string.Join(", ", future)}");
        }
    }

    private PhantomProgressSnapshot GetPhantomProgress()
    {
        var raw = new List<PhantomStageState>();
        var furthestObservedStage = -1;
        var furthestObservedQuest = -1;
        string activeQuest = string.Empty;
        string activeStage = string.Empty;

        for (var stageIndex = 0; stageIndex < PhantomStages.Length; stageIndex++)
        {
            var stage = PhantomStages[stageIndex];
            var states = stage.QuestChain.Select(GetQuestState).ToArray();
            for (var questIndex = 0; questIndex < states.Length; questIndex++)
            {
                if (states[questIndex] is QuestProgressState.Completed or QuestProgressState.InProgress)
                {
                    if (stageIndex > furthestObservedStage || (stageIndex == furthestObservedStage && questIndex > furthestObservedQuest))
                    {
                        furthestObservedStage = stageIndex;
                        furthestObservedQuest = questIndex;
                    }
                }
                if (states[questIndex] == QuestProgressState.InProgress)
                {
                    activeQuest = stage.QuestChain[questIndex];
                    activeStage = stage.Label;
                }
            }

            raw.Add(new PhantomStageState(stage, states, false, false, string.Empty, string.Empty));
        }

        var results = new List<PhantomStageState>();
        var frontierStage = -1;
        string nextQuest = string.Empty;
        string nextStage = string.Empty;
        string nextAfterActive = string.Empty;

        for (var stageIndex = 0; stageIndex < raw.Count; stageIndex++)
        {
            var entry = raw[stageIndex];
            var stage = entry.Stage;
            var states = entry.RawStates.ToArray();

            // If the player is already on a later quest, every earlier quest is
            // necessarily complete even if an individual completion-bit lookup is
            // missing/ambiguous. This mirrors a quest tracker's prerequisite logic.
            for (var questIndex = 0; questIndex < states.Length; questIndex++)
            {
                if (stageIndex < furthestObservedStage ||
                    (stageIndex == furthestObservedStage && questIndex < furthestObservedQuest))
                    states[questIndex] = QuestProgressState.Completed;
            }

            var finalIndex = states.Length - 1;
            var unlocked = finalIndex >= 0 && states[finalIndex] == QuestProgressState.Completed;

            if (!unlocked && frontierStage < 0)
            {
                frontierStage = stageIndex;
                var activeIndex = Array.FindIndex(states, x => x == QuestProgressState.InProgress);
                var nextIndex = Array.FindIndex(states, x => x == QuestProgressState.NotStarted);
                var stageActive = activeIndex >= 0 ? stage.QuestChain[activeIndex] : string.Empty;
                var stageNext = nextIndex >= 0 ? stage.QuestChain[nextIndex] : string.Empty;

                if (string.IsNullOrWhiteSpace(activeQuest) && !string.IsNullOrWhiteSpace(stageActive))
                {
                    activeQuest = stageActive;
                    activeStage = stage.Label;
                }
                if (string.IsNullOrWhiteSpace(nextQuest) && string.IsNullOrWhiteSpace(stageActive))
                {
                    nextQuest = stageNext;
                    nextStage = stage.Label;
                }
                if (!string.IsNullOrWhiteSpace(stageActive))
                {
                    var after = activeIndex + 1;
                    if (after < stage.QuestChain.Count)
                        nextAfterActive = stage.QuestChain[after];
                    else if (stageIndex + 1 < PhantomStages.Length)
                        nextAfterActive = PhantomStages[stageIndex + 1].QuestChain.FirstOrDefault() ?? string.Empty;
                }
            }

            results.Add(new PhantomStageState(
                stage,
                states,
                unlocked,
                stageIndex == frontierStage,
                states.Select((x, i) => (x, i)).FirstOrDefault(x => x.x == QuestProgressState.InProgress) is var active && active.x == QuestProgressState.InProgress ? stage.QuestChain[active.i] : string.Empty,
                states.Select((x, i) => (x, i)).FirstOrDefault(x => x.x == QuestProgressState.NotStarted) is var next && next.x == QuestProgressState.NotStarted ? stage.QuestChain[next.i] : string.Empty));
        }

        var allUnlocked = results.Count > 0 && results.All(x => x.Unlocked);
        return new PhantomProgressSnapshot(results, allUnlocked, activeQuest, activeStage, nextQuest, nextStage, nextAfterActive);
    }

    private static string RelicWeaponType(string job) => job.ToUpperInvariant() switch
    {
        "PLD" => "Sword & Shield", "WAR" => "Greataxe", "DRK" => "Greatsword", "GNB" => "Gunblade",
        "MNK" => "Fists", "DRG" => "Spear", "NIN" => "Daggers", "SAM" => "Katana", "RPR" => "Scythe", "VPR" => "Twinfangs",
        "BRD" => "Bow", "MCH" => "Gun", "DNC" => "Chakrams",
        "WHM" => "Cane", "SCH" => "Grimoire", "AST" => "Star Globe", "SGE" => "Nouliths",
        "BLM" => "Staff", "SMN" => "Grimoire", "RDM" => "Rapier", "PCT" => "Brush",
        _ => "Weapon",
    };

    private static readonly PhantomRelicStage[] PhantomStages =
    [
        new("Penumbrae", "Penumbrae", new[] { "Arcane Artistry", "Forging the Phantasmal" }, "Forging the Phantasmal",
            "Repeat route: buy 3 Arcanite from Ermina for 1,500 Allagan Tomestones of Mathematics total, then obtain the selected Penumbrae weapon from Dodokkuli."),
        new("Umbrae", "Umbrae", new[] { "Keeping the Old Ways Alive", "Aether, Aether, Everywhere", "Wrought by Hands Phantasmal" }, "Wrought by Hands Phantasmal",
            "Repeat route: bring the corresponding Penumbrae weapon plus 3 Waxing Arcanite (1,500 Mathematics total) to Dodokkuli."),
        new("Obscurum", "Obscurum", new[] { "Timeworn Techniques", "In Pursuit of Perfection", "A Phantom Reborn" }, "A Phantom Reborn",
            "Repeat route: bring the corresponding Umbrae weapon plus 3 Waning Arcanite (1,500 Mathematics total) to Dodokkuli."),
        new("Eclipticum", "Eclipticum", new[] { "Under No Illusion", "Phantoms to Fillet", "A Phantom Unveiled" }, "A Phantom Unveiled",
            "Repeat route: bring the corresponding Obscurum weapon plus 3 Ecliptic Arcanite (1,500 Mathematics total) to Dodokkuli."),
        new("Occultum", "Occultum", new[] { "Final Phantasm" }, "Final Phantasm",
            "Repeat route: bring the corresponding Eclipticum weapon to Dodokkuli and choose the weapon's bonus attributes. No Mathematics exchange is required."),
    ];

    private sealed record PhantomRelicStage(
        string Label,
        string Suffix,
        IReadOnlyList<string> QuestChain,
        string FinalQuest,
        string RepeatRoute);

    private sealed record PhantomStageState(
        PhantomRelicStage Stage,
        IReadOnlyList<QuestProgressState> RawStates,
        bool Unlocked,
        bool IsFrontier,
        string ActiveQuest,
        string NextQuest);

    private sealed record PhantomProgressSnapshot(
        IReadOnlyList<PhantomStageState> StageStates,
        bool AllStagesUnlocked,
        string ActiveQuest,
        string ActiveStage,
        string NextQuest,
        string NextStage,
        string NextQuestAfterActive);

    private void DrawSetDetailed(TargetSet set)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var members = set.ItemIds
            .Select(catalog.GetById)
            .Where(t => t is not null)
            .Cast<TargetEntry>()
            .ToArray();

        ImGui.TextUnformatted($"Set ID: {set.Id}");
        ImGui.TextUnformatted($"Group: {set.Group}");
        ImGui.TextUnformatted($"Members: {members.Length}");

        ImGui.Spacing();

        var prerequisiteMembers = members.Where(x => !IsCompletionReward(set, x)).ToArray();
        var prerequisiteOwned = prerequisiteMembers.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned);
        var prerequisitesComplete = prerequisiteMembers.Length == 0 || prerequisiteOwned == prerequisiteMembers.Length;

        foreach (var member in members)
        {
            var result = resolver.Resolve(member);
            var primary = result.Sources.FirstOrDefault();
            var ownedStatus = ownership.GetItemStatus(member.ItemId);
            var owned = ownedStatus.IsOwned;

            DrawOwnershipText(member.Name, ownedStatus);
            ImGui.Indent();

            if (!owned && IsCompletionReward(set, member) && !prerequisitesComplete)
            {
                ImGui.TextDisabled($"Completion reward locked until prerequisite collection is complete ({prerequisiteOwned} / {prerequisiteMembers.Length}).");
            }
            else
            {
                var action = owned ? SourceActionGuidance.Empty : actionGuidance.Resolve(result);
                if (!action.IsEmpty)
                {
                    ImGui.TextWrapped($"{action.ActionLabel}: {action.ActionText}");
                    if (!string.IsNullOrWhiteSpace(action.GoTo))
                        ImGui.TextDisabled($"Go to: {action.GoTo}");
                }
                else if (primary is null)
                {
                    ImGui.TextDisabled("No trusted source resolved yet.");
                }
                else
                {
                    ImGui.TextWrapped(
                        string.IsNullOrWhiteSpace(primary.Detail)
                            ? primary.Name
                            : $"{primary.Name} — {primary.Detail}");
                }
            }

            ImGui.Unindent();
        }
    }


}

// ===== Plugin.PinsAndDependencies.cs =====
public sealed partial class Plugin
{
    // The Pins tab is rendered every frame, but source resolution/frontier selection
    // is derived data. Cache that expensive portion briefly so simply leaving the
    // tab open does not re-run resolver/set-frontier work at the UI frame rate.
    private readonly Dictionary<string, PinnedUiDerivedCache> pinnedUiDerivedCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan PinnedUiRefreshInterval = TimeSpan.FromSeconds(30);
    private DateTime nextPinnedUiRefreshUtc = DateTime.MinValue;

    private sealed record PinnedProgressSnapshot(string Label, int Current, int Total, string Unit);

    private sealed record PinnedUiDerivedCache(
        string Title,
        string Subtitle,
        PinnedLocationHint? Location,
        bool Complete,
        OwnershipStatus? ItemOwnership,
        bool JobPartial,
        PinnedProgressSnapshot? Progress);

    private sealed record PinnedLocationHint(
        string Prefix,
        string Value,
        string? NpcName = null,
        string? Location = null,
        uint? NpcId = null,
        bool IsNpcLocation = false,
        bool IsLocationOnly = false);

    private void DrawCompletionPreferences()
    {
        if (!ImGui.CollapsingHeader("Completion settings"))
            return;

        var autoUnpin = config.AutoUnpinCompletedTargets;
        if (ImGui.Checkbox("Automatically unpin completed targets", ref autoUnpin))
        {
            config.AutoUnpinCompletedTargets = autoUnpin;
            SaveConfig();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Confirmed completed targets are removed automatically unless Sticky is enabled.");

        var chatNotifications = config.CompletionChatNotifications;
        if (ImGui.Checkbox("Show completion notifications in chat", ref chatNotifications))
        {
            config.CompletionChatNotifications = chatNotifications;
            SaveConfig();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("CQG announces verified target completion and any automatic pin removal.");

        ImGui.TextDisabled("Inventory acquisition is event-driven; other targets reconcile on /cqg scans.");
        ImGui.Spacing();
    }

    private void DrawPinnedTargetsTab()
    {
        RefreshPinnedUiSnapshotIfDue();

        // Keep the capacity and refresh information on one compact header row.
        ImGui.TextUnformatted($"Watchlist  {config.PinnedTargets.Count} / {PinnedTargetPolicy.MaximumPins}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pins are listed in priority order. Sticky pins remain in the Watchlist after completion, even with automatic unpinning enabled.");
        ImGui.SameLine();
        DrawPinnedRefreshCountdown();
        if (!PinnedTargetPolicy.CanAdd(config.PinnedTargets.Count))
            ImGui.TextDisabled("Pin limit reached — remove a target before adding another.");
        ImGui.Separator();

        if (ImGui.CollapsingHeader("Focused Tracking Settings"))
        {
            if (ImGui.SmallButton(config.FocusedTrackingEditMode ? "Finish Focused Layout" : "Edit Focused Layout"))
            {
                config.FocusedTrackingEditMode = !config.FocusedTrackingEditMode;
                SaveConfig();
            }
            ImGui.SameLine();
            ImGui.TextUnformatted("Text alignment:");
            ImGui.SameLine();
            if (ImGui.SmallButton(config.FocusedTrackingLeftAligned ? "Left##FocusedAlign" : "Right##FocusedAlign"))
            {
                config.FocusedTrackingLeftAligned = !config.FocusedTrackingLeftAligned;
                SaveConfig();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Choose left or right alignment for all Focused Tracking text.");

            var attached = config.FocusedTrackingAttachToQuestList;
            if (ImGui.Checkbox("Attach to Bottom of Quest List##FocusedAnchor", ref attached))
            {
                config.FocusedTrackingAttachToQuestList = attached;
                SaveConfig();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Follow the bottommost visible quest in FFXIV's native tracker. Falls back to your saved manual position when unavailable.");
            if (attached)
            {
                var gap = config.FocusedTrackingQuestListGap;
                ImGui.SetNextItemWidth(145f);
                if (ImGui.SliderFloat("Vertical spacing (px)##FocusedGap", ref gap, 0f, 32f, "%.0f"))
                {
                        config.FocusedTrackingQuestListGap = gap;
                        SaveConfig();
                }
                var horizontalOffset = config.FocusedTrackingQuestListHorizontalOffset;
                ImGui.SetNextItemWidth(145f);
                if (ImGui.SliderFloat("Horizontal offset (px)##FocusedHorizontal", ref horizontalOffset, -250f, 250f, "%.0f"))
                {
                        config.FocusedTrackingQuestListHorizontalOffset = horizontalOffset;
                        SaveConfig();
                }
                if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Negative moves the tracker left; positive moves it right relative to the native quest list.");
            }
            var showIcons = config.FocusedTrackingShowIcons;
            if (ImGui.Checkbox("Show FFXIV Icons##FocusedIcons", ref showIcons))
            {
                config.FocusedTrackingShowIcons = showIcons;
                PrimeFocusedIconsOnStartup();
                SaveConfig();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show native FFXIV icons for jobs, items, and collection sets where game data provides an appropriate icon.");

        }

        DrawCompletionPreferences();
        DrawPinnedCompletionUndo();

        if (config.PinnedTargets.Count == 0)
        {
            ImGui.TextDisabled("No targets are pinned yet. Pin items, emotes, titles, sets, duties, or class/job progression from their tabs.");
            return;
        }

        for (var i = 0; i < config.PinnedTargets.Count; i++)
        {
            var pin = config.PinnedTargets[i];
            ImGui.PushID(i);

            var derived = GetPinnedUiDerived(pin);
            var title = derived.Title;
            var subtitle = derived.Subtitle;

            if (derived.JobPartial)
            {
                DrawJobStateText($"{i + 1}. {title}", JobVisualState.Partial);
            }
            else if (derived.ItemOwnership is { } itemStatus)
            {
                DrawOwnershipText($"{i + 1}. {title}", itemStatus);
            }
            else
            {
                DrawColoredText($"{i + 1}. {title}", derived.Complete);
            }
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                ImGui.Indent();
                ImGui.TextWrapped(subtitle);
                DrawPinnedLocationLink(derived.Location, i);
                ImGui.Unindent();
            }

            if (!string.IsNullOrWhiteSpace(pin.PrerequisiteForKind))
            {
                ImGui.Indent();
                ImGui.TextDisabled($"Linked prerequisite for {DescribeLinkedPrerequisiteParent(pin)}");
                ImGui.Unindent();
            }

            DrawPinnedProgress(pin, derived);
            if (pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase))
                DrawPinnedQuantityGoal(pin);

            // Actions share a compact toolbar. The priority number is already visible
            // above, so narrow arrow buttons don't need the full width of Open/Remove.
            if (ImGui.SmallButton("Open"))
                OpenPinnedTarget(pin);

            ImGui.SameLine();
            var canMoveUp = i > 0;
            if (!canMoveUp)
                ImGui.BeginDisabled();
            var moveUp = ImGui.SmallButton("↑");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move up one priority");
            if (!canMoveUp)
                ImGui.EndDisabled();
            if (moveUp && canMoveUp)
            {
                SwapPins(i, i - 1);
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            var canMoveDown = i < config.PinnedTargets.Count - 1;
            if (!canMoveDown)
                ImGui.BeginDisabled();
            var moveDown = ImGui.SmallButton("↓");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move down one priority");
            if (!canMoveDown)
                ImGui.EndDisabled();
            if (moveDown && canMoveDown)
            {
                SwapPins(i, i + 1);
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (!canMoveUp)
                ImGui.BeginDisabled();
            var moveTop = ImGui.SmallButton("Top");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move to highest priority");
            if (!canMoveUp)
                ImGui.EndDisabled();
            if (moveTop && canMoveUp)
            {
                var moving = config.PinnedTargets[i];
                config.PinnedTargets.RemoveAt(i);
                config.PinnedTargets.Insert(0, moving);
                SaveConfig();
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(pin.IsSticky ? "Sticky ✓" : "Sticky"))
            {
                pin.IsSticky = !pin.IsSticky;
                SaveConfig();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(pin.IsSticky
                    ? "Sticky is on: keep this pin after completion. Click to turn off."
                    : "Sticky is off: this pin can automatically unpin after completion. Click to keep it.");

            ImGui.SameLine();
            if (ImGui.SmallButton(pin.FocusedTrackingEnabled ? "Focused ✓" : "Focus"))
            {
                if (!pin.FocusedTrackingEnabled && string.IsNullOrWhiteSpace(pin.FocusedTrackingId))
                    pin.FocusedTrackingId = Guid.NewGuid().ToString("N");
                if (pin.FocusedTrackingEnabled || config.PinnedTargets.Count(p => p.FocusedTrackingEnabled) < MaxFocusedTargets)
                {
                    pin.FocusedTrackingEnabled = !pin.FocusedTrackingEnabled;
                    SaveConfig();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(!pin.FocusedTrackingEnabled && config.PinnedTargets.Count(p => p.FocusedTrackingEnabled) >= MaxFocusedTargets
                    ? "Focused Tracking supports at most 3 targets. Unfocus one first."
                    : "Add or remove this Watchlist entry from the shared Focused Tracking HUD.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                config.PinnedTargets.RemoveAt(i);
                SaveConfig();
                ImGui.PopID();
                return;
            }

            ImGui.PopID();
            ImGui.Separator();
        }
    }



    // Keep Watchlist and Focused Tracking decisions in one place.
    private const int MaxFocusedTargets = 3;

    private static bool IsJobPin(PinnedTargetConfig pin) =>
        pin.Kind.Equals("JobUnlock", StringComparison.OrdinalIgnoreCase) ||
        pin.Kind.Equals("JobProgression", StringComparison.OrdinalIgnoreCase);

    private static string PinnedTargetCacheKey(PinnedTargetConfig pin) =>
        $"{pin.Kind}\u001f{pin.NumericId}\u001f{pin.StringId}\u001f{pin.Variant}";

    private static System.Numerics.Vector4 OwnedTextColor(OwnershipStatus status) =>
        IsGlamourDresserOwnership(status) ? GlamourPink : OwnedGreen;

    // Invalidate the guidance of a newly focused target without rebuilding
    // unrelated Watchlist rows. The next draw resolves this entry on demand.
    private void InvalidatePinnedUiDerived(PinnedTargetConfig pin)
    {
        var key = PinnedTargetCacheKey(pin);
        pinnedUiDerivedCache.Remove(key);
    }

    // Keep target-scoped ownership evaluation together for both independent HUD
    // initialization and scheduled updates. Never run the full catalog scan here.
    // One definition of variant-aware set membership, shared by completion,
    // guidance and the targeted Focused Tracking ownership refresh.
    private uint[] GetApplicableSetItemIds(PinnedTargetConfig pin, IEnumerable<uint> itemIds) =>
        itemIds.Where(id => string.IsNullOrWhiteSpace(pin.Variant) ||
            catalog.GetById(id)?.EquipJobs.Contains(pin.Variant) == true).ToArray();

    // Later stages of the same relic weapon imply earlier consumed stages.
    // Other collections continue to use literal item ownership.
    private HashSet<uint> GetEffectivelyOwnedSetItems(TargetSet set, IReadOnlyList<TargetEntry> members)
    {
        var owned = members.Where(member => ownership.GetItemStatus(member.ItemId).IsOwned)
            .Select(member => member.ItemId).ToHashSet();
        return RelicStageLineage.InferAttainedStages(set.Id, members, owned);
    }

    private void RefreshFocusedTargetStates(IReadOnlyList<PinnedTargetConfig> focused)
    {
        var focusedJobIds = focused
            .Where(p => IsJobPin(p))
            .Select(p => p.NumericId);
        ownership.RefreshFocusedJobUnlocks(focusedJobIds);
        // The cap is on focused TARGETS, not the number of item IDs.
        // A focused collection can contain dozens of members. Resolve the
        // applicable member IDs once per refresh, never once per draw.
        var focusedItems = new HashSet<uint>();
        foreach (var pin in focused)
        {
            if (pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase) && pin.NumericId != 0)
                focusedItems.Add(pin.NumericId);
            else if (pin.Kind.Equals("Set", StringComparison.OrdinalIgnoreCase) &&
                     setCatalog.Get(pin.StringId) is { } set)
            {
                foreach (var id in GetApplicableSetItemIds(pin, set.ItemIds))
                    if (id != 0)
                        focusedItems.Add(id);
            }
            else if (pin.Kind.Equals("Emote", StringComparison.OrdinalIgnoreCase) &&
                     pin.NumericId <= ushort.MaxValue &&
                     emoteCatalog.Get((ushort)pin.NumericId) is { } emote && emote.BackingItemId != 0)
                focusedItems.Add(emote.BackingItemId);
        }
        ownership.RefreshFocusedItemOwnership(focusedItems);
        // These are also target-scoped, not the normal /cqg catalog scan.
        emoteCatalog.RefreshFocusedUnlocks(focused
            .Where(p => p.Kind.Equals("Emote", StringComparison.OrdinalIgnoreCase) && p.NumericId <= ushort.MaxValue)
            .Select(p => (ushort)p.NumericId));
        titleCatalog.RefreshFocusedUnlocks(focused
            .Where(p => p.Kind.Equals("Title", StringComparison.OrdinalIgnoreCase) && p.NumericId <= ushort.MaxValue)
            .Select(p => (ushort)p.NumericId));
        dutyUnlocks.RefreshFocusedUnlocks(focused
            .Where(p => p.Kind.Equals("Duty", StringComparison.OrdinalIgnoreCase))
            .Select(p => dutyCatalog.Get(p.NumericId)?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!));
        foreach (var pin in focused)
            InvalidatePinnedUiDerived(pin);
    }

    private void RefreshPinnedUiSnapshotIfDue()
    {
        var now = DateTime.UtcNow;
        if (nextPinnedUiRefreshUtc == DateTime.MinValue)
        {
            // The first frame after opening the Pins tab builds one fresh snapshot.
            pinnedUiDerivedCache.Clear();
            nextPinnedUiRefreshUtc = now.Add(PinnedUiRefreshInterval);
            return;
        }

        if (now < nextPinnedUiRefreshUtc)
            return;

        // Refresh as one snapshot, never at frame rate. The next calls to
        // GetPinnedUiDerived rebuild each visible row exactly once.
        pinnedUiDerivedCache.Clear();
        nextPinnedUiRefreshUtc = now.Add(PinnedUiRefreshInterval);
    }

    private void DrawPinnedRefreshCountdown()
    {
        var remaining = nextPinnedUiRefreshUtc == DateTime.MinValue
            ? PinnedUiRefreshInterval
            : nextPinnedUiRefreshUtc - DateTime.UtcNow;
        var seconds = Math.Clamp((int)Math.Ceiling(remaining.TotalSeconds), 0, (int)PinnedUiRefreshInterval.TotalSeconds);
        var text = $"{seconds} Seconds Until Refresh";
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var textWidth = ImGui.CalcTextSize(text).X;
        if (availableWidth > textWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availableWidth - textWidth);
        ImGui.TextDisabled(text);
    }

    private void DrawPinnedQuantityGoal(PinnedTargetConfig pin)
    {
        var value = Math.Max(1, pin.DesiredQuantity);
        ImGui.Indent();
        ImGui.TextDisabled("Qty goal (minimum 1):");
        ImGui.SameLine();
        var modified = false;
        if (ImGui.SmallButton("+##quantity"))
        {
            value = Math.Min(999999, value + 1);
            modified = true;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(66);
        if (ImGui.InputInt("##quantity", ref value, 0, 0)) modified = true;
        ImGui.SameLine();
        if (ImGui.SmallButton("-##quantity"))
        {
            value = Math.Max(1, value - 1);
            modified = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Goal is the number of copies currently held in accessible inventory (minimum 1).");
        if (modified)
        {
            var next = Math.Clamp(value, 1, 999999);
            if (next != pin.DesiredQuantity)
            {
                pin.DesiredQuantity = next;
                QueuePinnedQuantityRecheck(pin.NumericId);
                SaveConfig();
            }
        }
        if (pin.DesiredQuantity > 1 && pinnedItemQuantityCounts.TryGetValue(pin.NumericId, out var count))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{count} / {pin.DesiredQuantity}");
        }
        ImGui.Unindent();
    }

    private void DrawPinnedProgress(PinnedTargetConfig pin, PinnedUiDerivedCache derived)
    {
        if (derived.Progress is { } progress)
        {
            ImGui.Indent();
            DrawProgressBar(progress.Label, progress.Current, progress.Total, progress.Unit);
            ImGui.Unindent();
            return;
        }

        // Riding-map Shared FATE presentation has its own richer widget. Keep it
        // live; all common set/job progress is snapshotted above so the Pins tab
        // does not repeatedly walk catalogs/quest sequences every frame.
        if (pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase))
        {
            var item = catalog.GetById(pin.NumericId);
            if (item is not null && derived.ItemOwnership?.IsOwned != true &&
                item.Category.Equals("Riding Maps", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.Indent();
                DrawSharedFateRequirement(item, compact: true);
                ImGui.Unindent();
            }
        }
    }

    private PinnedUiDerivedCache GetPinnedUiDerived(PinnedTargetConfig pin)
    {
        var key = PinnedTargetCacheKey(pin);
        if (pinnedUiDerivedCache.TryGetValue(key, out var cached))
            return cached;

        var title = DescribePinTitle(pin);
        var complete = IsPinComplete(pin);
        OwnershipStatus? itemOwnership = null;
        var jobPartial = false;
        PinnedProgressSnapshot? progress = null;

        if (pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase))
        {
            itemOwnership = ownership.GetItemStatus(pin.NumericId);
            complete = itemOwnership.IsOwned;
        }
        else if (pin.Kind.Equals("JobProgression", StringComparison.OrdinalIgnoreCase) &&
                 jobCatalog.Get(pin.NumericId) is { } pinnedJob)
        {
            var unlocked = ownership.IsJobUnlocked(pinnedJob.ClassJobId);
            jobPartial = unlocked && !complete;
            if (unlocked && pinnedJob.Progression.Count > 0)
            {
                var completed = CountConfirmedProgressionQuests(pinnedJob);
                progress = new PinnedProgressSnapshot("Quest progression", completed, GetApplicableProgression(pinnedJob).Count, "quests");
            }
        }
        else if (pin.Kind.Equals("Set", StringComparison.OrdinalIgnoreCase) &&
                 setCatalog.Get(pin.StringId) is { } set)
        {
            var members = GetApplicableSetItemIds(pin, set.ItemIds)
                .Select(catalog.GetById)
                .Where(x => x is not null)
                .Cast<TargetEntry>()
                .ToArray();
            if (members.Length > 0)
            {
                var owned = GetEffectivelyOwnedSetItems(set, members).Count;
                var applicableIds = GetApplicableSetItemIds(pin, set.ItemIds);
                progress = new PinnedProgressSnapshot("Collection progress", owned, applicableIds.Length,
                    RelicStageLineage.UsesStages(set.Id) ? "stages" : "owned");
                complete = applicableIds.Length > 0 && members.Length == applicableIds.Length &&
                    owned == applicableIds.Length;
            }
        }

        var rebuilt = new PinnedUiDerivedCache(
            title,
            DescribePinSubtitle(pin),
            ResolvePinnedLocationHint(pin),
            complete,
            itemOwnership,
            jobPartial,
            progress);
        pinnedUiDerivedCache[key] = rebuilt;
        return rebuilt;
    }

    private PinnedLocationHint? ResolvePinnedLocationHint(PinnedTargetConfig pin)
    {
        switch (pin.Kind)
        {
            case "Item":
            {
                var item = catalog.GetById(pin.NumericId);
                if (item is null || ownership.GetItemStatus(item.ItemId).IsOwned)
                    return null;
                var action = actionGuidance.Resolve(resolver.Resolve(item));
                if (!string.IsNullOrWhiteSpace(action.GoTo))
                    return new PinnedLocationHint("Map", action.GoTo, NpcId: action.Source?.NpcId);
                if (!string.IsNullOrWhiteSpace(action.QuestStartedBy))
                    return new PinnedLocationHint("Map", action.QuestStartedBy, NpcId: action.Source?.NpcId);
                return null;
            }
            case "Emote":
            {
                if (pin.NumericId > ushort.MaxValue || emoteCatalog.Get((ushort)pin.NumericId) is not { } emote ||
                    emoteCatalog.IsUnlocked(emote.EmoteId) == true)
                    return null;

                if (emote.BackingItemId != 0 && catalog.GetById(emote.BackingItemId) is { } item)
                {
                    var action = actionGuidance.Resolve(resolver.Resolve(item));
                    if (!string.IsNullOrWhiteSpace(action.GoTo))
                        return new PinnedLocationHint("Map", action.GoTo, NpcId: action.Source?.NpcId);
                    if (!string.IsNullOrWhiteSpace(action.QuestStartedBy))
                        return new PinnedLocationHint("Map", action.QuestStartedBy, NpcId: action.Source?.NpcId);
                    return null;
                }

                if (emote.QuestId != 0 && questGuidance.GetById(emote.QuestId) is { } quest &&
                    !string.IsNullOrWhiteSpace(quest.QuestGiver))
                {
                    return new PinnedLocationHint("Map", quest.QuestGiver!, quest.QuestGiver, quest.Location, quest.IssuerNpcId, IsNpcLocation: true);
                }
                return null;
            }
            case "Duty":
            {
                var duty = dutyCatalog.Get(pin.NumericId);
                if (duty is null || dutyUnlocks.IsDutyUnlocked(duty.Name) == true)
                    return null;
                var advice = dutyUnlocks.GetAdvice(duty.Name);
                if (!string.IsNullOrWhiteSpace(advice?.Contact))
                    return new PinnedLocationHint("Map", advice!.Contact!, advice.Contact, advice.Location, IsNpcLocation: true);
                if (!string.IsNullOrWhiteSpace(advice?.Location))
                    return new PinnedLocationHint("Map", advice!.Location!, Location: advice.Location, IsLocationOnly: true);
                return null;
            }
            case "JobUnlock":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null || ownership.IsJobUnlocked(job.ClassJobId))
                    return null;
                var step = GetMissingBaseClassUnlock(job)?.UnlockQuest;
                if (step is null && GetMissingBaseClassProgression(job) is { } parent)
                    step = GetNextConfirmedProgressionQuest(parent);
                step ??= job.UnlockQuest;
                if (string.IsNullOrWhiteSpace(step?.QuestGiver))
                    return null;
                return new PinnedLocationHint("Map", step.QuestGiver!, step.QuestGiver, step.Location, null, IsNpcLocation: true);
            }
            case "JobProgression":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null || !ownership.IsJobUnlocked(job.ClassJobId))
                    return null;
                var sequence = ResolveJobQuestSequence(GetApplicableProgression(job));
                var actionable = sequence.CurrentOrNext;
                if (string.IsNullOrWhiteSpace(actionable?.Info?.QuestGiver))
                    return null;
                return new PinnedLocationHint("Map", actionable!.Info!.QuestGiver!, actionable.Info.QuestGiver, actionable.Info.Location, null, IsNpcLocation: true);
            }
            case "Set":
            {
                var set = setCatalog.Get(pin.StringId);
                if (set is null)
                    return null;
                var members = set.ItemIds
                    .Select(catalog.GetById)
                    .Where(x => x is not null)
                    .Cast<TargetEntry>()
                    .Where(x => string.IsNullOrWhiteSpace(pin.Variant) || x.EquipJobs.Contains(pin.Variant))
                    .ToArray();
                var frontier = GetSetFrontierChoice(set, members);
                if (frontier is null)
                    return null;
                if (!string.IsNullOrWhiteSpace(frontier.Action.GoTo))
                    return new PinnedLocationHint("Map", frontier.Action.GoTo, NpcId: frontier.Action.Source?.NpcId);
                if (!string.IsNullOrWhiteSpace(frontier.DutyAdvice?.Contact))
                    return new PinnedLocationHint("Map", frontier.DutyAdvice!.Contact!, frontier.DutyAdvice.Contact, frontier.DutyAdvice.Location, IsNpcLocation: true);
                return null;
            }
            default:
                return null;
        }
    }

    private void DrawPinnedLocationLink(PinnedLocationHint? hint, int index)
    {
        if (hint is null)
            return;

        if (hint.IsNpcLocation && !string.IsNullOrWhiteSpace(hint.NpcName))
        {
            DrawNpcLocationLine(hint.Prefix, hint.NpcName!, hint.Location, $"pin-cached-{index}", hint.NpcId);
            return;
        }

        if (hint.IsLocationOnly && !string.IsNullOrWhiteSpace(hint.Location))
        {
            DrawLocationOnlyLine(hint.Prefix, hint.Location!, $"pin-cached-{index}");
            return;
        }

        DrawCombinedWhoWhereLine(hint.Prefix, hint.Value, $"pin-cached-{index}", hint.NpcId);
    }

    private static void DrawColoredText(string text, bool green)
    {
        if (green)
            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
        ImGui.TextUnformatted(text);
        if (green)
            ImGui.PopStyleColor();
    }

    private static bool IsGlamourDresserOwnership(OwnershipStatus status) =>
        status.IsOwned && status.Source.Equals("Glamour Dresser", StringComparison.OrdinalIgnoreCase);

    private static void DrawOwnershipText(string text, OwnershipStatus status)
    {
        if (status.IsOwned)
            ImGui.PushStyleColor(ImGuiCol.Text, IsGlamourDresserOwnership(status) ? GlamourPink : OwnedGreen);
        ImGui.TextUnformatted(text);
        if (status.IsOwned)
            ImGui.PopStyleColor();
    }

    private enum JobVisualState
    {
        Locked,
        Partial,
        Complete,
    }

    private JobVisualState GetJobVisualState(JobProgressionTarget job)
    {
        if (!ownership.IsJobUnlocked(job.ClassJobId))
            return JobVisualState.Locked;

        // Unlock and quest completion are independent facts. Do not infer
        // completed class quests from the derived job's soul crystal or level.
        if (GetApplicableProgression(job).Count == 0)
            return JobVisualState.Partial;

        return AreAllJobQuestsConfirmedComplete(job)
            ? JobVisualState.Complete
            : JobVisualState.Partial;
    }

    // Resolve the earliest class gate for a derived job. An unowned base class
    // always precedes both its class quests and the derived job unlock.
    private JobProgressionTarget? GetMissingBaseClassUnlock(JobProgressionTarget job)
    {
        if (job.ParentClassJobId == 0 || job.ParentClassJobId == job.ClassJobId)
            return null;
        var parent = jobCatalog.Get(job.ParentClassJobId);
        return parent is not null && !ownership.IsJobUnlocked(parent.ClassJobId) ? parent : null;
    }

    private JobProgressionTarget? GetMissingBaseClassProgression(JobProgressionTarget job)
    {
        if (job.ParentClassJobId == 0 || job.ParentClassJobId == job.ClassJobId)
            return null;
        var parent = jobCatalog.Get(job.ParentClassJobId);
        if (parent is null || !ownership.IsJobUnlocked(parent.ClassJobId))
            return null;
        return job.UnlockPrerequisites.Any(requirement =>
            !IsQuestCompleteByRowId(requirement.QuestId) &&
            GetApplicableProgression(parent).Any(step => step.QuestId == requirement.QuestId))
            ? parent : null;
    }

    private bool AreAllJobQuestsConfirmedComplete(JobProgressionTarget job) =>
        HasCompleteProgressionCoverage(job) &&
        GetApplicableProgression(job).Count > 0 &&
        GetApplicableProgression(job).All(step => step.QuestId != 0 &&
            GetQuestStateByRowId(step.QuestId) == QuestProgressState.Completed);

    // ARR advanced jobs share level data with their base class, so a nonzero
    // ClassJob level cannot establish that the soul-crystal job is unlocked.
    // Completion of the specific advanced-job unlock quest is authoritative.
    // Actual job access is proven by the soul crystal, not by a completed
    // quest or by the ARR base class's shared level.
    private bool IsDerivedJobUnlockQuestComplete(JobProgressionTarget job) =>
        ownership.IsJobUnlocked(job.ClassJobId);

    private JobProgressionTarget? GetUnlockedDerivedJob(JobProgressionTarget baseClass) =>
        jobCatalog.Jobs.FirstOrDefault(candidate =>
            candidate.ClassJobId != baseClass.ClassJobId &&
            candidate.ParentClassJobId == baseClass.ClassJobId &&
            IsDerivedJobUnlockQuestComplete(candidate));

    private static void DrawJobStateText(string text, JobVisualState state)
    {
        if (state != JobVisualState.Locked)
            ImGui.PushStyleColor(ImGuiCol.Text, state == JobVisualState.Complete ? OwnedGreen : PartialBlue);
        ImGui.TextUnformatted(text);
        if (state != JobVisualState.Locked)
            ImGui.PopStyleColor();
    }

    private bool IsPinComplete(PinnedTargetConfig pin)
    {
        switch (pin.Kind)
        {
            case "Item":
                return ownership.GetItemStatus(pin.NumericId).IsOwned;
            case "Emote":
                return pin.NumericId <= ushort.MaxValue && emoteCatalog.IsUnlocked((ushort)pin.NumericId) == true;
            case "Title":
                return pin.NumericId <= ushort.MaxValue && titleCatalog.IsUnlocked((ushort)pin.NumericId) == true;
            case "Set":
            {
                var set = setCatalog.Get(pin.StringId);
                if (set is null)
                    return false;
                var members = set.ItemIds
                    .Select(catalog.GetById)
                    .Where(x => x is not null)
                    .Cast<TargetEntry>()
                    .Where(x => string.IsNullOrWhiteSpace(pin.Variant) || x.EquipJobs.Contains(pin.Variant))
                    .ToArray();
                // Do not silently discard missing catalog IDs and then treat a
                // truncated set as complete. For job-specific variants, only IDs
                // with a positively matched job are applicable.
                var applicableIds = GetApplicableSetItemIds(pin, set.ItemIds);
                return applicableIds.Length > 0 && members.Length == applicableIds.Length &&
                    GetEffectivelyOwnedSetItems(set, members).Count == applicableIds.Length;
            }
            case "JobUnlock":
                return ownership.IsJobUnlocked(pin.NumericId);
            case "Duty":
            {
                var duty = dutyCatalog.Get(pin.NumericId);
                return duty is not null && dutyUnlocks.IsDutyUnlocked(duty.Name) == true;
            }
            case "JobProgression":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null)
                    return false;
                if (!ownership.IsJobUnlocked(job.ClassJobId))
                    return false;
                // Completion requires every formal class/job quest to be
                // confirmed complete. Unlocks, levels, and inferred sequence
                // entries are not substitutes for completed quest flags.
                return AreAllJobQuestsConfirmedComplete(job);
            }
            default:
                return false;
        }
    }

    private TargetSet? FindContainingSet(uint itemId) =>
        setCatalog.Sets.FirstOrDefault(set => set.ItemIds.Contains(itemId));

    private DutyTarget? GetDutyFromSource(SourceEntry source)
    {
        var dutyName = DutyUnlockGuidanceIndex.ExtractDutyName(source);
        if (string.IsNullOrWhiteSpace(dutyName))
            return null;

        var duty = dutyCatalog.GetByName(dutyName);
        if (duty is not null)
            return duty;

        var cfcId = dutyUnlocks.GetContentFinderConditionId(dutyName);
        return cfcId == 0 ? null : dutyCatalog.Get(cfcId);
    }

    private void DrawDutyDependency(AcquisitionResult result, string idPrefix)
    {
        SourceEntry? source = null;
        string? dutyName = null;
        DutyTarget? duty = null;
        uint dutyPinId = 0;

        // Only promote a source into the duty dependency block when it can be
        // tied to an actual ContentFinderCondition row. This catches exact
        // Activity sources such as "Containment Bay P1T6 (Extreme)" while
        // avoiding broad labels such as "Duty Chest".
        foreach (var candidate in result.Sources)
        {
            var candidateName = DutyUnlockGuidanceIndex.ExtractDutyName(candidate);
            if (string.IsNullOrWhiteSpace(candidateName))
                continue;

            var candidateDuty = dutyCatalog.GetByName(candidateName);
            var candidateId = candidateDuty?.ContentFinderConditionId ??
                              dutyUnlocks.GetContentFinderConditionId(candidateName);
            if (candidateId == 0)
                continue;

            source = candidate;
            dutyName = candidateDuty?.Name ?? candidateName;
            duty = candidateDuty ?? dutyCatalog.Get(candidateId);
            dutyPinId = candidateId;
            break;
        }

        if (source is null || string.IsNullOrWhiteSpace(dutyName) || dutyPinId == 0)
            return;

        var advice = dutyUnlocks.GetAdvice(dutyName);
        var unlocked = advice?.IsUnlocked ?? dutyUnlocks.IsDutyUnlocked(dutyName);

        ImGui.Spacing();
        ImGui.TextUnformatted("Duty:");
        ImGui.SameLine();
        if (unlocked == true)
            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
        ImGui.TextUnformatted(duty?.DisplayLabel ?? dutyCatalog.GetDisplayLabel(dutyName));
        if (unlocked == true)
            ImGui.PopStyleColor();

        ImGui.SameLine();
        DrawPinToggle("Duty", dutyPinId, string.Empty, "Pin Duty", "Unpin Duty",
            prerequisiteForKind: "Item", prerequisiteForNumericId: result.Target.ItemId);

        if (string.Equals(source.SpecificityHint, "Exchange dependency", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(source.Detail))
        {
            ImGui.TextDisabled(source.Detail);
        }

        if (unlocked == true)
        {
            DrawColoredText("Duty unlocked", true);
            return;
        }

        if (advice is null)
        {
            ImGui.TextDisabled("Duty is locked; CQG could not resolve its unlock quest.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(advice.QuestName))
            ImGui.TextWrapped($"Unlock quest: {advice.QuestName}");
        else
            ImGui.TextDisabled("Unlock quest was not resolved.");

        if (!string.IsNullOrWhiteSpace(advice.Contact))
            DrawNpcLocationLine("Go to", advice.Contact!, advice.Location, $"{idPrefix}-{duty?.ContentFinderConditionId ?? 0}");
        else if (!string.IsNullOrWhiteSpace(advice.Location))
            DrawLocationOnlyLine("Location", advice.Location!, $"{idPrefix}-{duty?.ContentFinderConditionId ?? 0}");
    }

    private DutyUnlockAdvice? GetDutyUnlockAdvice(TargetSet set, TargetEntry member)
    {
        var result = resolver.Resolve(member);
        foreach (var source in result.Sources)
        {
            var duty = DutyUnlockGuidanceIndex.ExtractDutyName(source);
            if (string.IsNullOrWhiteSpace(duty))
                continue;
            return dutyUnlocks.GetAdvice(set.Id, duty);
        }
        return null;
    }

    private sealed record SetFrontierChoice(
        TargetEntry Member,
        AcquisitionResult Result,
        SourceActionGuidance Action,
        DutyUnlockAdvice? DutyAdvice,
        int Priority);

    private static bool IsCompletionReward(TargetSet set, TargetEntry member) =>
        set.CompletionRewardItemIds.Contains(member.ItemId);

    private SetFrontierChoice? GetSetFrontierChoice(TargetSet set, IReadOnlyList<TargetEntry> members)
    {
        var regularMissing = members
            .Where(member => !IsCompletionReward(set, member))
            .Where(member => !ownership.GetItemStatus(member.ItemId).IsOwned)
            .ToArray();

        // Completion rewards are deliberately excluded until every prerequisite
        // member is owned. This prevents a meta-mount quest from being presented
        // as the next step while the actual trial mounts are still missing.
        var candidates = regularMissing.Length > 0
            ? regularMissing
            : members
                .Where(member => IsCompletionReward(set, member))
                .Where(member => !ownership.GetItemStatus(member.ItemId).IsOwned)
                .ToArray();

        SetFrontierChoice? best = null;
        foreach (var member in candidates)
        {
            var result = resolver.Resolve(member);
            var action = actionGuidance.Resolve(result);
            var advice = GetDutyUnlockAdvice(set, member);

            // Prefer something the character can do right now over content that
            // first needs an unlock. Preserve set/catalog order for ties.
            var priority = action.ActionLabel switch
            {
                "Current quest" => 0,
                "Run" => 0,
                "Go to" => 1,
                "Next quest" => 1,
                "Unlock content" => 2,
                _ when advice?.IsUnlocked == true => 0,
                _ when advice is not null => 2,
                _ when result.Sources.Count > 0 => 3,
                _ => 4,
            };

            if (best is null || priority < best.Priority)
                best = new SetFrontierChoice(member, result, action, advice, priority);
        }

        return best;
    }

    private void DrawSetFrontierGuidance(TargetSet set, IReadOnlyList<TargetEntry> members)
    {
        var prerequisiteMembers = members
            .Where(member => !IsCompletionReward(set, member))
            .ToArray();
        var completionRewards = members
            .Where(member => IsCompletionReward(set, member))
            .ToArray();
        var prerequisiteOwned = prerequisiteMembers.Count(member => ownership.GetItemStatus(member.ItemId).IsOwned);
        var prerequisitesComplete = prerequisiteMembers.Length == 0 || prerequisiteOwned == prerequisiteMembers.Length;

        var missingDutyMembers = prerequisiteMembers
            .Where(member => !ownership.GetItemStatus(member.ItemId).IsOwned)
            .Select(member => (Member: member, Advice: GetDutyUnlockAdvice(set, member)))
            .Where(x => x.Advice is not null)
            .ToArray();
        if (missingDutyMembers.Length > 0)
        {
            var unlockedDuties = missingDutyMembers.Count(x => x.Advice!.IsUnlocked == true);
            ImGui.TextDisabled($"Content unlock state: {unlockedDuties} / {missingDutyMembers.Length} remaining duty sources detected as unlocked.");
        }

        if (completionRewards.Length > 0 && !prerequisitesComplete)
        {
            foreach (var reward in completionRewards.Where(reward => !ownership.GetItemStatus(reward.ItemId).IsOwned))
            {
                ImGui.TextDisabled(
                    $"Completion reward locked: {reward.Name} — requires the prerequisite collection ({prerequisiteOwned} / {prerequisiteMembers.Length} owned).");
            }
        }
        else if (completionRewards.Length > 0 && completionRewards.Any(reward => !ownership.GetItemStatus(reward.ItemId).IsOwned))
        {
            ImGui.TextDisabled("Prerequisite collection complete — the completion reward is now actionable.");
        }

        var frontier = GetSetFrontierChoice(set, members);
        if (frontier is null)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted(IsCompletionReward(set, frontier.Member)
            ? $"Next completion reward: {frontier.Member.Name}"
            : $"Next actionable target: {frontier.Member.Name}");

        if (!frontier.Action.IsEmpty)
        {
            var actionIsUnlockedDuty = frontier.Action.ActionLabel.Equals("Run", StringComparison.OrdinalIgnoreCase);
            var actionDuty = dutyCatalog.GetByName(frontier.Action.ActionText);
            var actionText = actionIsUnlockedDuty && actionDuty is not null
                ? actionDuty.DisplayLabel
                : frontier.Action.ActionText;
            if (actionIsUnlockedDuty)
                ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
            ImGui.TextUnformatted($"{frontier.Action.ActionLabel}: {actionText}");
            if (actionIsUnlockedDuty)
                ImGui.PopStyleColor();
            if (actionDuty is not null)
            {
                ImGui.SameLine();
                DrawPinToggle("Duty", actionDuty.ContentFinderConditionId, string.Empty, "Pin Duty", "Unpin Duty");
            }

            if (!string.IsNullOrWhiteSpace(frontier.Action.GoTo))
                DrawCombinedWhoWhereLine("Go to", frontier.Action.GoTo, $"set-frontier-{frontier.Member.ItemId}");
            else if (!string.IsNullOrWhiteSpace(frontier.Action.QuestStartedBy))
                DrawCombinedWhoWhereLine("Quest started by", frontier.Action.QuestStartedBy, $"set-frontier-start-{frontier.Member.ItemId}");
            return;
        }

        if (frontier.DutyAdvice is { } advice)
        {
            var duty = dutyCatalog.GetByName(advice.DutyName);
            if (advice.IsUnlocked == true)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                ImGui.TextUnformatted($"Run: {duty?.DisplayLabel ?? dutyCatalog.GetDisplayLabel(advice.DutyName)}");
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextUnformatted($"Unlock content: {duty?.DisplayLabel ?? dutyCatalog.GetDisplayLabel(advice.DutyName)}");
                if (!string.IsNullOrWhiteSpace(advice.QuestName))
                    ImGui.TextUnformatted($"Unlock via: {advice.QuestName}");
                if (!string.IsNullOrWhiteSpace(advice.Contact))
                    DrawNpcLocationLine("Talk to", advice.Contact!, advice.Location, $"set-duty-{frontier.Member.ItemId}");
                else if (!string.IsNullOrWhiteSpace(advice.Location))
                    DrawLocationOnlyLine("Location", advice.Location!, $"set-duty-{frontier.Member.ItemId}");
                if (!string.IsNullOrWhiteSpace(advice.Note))
                    ImGui.TextDisabled(advice.Note);
            }

            if (duty is not null)
            {
                ImGui.SameLine();
                DrawPinToggle("Duty", duty.ContentFinderConditionId, string.Empty, "Pin Duty", "Unpin Duty");
            }
            return;
        }

        var primary = frontier.Result.Sources.FirstOrDefault();
        if (primary is not null)
        {
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(primary.Detail)
                ? $"Source: {primary.Name}"
                : $"Source: {primary.Name} — {primary.Detail}");
        }
        else
        {
            ImGui.TextDisabled("No trusted acquisition source resolved for this frontier target yet.");
        }
    }

    private string DescribePinTitle(PinnedTargetConfig pin)
    {
        return pin.Kind switch
        {
            "Item" => catalog.GetById(pin.NumericId) is { } item
                ? $"[ITEM] {item.Name}"
                : $"[ITEM] Missing item #{pin.NumericId}",
            "Emote" => pin.NumericId <= ushort.MaxValue && emoteCatalog.Get((ushort)pin.NumericId) is { } emote
                ? $"[EMOTE] {emote.Name}"
                : $"[EMOTE] Missing emote #{pin.NumericId}",
            "Title" => pin.NumericId <= ushort.MaxValue && titleCatalog.Get((ushort)pin.NumericId) is { } title
                ? $"[TITLE] {title.Name}"
                : $"[TITLE] Missing title #{pin.NumericId}",
            "Set" => setCatalog.Get(pin.StringId) is { } set
                ? $"[SET] {set.Name}{(string.IsNullOrWhiteSpace(pin.Variant) ? string.Empty : $" — {pin.Variant} {RelicWeaponType(pin.Variant)}")}"
                : $"[SET] Missing set {pin.StringId}",
            "JobUnlock" => jobCatalog.Get(pin.NumericId) is { } unlockJob
                ? GetMissingBaseClassUnlock(unlockJob) is { } baseUnlock
                    ? $"[JOB] Unlock {baseUnlock.Name} (for {unlockJob.Name})"
                    : GetMissingBaseClassProgression(unlockJob) is { } baseProgression
                        ? $"[JOB] {baseProgression.Name} Progression (for {unlockJob.Name})"
                        : $"[JOB] Unlock {unlockJob.Name}"
                : $"[JOB] Missing ClassJob #{pin.NumericId}",
            "JobProgression" => jobCatalog.Get(pin.NumericId) is { } progressionJob
                ? $"[JOB] {progressionJob.Name} Progression"
                : $"[JOB] Missing ClassJob #{pin.NumericId}",
            "Duty" => dutyCatalog.Get(pin.NumericId) is { } duty
                ? $"[DUTY] {duty.DisplayLabel}"
                : $"[DUTY] Missing ContentFinderCondition #{pin.NumericId}",
            _ => $"[UNKNOWN] {pin.Kind}",
        };
    }

    private string DescribePinSubtitle(PinnedTargetConfig pin)
    {
        switch (pin.Kind)
        {
            case "Item":
            {
                var item = catalog.GetById(pin.NumericId);
                if (item is null)
                    return "Target is no longer present in the current catalog.";

                var owned = ownership.GetItemStatus(item.ItemId);
                if (owned.IsOwned)
                    return $"Owned — {owned.Source}";

                var result = resolver.Resolve(item);
                var action = actionGuidance.Resolve(result);
                if (!action.IsEmpty)
                {
                    var who = !string.IsNullOrWhiteSpace(action.GoTo)
                        ? $" — Go to {action.GoTo}"
                        : string.Empty;
                    return $"{action.ActionLabel}: {action.ActionText}{who}";
                }

                var primary = result.Sources.FirstOrDefault();
                return primary is null
                    ? "No source resolved."
                    : string.IsNullOrWhiteSpace(primary.Detail)
                        ? $"Next: {primary.Name}"
                        : $"Next: {primary.Name} — {primary.Detail}";
            }
            case "Emote":
            {
                if (pin.NumericId > ushort.MaxValue || emoteCatalog.Get((ushort)pin.NumericId) is not { } emote)
                    return "Emote is no longer present in the current game data.";
                if (emoteCatalog.IsUnlocked(emote.EmoteId) == true)
                    return "Unlocked.";
                if (emote.BackingItemId != 0 && catalog.GetById(emote.BackingItemId) is { } item)
                {
                    var action = actionGuidance.Resolve(resolver.Resolve(item));
                    return action.IsEmpty
                        ? $"Learn via {item.Name}."
                        : $"{action.ActionLabel}: {action.ActionText}{(string.IsNullOrWhiteSpace(action.GoTo) ? string.Empty : $" — Go to {action.GoTo}")}";
                }
                if (emote.QuestId != 0)
                {
                    var quest = questGuidance.GetById(emote.QuestId);
                    return $"Unlock via quest: {quest?.Name ?? $"Quest #{emote.QuestId}"}.";
                }
                return "Unlock source is not yet resolved.";
            }
            case "Title":
            {
                if (pin.NumericId > ushort.MaxValue || titleCatalog.Get((ushort)pin.NumericId) is not { } title)
                    return "Title is no longer present in the current game data.";
                var unlocked = titleCatalog.IsUnlocked(title.TitleId);
                if (unlocked == true)
                    return "Unlocked.";
                var achievement = string.IsNullOrWhiteSpace(title.AchievementName)
                    ? $"Achievement #{title.AchievementId}"
                    : title.AchievementName;
                return $"Unlock via achievement: {achievement}";
            }
            case "Set":
            {
                var set = setCatalog.Get(pin.StringId);
                if (set is null)
                    return "Set definition is no longer present.";

                var members = set.ItemIds
                    .Select(catalog.GetById)
                    .Where(x => x is not null)
                    .Cast<TargetEntry>()
                    .Where(x => string.IsNullOrWhiteSpace(pin.Variant) || x.EquipJobs.Contains(pin.Variant))
                    .ToArray();
                if (set.Id.Equals("relic-phantom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pin.Variant))
                {
                    var progress = GetPhantomProgress();
                    if (progress.AllStagesUnlocked)
                        return "Account-wide Phantom unlocks complete — repeat exchanges are available for this job.";
                    if (!string.IsNullOrWhiteSpace(progress.ActiveQuest))
                        return FormatQuestPinAction("Current Phantom quest", progress.ActiveQuest, false);
                    return !string.IsNullOrWhiteSpace(progress.NextQuest)
                        ? FormatQuestPinAction("Next Phantom quest", progress.NextQuest, true)
                        : "Phantom progression state needs review.";
                }

                if (IsRelicSet(set) && members.Length > 0)
                {
                    var sequence = ResolveRelicQuestSequence(members);
                    if (sequence.CurrentOrNext is { } actionable)
                    {
                        var label = sequence.Active is not null ? "Current quest" : "Next quest";
                        var who = sequence.Active is null ? FormatWhoWhere(actionable.Info) : string.Empty;
                        return string.IsNullOrWhiteSpace(who)
                            ? $"{label}: {actionable.Name}"
                            : $"{label}: {actionable.Name} — Go to {who}";
                    }
                }

                var ownedCount = GetEffectivelyOwnedSetItems(set, members).Count;
                // Never report a collection complete if its definition contains
                // members not present in the target catalog (or a variant could
                // not resolve to a valid member). Missing data is not ownership.
                var expectedMembers = set.ItemIds.Count(id =>
                    string.IsNullOrWhiteSpace(pin.Variant) ||
                    catalog.GetById(id)?.EquipJobs.Contains(pin.Variant) == true);
                if (expectedMembers > 0 && members.Length == expectedMembers && ownedCount == expectedMembers)
                    return $"Complete — {ownedCount} / {expectedMembers} owned.";

                var prerequisiteMembers = members.Where(x => !IsCompletionReward(set, x)).ToArray();
                var prerequisiteOwned = prerequisiteMembers.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned);
                var frontier = GetSetFrontierChoice(set, members);
                if (frontier is not null)
                {
                    var prefix = IsCompletionReward(set, frontier.Member)
                        ? "Completion reward"
                        : "Next target";
                    if (!frontier.Action.IsEmpty)
                    {
                        var who = !string.IsNullOrWhiteSpace(frontier.Action.GoTo)
                            ? $" — Go to {frontier.Action.GoTo}"
                            : string.Empty;
                        return $"{prefix}: {frontier.Member.Name} — {frontier.Action.ActionLabel}: {frontier.Action.ActionText}{who}";
                    }

                    if (frontier.DutyAdvice is { } advice)
                    {
                        if (advice.IsUnlocked == true)
                            return $"{prefix}: {frontier.Member.Name} — Run: {dutyCatalog.GetDisplayLabel(advice.DutyName)}";

                        var via = string.IsNullOrWhiteSpace(advice.QuestName) ? string.Empty : $" via {advice.QuestName}";
                        var who = string.IsNullOrWhiteSpace(advice.Contact)
                            ? string.Empty
                            : $" — Talk to {advice.Contact}{(string.IsNullOrWhiteSpace(advice.Location) ? string.Empty : $" — {advice.Location}")}";
                        return $"{prefix}: {frontier.Member.Name} — Unlock: {dutyCatalog.GetDisplayLabel(advice.DutyName)}{via}{who}";
                    }
                }

                var lockedReward = members.FirstOrDefault(x =>
                    IsCompletionReward(set, x) &&
                    !ownership.GetItemStatus(x.ItemId).IsOwned &&
                    prerequisiteOwned < prerequisiteMembers.Length);
                if (lockedReward is not null)
                    return $"{ownedCount} / {members.Length} owned — {lockedReward.Name} unlocks after the prerequisite collection ({prerequisiteOwned} / {prerequisiteMembers.Length}).";

                var resolved = members.Count(resolver.HasDirectSource);
                return $"{ownedCount} / {members.Length} owned; {resolved} / {members.Length} have resolved acquisition sources.";
            }
            case "Duty":
            {
                var duty = dutyCatalog.Get(pin.NumericId);
                if (duty is null)
                    return "Duty is no longer present in the current game data.";
                var advice = dutyUnlocks.GetAdvice(duty.Name);
                if (advice?.IsUnlocked == true || dutyUnlocks.IsDutyUnlocked(duty.Name) == true)
                    return "Unlocked — duty is available on this character.";
                if (advice is null)
                    return "Locked — unlock quest could not be resolved.";
                var quest = string.IsNullOrWhiteSpace(advice.QuestName) ? "unlock quest unresolved" : advice.QuestName;
                var who = string.IsNullOrWhiteSpace(advice.Contact)
                    ? string.Empty
                    : $" — Go to {advice.Contact}{(string.IsNullOrWhiteSpace(advice.Location) ? string.Empty : $" — {advice.Location}")}";
                return $"Locked — {quest}{who}";
            }
            case "JobUnlock":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null)
                    return "Class/job is no longer present in the current game data.";
                // Once the job itself is unlocked, stale or incomplete parent
                // evidence must not keep guiding the player to unlock it.
                if (ownership.IsJobUnlocked(job.ClassJobId))
                    return $"Unlocked — {job.Name}.";
                if (GetMissingBaseClassUnlock(job) is { } missingBase)
                {
                    var baseQuest = missingBase.UnlockQuest;
                    if (baseQuest is null)
                        return $"Requires unlocking {missingBase.Name}. Open this pin for class guidance.";
                    return $"Requires {missingBase.Name} unlock — Next: {baseQuest.Name}" +
                        (string.IsNullOrWhiteSpace(baseQuest.QuestGiver) ? string.Empty : $" — Go to {baseQuest.QuestGiver}" +
                        (string.IsNullOrWhiteSpace(baseQuest.Location) ? string.Empty : $" — {baseQuest.Location}"));
                }
                if (GetMissingBaseClassProgression(job) is { } missingProgression)
                {
                    var nextParentStep = GetNextConfirmedProgressionQuest(missingProgression);
                    return nextParentStep is null ? $"Requires {missingProgression.Name} progression" :
                        $"Requires {missingProgression.Name} progression — Next: {nextParentStep.Name}";
                }
                if (job.UnlockQuest is null)
                    return "No explicit unlock quest relation found.";
                var state = GetQuestStateByRowId(job.UnlockQuest.QuestId);
                if (ownership.IsJobUnlocked(job.ClassJobId))
                    return $"Unlocked — {job.UnlockQuest.Name}";
                if (state == QuestProgressState.Completed)
                    return $"Complete — {job.UnlockQuest.Name}";
                var who = string.IsNullOrWhiteSpace(job.UnlockQuest.QuestGiver)
                    ? string.Empty
                    : $" — {(state == QuestProgressState.InProgress ? "started by" : "go to")} {job.UnlockQuest.QuestGiver}{(string.IsNullOrWhiteSpace(job.UnlockQuest.Location) ? string.Empty : $" — {job.UnlockQuest.Location}")}";
                return $"{(state == QuestProgressState.InProgress ? "Current" : "Next")}: {job.UnlockQuest.Name}{who}";
            }
            case "JobProgression":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null)
                    return "Class/job is no longer present in the current game data.";

                if (!ownership.IsJobUnlocked(job.ClassJobId))
                    return $"{job.Name} is locked — unlock it before tracking its progression.";
                var completed = CountConfirmedProgressionQuests(job);
                var applicable = GetApplicableProgression(job);
                var next = GetNextConfirmedProgressionQuest(job);
                if (next is null)
                    return applicable.Count == 0 ? "Progression quest data unavailable." :
                        AreAllJobQuestsConfirmedComplete(job)
                            ? $"Formal progression complete ({completed} / {applicable.Count})."
                            : $"All currently resolved quests complete ({completed} / {applicable.Count}); remaining coverage unverified.";
                var current = GetQuestStateByRowId(next.QuestId) == QuestProgressState.InProgress;
                var who = current || string.IsNullOrWhiteSpace(next.QuestGiver) ? string.Empty :
                    $"{next.QuestGiver}{(string.IsNullOrWhiteSpace(next.Location) ? string.Empty : $" — {next.Location}")}";
                var label = current ? "Current" : "Next";
                return string.IsNullOrWhiteSpace(who)
                    ? $"{completed} / {applicable.Count} complete — {label}: {next.Name}"
                    : $"{completed} / {applicable.Count} complete — {label}: {next.Name} — Go to {who}";
            }
            default:
                return "Unknown pinned target type.";
        }
    }

    private void OpenPinnedTarget(PinnedTargetConfig pin)
    {
        switch (pin.Kind)
        {
            case "Item":
            {
                var item = catalog.GetById(pin.NumericId);
                if (item is null)
                    return;
                SelectItemTarget(item);
                targetSearch = string.Empty;
                break;
            }
            case "Emote":
            {
                if (pin.NumericId > ushort.MaxValue || emoteCatalog.Get((ushort)pin.NumericId) is not { } emote)
                    return;
                SelectEmoteTarget(emote);
                targetSearch = string.Empty;
                break;
            }
            case "Title":
            {
                if (pin.NumericId > ushort.MaxValue || titleCatalog.Get((ushort)pin.NumericId) is not { } title)
                    return;
                SelectTitleTarget(title);
                targetSearch = string.Empty;
                break;
            }
            case "Set":
            {
                if (setCatalog.Get(pin.StringId) is not { } set)
                    return;
                SelectSetTarget(set, pin.Variant);
                targetSearch = string.Empty;
                break;
            }
            case "Duty":
            {
                var duty = dutyCatalog.Get(pin.NumericId);
                if (duty is null)
                    return;
                SelectDutyTarget(duty);
                targetSearch = string.Empty;
                break;
            }
            case "JobUnlock":
            case "JobProgression":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null)
                    return;
                // Open the original pinned job so the prerequisite is shown
                // there alongside Back navigation to the selected class.
                SelectJobTarget(job);
                targetSearch = string.Empty;
                break;
            }
        }
    }

    private void SwapPins(int left, int right)
    {
        (config.PinnedTargets[left], config.PinnedTargets[right]) =
            (config.PinnedTargets[right], config.PinnedTargets[left]);
        SaveConfig();
    }

    private void DrawPinToggle(
        string kind,
        uint numericId,
        string stringId,
        string pinLabel = "Pin Target",
        string unpinLabel = "Unpin",
        string variant = "",
        Vector2? buttonSize = null,
        string prerequisiteForKind = "",
        uint prerequisiteForNumericId = 0,
        string prerequisiteForStringId = "",
        string prerequisiteForVariant = "")
    {
        var index = FindPinIndex(kind, numericId, stringId, variant);
        var pinned = index >= 0;
        // A target explicitly pinned by the user is not a disposable dependency.
        // Never let a contextual Pin Prerequisite control unpin or relabel it.
        var contextual = !string.IsNullOrWhiteSpace(prerequisiteForKind);
        var independentlyPinned = contextual && pinned &&
            string.IsNullOrWhiteSpace(config.PinnedTargets[index].PrerequisiteForKind);
        if (independentlyPinned)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Already in Watchlist", buttonSize ?? Vector2.Zero);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("This target was pinned independently and will not be automatically cleaned up as a prerequisite.");
            return;
        }

        var atCapacity = !pinned && !PinnedTargetPolicy.CanAdd(config.PinnedTargets.Count);
        if (atCapacity)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(pinned ? unpinLabel : pinLabel, buttonSize ?? Vector2.Zero);
        if (atCapacity)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip($"Pin limit reached ({PinnedTargetPolicy.MaximumPins}). Remove an existing pin to add another.");
        }
        if (!atCapacity && ImGui.IsItemHovered())
            ImGui.SetTooltip(pinned
                ? "Remove this pinned target. Sticky behavior is configured in Watchlist."
                : "Add to Watchlist. Pins can be made Sticky there to keep them after completion.");
        if (!clicked || atCapacity)
            return;

        if (pinned)
        {
            config.PinnedTargets.RemoveAt(index);
        }
        else
        {
            config.PinnedTargets.Add(new PinnedTargetConfig
            {
                Kind = kind,
                NumericId = numericId,
                StringId = stringId,
                Variant = variant,
                PrerequisiteForKind = prerequisiteForKind,
                PrerequisiteForNumericId = prerequisiteForNumericId,
                PrerequisiteForStringId = prerequisiteForStringId,
                PrerequisiteForVariant = prerequisiteForVariant,
            });
        }

        SaveConfig();
    }

    private int FindPinIndex(string kind, uint numericId, string stringId, string variant = "")
    {
        for (var i = 0; i < config.PinnedTargets.Count; i++)
        {
            var pin = config.PinnedTargets[i];
            if (!pin.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                continue;
            if (pin.NumericId != numericId)
                continue;
            if (!pin.StringId.Equals(stringId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!(pin.Variant ?? string.Empty).Equals(variant ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                continue;
            return i;
        }

        return -1;
    }

    private void SelectFirstProgressionJob()
    {
        config.SelectedClassJobId =
            jobCatalog.GetByCategory(config.SelectedProgressionCategory)
                .FirstOrDefault()
                ?.ClassJobId ?? 0;
    }

}
