using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Lumina.Excel;

namespace CustomQuestGuide;

/// <summary>
/// Pin lifecycle integration. Inventory events flag only indexed IDs; verification
/// happens once after the inventory settles, never inside event callbacks.
/// </summary>
public sealed partial class Plugin
{
    private readonly Dictionary<uint, List<PinnedTargetConfig>> completionItemIndex = new();
    // Member-to-set index: inventory changes only recheck affected set pins.
    private readonly Dictionary<uint, List<PinnedTargetConfig>> completionSetMemberIndex = new();
    // Parent item events can invalidate linked prerequisites without a Watchlist scan.
    private readonly Dictionary<uint, List<PinnedTargetConfig>> linkedParentItemIndex = new();
    private readonly HashSet<uint> pendingCompletionItems = new();
    // Explicit quantity edits should be evaluated even if possession did not increase.
    private readonly HashSet<uint> pendingExplicitQuantityRechecks = new();
    private readonly Dictionary<uint, int> pinnedItemQuantityCounts = new();
    private readonly HashSet<string> notifiedStickyCompletions = new(StringComparer.Ordinal);
    private bool pinnedCompletionIndexDirty = true;
    private readonly Queue<PinnedTargetConfig> completionReconciliationQueue = new();
    private bool pinnedCompletionNeedsLoginReconcile;
    // Only event-triggered non-item checks are queued. No periodic polling.
    private readonly HashSet<PinnedTargetConfig> pendingEventCompletionPins = new();
    private readonly List<(PinnedTargetConfig Pin, int Index)> lastAutoRemovedPins = new();
    // The original progression position is preserved when de-duplicating an
    // already-pinned progression goal; -1 means the progression pin was new.
    private readonly List<(PinnedTargetConfig Unlock, PinnedTargetConfig Progression, int UnlockIndex, int PreviousProgressionIndex)> lastJobHandoffs = new();
    private DateTime lastAutoRemovalUndoUntilUtc = DateTime.MinValue;
    // Undo must not instantly remove an already-complete target again.
    // Suppress future automatic removal until the item is observed below its goal.
    // Non-item targets remain suppressed for this plugin session, since we have
    // no reliable event proving their completion state has reset.
    private readonly HashSet<string> undoRestoredCompletionKeys = new(StringComparer.Ordinal);

    // Choose the user-facing completion verb from the actual target category.
    private static string PinCompletionVerb(PinnedTargetConfig pin) => pin.Kind switch
    {
        "Item" => "obtained",
        "Emote" or "Title" or "Duty" or "JobUnlock" => "unlocked",
        "Set" or "JobProgression" => "completed",
        _ => "completed",
    };

    private static string PinCompletionKey(PinnedTargetConfig pin) =>
        $"{pin.Kind}\u001f{pin.NumericId}\u001f{pin.StringId}\u001f{pin.Variant}";

    private void RebuildCompletionIndexIfNeeded()
    {
        if (!pinnedCompletionIndexDirty) return;
        completionItemIndex.Clear();
        completionSetMemberIndex.Clear();
        linkedParentItemIndex.Clear();
        foreach (var pin in config.PinnedTargets)
        {
            if (pin.PrerequisiteForKind == "Item" && pin.PrerequisiteForNumericId != 0)
            {
                if (!linkedParentItemIndex.TryGetValue(pin.PrerequisiteForNumericId, out var dependents))
                    linkedParentItemIndex.Add(pin.PrerequisiteForNumericId, dependents = new List<PinnedTargetConfig>(1));
                dependents.Add(pin);
            }
            if (pin.Kind == "Set" && setCatalog.Get(pin.StringId) is { } set)
            {
                foreach (var memberId in set.ItemIds)
                {
                    // A relic set with a selected weapon must only respond to its
                    // relevant stages, just as the completion predicate does.
                    if (!string.IsNullOrWhiteSpace(pin.Variant))
                    {
                        var member = catalog.GetById(memberId);
                        if (member is null || !member.EquipJobs.Contains(pin.Variant))
                            continue;
                    }
                    if (!completionSetMemberIndex.TryGetValue(memberId, out var memberPins))
                        completionSetMemberIndex.Add(memberId, memberPins = new List<PinnedTargetConfig>(1));
                    memberPins.Add(pin);
                }
                continue;
            }
            if (!pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase) || pin.NumericId == 0)
                continue;
            if (!completionItemIndex.TryGetValue(pin.NumericId, out var matches))
                completionItemIndex.Add(pin.NumericId, matches = new List<PinnedTargetConfig>(1));
            matches.Add(pin);
        }
        pinnedCompletionIndexDirty = false;
        foreach (var id in pinnedItemQuantityCounts.Keys.Where(id => !completionItemIndex.ContainsKey(id)).ToArray())
            pinnedItemQuantityCounts.Remove(id);
    }

    private void OnPinnedInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (!ClientState.IsLoggedIn || config.PinnedTargets.Count == 0) return;
        RebuildCompletionIndexIfNeeded();
        foreach (var change in events)
        {
            var itemId = change.Item.BaseItemId;
            if (itemId == 0) continue;
            if (completionItemIndex.ContainsKey(itemId))
                pendingCompletionItems.Add(itemId);
            if (linkedParentItemIndex.TryGetValue(itemId, out var linkedDependents))
                foreach (var dependent in linkedDependents)
                    pendingEventCompletionPins.Add(dependent);
            if (completionSetMemberIndex.TryGetValue(itemId, out var setPins))
                foreach (var setPin in setPins)
                    pendingEventCompletionPins.Add(setPin);
            if (ownership.TryGetJobForSoulCrystal(itemId, out var jobId))
                QueueJobCompletionCheck(jobId);
        }
    }

    private void ProcessPendingPinnedCompletions()
    {
        if (DateTime.UtcNow >= lastAutoRemovalUndoUntilUtc)
        {
            lastAutoRemovedPins.Clear();
            lastJobHandoffs.Clear();
        }
        if (config.PinnedTargets.Count == 0)
        {
            pendingCompletionItems.Clear();
            pendingExplicitQuantityRechecks.Clear();
            pinnedCompletionNeedsLoginReconcile = false;
            return;
        }
        RebuildCompletionIndexIfNeeded();
        DrainCompletionReconciliationQueue();
        if (pinnedCompletionNeedsLoginReconcile)
        {
            pinnedCompletionNeedsLoginReconcile = false;
            var ids = new HashSet<uint>(completionItemIndex.Keys);
            // At most one inventory sweep even if the player has 500 pins.
            var loginCounts = ownership.CountPhysicallyPresentTrackedItems(ids);
            foreach (var id in ids)
                pinnedItemQuantityCounts[id] = loginCounts.GetValueOrDefault(id);
            CompleteVerifiedPhysicalPins(loginCounts);
        }
        if (pendingCompletionItems.Count == 0) return;
        var affected = pendingCompletionItems.ToArray();
        pendingCompletionItems.Clear();
        // Sweep the loaded containers once for all changed tracked IDs instead
        // of performing a separate full-container iteration per material.
        var trackedAffected = new HashSet<uint>(affected.Where(completionItemIndex.ContainsKey));
        if (trackedAffected.Count == 0) return;
        var counts = ownership.CountPhysicallyPresentTrackedItems(trackedAffected);
        var eligible = new HashSet<uint>();
        foreach (var id in trackedAffected)
        {
            var count = counts.GetValueOrDefault(id);
            var hadPrevious = pinnedItemQuantityCounts.TryGetValue(id, out var previous);
            // New acquisitions must increase the verified held count. Moving
            // stacks between bags must not create a new completion event.
            if (!hadPrevious || count > previous || pendingExplicitQuantityRechecks.Contains(id))
                eligible.Add(id);
            pinnedItemQuantityCounts[id] = count;
            counts[id] = count; // Include zero: consumption can re-arm Undo.
            pendingExplicitQuantityRechecks.Remove(id);
        }
        CompleteVerifiedPhysicalPins(counts, eligible);
    }

    private void CompleteVerifiedPhysicalPins(Dictionary<uint, int> counts, HashSet<uint>? completionEligibleIds = null)
    {
        if (counts.Count == 0) return;
        // Re-arm an undone item after it falls below the desired quantity.
        // Only look at pins associated with the changed item ID.
        foreach (var entry in counts)
        {
            if (entry.Value > 0)
                ownership.NoteVerifiedPhysicalPresence(entry.Key);
            if (!completionItemIndex.TryGetValue(entry.Key, out var associatedPins))
                continue;
            foreach (var pin in associatedPins)
            {
                if (entry.Value < Math.Max(1, pin.DesiredQuantity))
                    undoRestoredCompletionKeys.Remove(PinCompletionKey(pin));
            }
        }
        // Only examine pins indexed under changed item IDs. A gathering event
        // must not iterate the entire watchlist, regardless of its size.
        var affectedPins = new HashSet<PinnedTargetConfig>();
        foreach (var id in counts.Keys)
            if ((completionEligibleIds == null || completionEligibleIds.Contains(id)) &&
                completionItemIndex.TryGetValue(id, out var matches))
                foreach (var pin in matches)
                    affectedPins.Add(pin);
        CompleteVerifiedPins(pin => pin.Kind == "Item" && counts.TryGetValue(pin.NumericId, out var held)
            && held > 0 && held >= Math.Max(1, pin.DesiredQuantity), affectedPins);
    }

    private void QueuePinnedQuantityRecheck(uint itemId)
    {
        if (itemId == 0) return;
        pendingCompletionItems.Add(itemId);
        pendingExplicitQuantityRechecks.Add(itemId);
    }

    private void QueueJobCompletionCheck(uint classJobId)
    {
        if (!ClientState.IsLoggedIn || classJobId == 0) return;
        QueueLinkedPrerequisitesForParent("JobUnlock", classJobId);
        QueueLinkedPrerequisitesForParent("JobProgression", classJobId);
        foreach (var pin in config.PinnedTargets)
            if ((pin.PrerequisiteForNumericId == classJobId ||
                 pin.NumericId == classJobId ||
                 (pin.Kind == "JobProgression" &&
                  jobCatalog.Get(classJobId)?.ParentClassJobId == pin.NumericId)) &&
                (pin.Kind == "JobUnlock" || pin.Kind == "JobProgression"))
                pendingEventCompletionPins.Add(pin);
    }

    // Unlocking a new base class does not necessarily change the active job or
    // fire LevelChanged. These low-frequency notifications cover that gap.
    // We queue only class/job pins and verify authoritative live levels later.
    private void QueuePinnedUnlockVerification()
    {
        if (!ClientState.IsLoggedIn) return;
        foreach (var pin in config.PinnedTargets)
            if (pin.Kind == "JobUnlock" || pin.Kind == "JobProgression")
                pendingEventCompletionPins.Add(pin);
    }

    private void OnPinnedUnlockObserved(RowRef _) => QueuePinnedUnlockVerification();

    private void OnPinnedQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled)
        => QueuePinnedUnlockVerification();

    private void OnPinnedClassJobChanged(uint classJobId) => QueueJobCompletionCheck(classJobId);

    private void OnPinnedLevelChanged(uint classJobId, uint level) => QueueJobCompletionCheck(classJobId);

    private void OnPinnedTerritoryChanged(uint territoryId)
    {
        if (!ClientState.IsLoggedIn) return;
        // Territory changes are infrequent. Unlock/duty checks use the existing
        // authoritative completion predicate; entering a duty is not proof of unlock.
        foreach (var pin in config.PinnedTargets)
            if (pin.Kind == "Duty" || !string.IsNullOrWhiteSpace(pin.PrerequisiteForKind))
                pendingEventCompletionPins.Add(pin);
    }

    private void QueueCompletedPinReconciliation()
    {
        // Spread expensive set/quest completion checks across later frames.
        // This queue is populated only when a /cqg scan finishes.
        completionReconciliationQueue.Clear();
        foreach (var pin in config.PinnedTargets)
            completionReconciliationQueue.Enqueue(pin);
    }

    private void DrainCompletionReconciliationQueue()
    {
        const int maxChecksPerFrame = 3;
        var checkedCount = 0;
        while (checkedCount < maxChecksPerFrame &&
               (pendingEventCompletionPins.Count > 0 || completionReconciliationQueue.Count > 0))
        {
            PinnedTargetConfig pin;
            if (pendingEventCompletionPins.Count > 0)
            {
                pin = pendingEventCompletionPins.First();
                pendingEventCompletionPins.Remove(pin);
            }
            else
                pin = completionReconciliationQueue.Dequeue();
            checkedCount++;
            if (!config.PinnedTargets.Contains(pin)) continue;
            CompleteVerifiedPins(candidate =>
                (candidate.Kind != "Item" || candidate.DesiredQuantity <= 1) &&
                (candidate.Kind == "JobUnlock"
                    ? (jobCatalog.Get(candidate.NumericId) is { } unlockJob &&
                       unlockJob.ParentClassJobId != 0 && unlockJob.ParentClassJobId != unlockJob.ClassJobId
                        ? ownership.IsJobCurrentlyUnlocked(unlockJob.ClassJobId)
                        : ownership.IsJobCurrentlyUnlocked(candidate.NumericId))
                    : IsPinComplete(candidate)), new[] { pin });
        }
    }

    // The parent goal is authoritative: a linked prerequisite becomes obsolete when
    // its parent's goal is confirmed, regardless of whether the dependency was itself
    // consumed or still owned. Never infer relationships for legacy/independent pins.
    private bool IsLinkedPrerequisiteObsolete(PinnedTargetConfig pin)
    {
        if (string.IsNullOrWhiteSpace(pin.PrerequisiteForKind)) return false;
        if (pin.PrerequisiteForNumericId == 0 &&
            string.IsNullOrWhiteSpace(pin.PrerequisiteForStringId)) return false;

        var parent = new PinnedTargetConfig
        {
            Kind = pin.PrerequisiteForKind,
            NumericId = pin.PrerequisiteForNumericId,
            StringId = pin.PrerequisiteForStringId,
            Variant = pin.PrerequisiteForVariant,
        };
        // Only evaluate supported, evidence-backed goals. Unknown kinds are never
        // reasons to remove a pin.
        return parent.Kind switch
        {
            "JobUnlock" => ownership.IsJobCurrentlyUnlocked(parent.NumericId),
            "JobProgression" or "Item" or "Duty" or "Set" or "Emote" or "Title" => IsPinComplete(parent),
            _ => false,
        };
    }

    private string DescribeLinkedPrerequisiteParent(PinnedTargetConfig pin)
    {
        var parent = new PinnedTargetConfig
        {
            Kind = pin.PrerequisiteForKind,
            NumericId = pin.PrerequisiteForNumericId,
            StringId = pin.PrerequisiteForStringId,
            Variant = pin.PrerequisiteForVariant,
        };
        return DescribePinTitle(parent);
    }

    private void QueueLinkedPrerequisitesForParent(string kind, uint numericId, string stringId = "")
    {
        foreach (var pin in config.PinnedTargets)
            if (string.Equals(pin.PrerequisiteForKind, kind, StringComparison.Ordinal) &&
                pin.PrerequisiteForNumericId == numericId &&
                string.Equals(pin.PrerequisiteForStringId, stringId, StringComparison.Ordinal))
                pendingEventCompletionPins.Add(pin);
    }

    private void CompleteVerifiedPins(
        Func<PinnedTargetConfig, bool> isConfirmedComplete,
        IReadOnlyCollection<PinnedTargetConfig>? candidatePins = null)
    {
        List<(PinnedTargetConfig Pin, int Index)> removed = new();
        List<(string Title, string Verb)> completedNames = new();
        List<string> retainedMessages = new();
        List<(PinnedTargetConfig Unlock, PinnedTargetConfig Progression, int UnlockIndex, int PreviousProgressionIndex)> handoffs = new();

        // Event handlers pass a small candidate collection. Reconciliation
        // passes only its one queued pin. Both paths avoid processing all pins.
        var candidates = candidatePins ?? config.PinnedTargets.ToArray();
        foreach (var pin in candidates)
        {
            var obsoletePrerequisite = IsLinkedPrerequisiteObsolete(pin);
            if (!obsoletePrerequisite && !isConfirmedComplete(pin)) continue;
            if (undoRestoredCompletionKeys.Contains(PinCompletionKey(pin))) continue;
            var index = config.PinnedTargets.IndexOf(pin);
            if (index < 0) continue; // Pin was removed since event was queued.
            var title = DescribePinTitle(pin);
            // A branching class has no single correct next stage. Keep the
            // completed class pin until the player deliberately selects a job.
            if (pin.Kind == "JobProgression" && jobCatalog.Jobs.Count(job =>
                    job.ClassJobId != pin.NumericId && job.ParentClassJobId == pin.NumericId) > 1)
                continue;
            if (pin.IsSticky || !config.AutoUnpinCompletedTargets)
            {
                if (config.CompletionChatNotifications && notifiedStickyCompletions.Add(PinCompletionKey(pin)))
                    retainedMessages.Add($"[CQG] {title} {PinCompletionVerb(pin)}! Pin kept{(pin.IsSticky ? " (Sticky)" : " (auto-unpin disabled)" )}.");
                continue;
            }
            // A linked prerequisite is never promoted into an independent job
            // handoff. Its parent goal remains the authoritative Watchlist pin.
            if (!string.IsNullOrWhiteSpace(pin.PrerequisiteForKind))
            {
                removed.Add((pin, index));
                completedNames.Add((title, "prerequisite satisfied"));
                continue;
            }
            // A base class should lead into its associated job rather than
            // disappearing when its class quest chain is complete. If that job
            // is not unlocked, guide the unlock; otherwise guide progression.
            // Jobs without a derived class/job relationship use the ordinary
            // unlock -> progression handoff below.
            uint nextJobId = pin.NumericId;
            string? nextKind = null;
            if (pin.Kind == "JobProgression" && jobCatalog.Get(pin.NumericId) is { } baseClass)
            {
                var derivedJobs = jobCatalog.Jobs.Where(job =>
                    job.ClassJobId != baseClass.ClassJobId &&
                    job.ParentClassJobId == baseClass.ClassJobId).ToArray();
                // Arcanist branches to SMN and SCH; never silently choose the first.
                // Its class progression pin remains for manual branch selection.
                var derivedJob = derivedJobs.Length == 1 ? derivedJobs[0] : null;
                if (derivedJob is not null && AreAllJobQuestsConfirmedComplete(baseClass))
                {
                    nextJobId = derivedJob.ClassJobId;
                    nextKind = ownership.IsJobCurrentlyUnlocked(derivedJob.ClassJobId) ? "JobProgression" : "JobUnlock";
                }
            }
            else if (pin.Kind == "JobUnlock" && jobCatalog.Get(pin.NumericId) is not null)
            {
                nextKind = "JobProgression";
            }

            if (nextKind is not null)
            {
                // Reuse a matching pin. A handoff is a replacement, never an
                // additional watchlist slot (even at the 500-pin limit).
                var previous = config.PinnedTargets.FindIndex(x =>
                    x.Kind == nextKind && x.NumericId == nextJobId);
                var nextPin = previous >= 0
                    ? config.PinnedTargets[previous]
                    : new PinnedTargetConfig
                    {
                        Kind = nextKind, NumericId = nextJobId,
                        StringId = pin.StringId, Variant = pin.Variant,
                        IsSticky = pin.IsSticky, DesiredQuantity = pin.DesiredQuantity,
                    };
                handoffs.Add((pin, nextPin, index, previous));
                continue;
            }
            removed.Add((pin, index));
            completedNames.Add((title, PinCompletionVerb(pin)));
        }

        PrintCompletionBatch(retainedMessages);
        if (removed.Count == 0 && handoffs.Count == 0) return;

        // All indices above refer to the pre-transition list. Remove completed
        // unlocks and any pre-existing progression pins first, then reinsert the
        // progression pins at their unlock positions in priority order.
        if (handoffs.Count > 0)
        {
            var drop = new HashSet<PinnedTargetConfig>();
            foreach (var handoff in handoffs)
            {
                drop.Add(handoff.Unlock);
                if (handoff.PreviousProgressionIndex >= 0)
                    drop.Add(handoff.Progression);
            }
            foreach (var entry in removed)
                drop.Add(entry.Pin);
            var original = config.PinnedTargets.ToArray();
            var ordered = new List<PinnedTargetConfig>(original.Length);
            for (var i = 0; i < original.Length; i++)
            {
                foreach (var handoff in handoffs)
                    if (handoff.UnlockIndex == i)
                        ordered.Add(handoff.Progression);
                if (!drop.Contains(original[i]))
                    ordered.Add(original[i]);
            }
            config.PinnedTargets.Clear();
            config.PinnedTargets.AddRange(ordered);
        }
        else
        {
            // Remove in descending priority order so recorded original positions
            // stay valid for Undo, even for multiple completed pins in one batch.
            foreach (var entry in removed.OrderByDescending(x => x.Index))
            {
                config.PinnedTargets.RemoveAt(entry.Index);
                notifiedStickyCompletions.Remove(PinCompletionKey(entry.Pin));
            }
        }

        // Multiple acquisitions within 30 seconds remain a single Undo action.
        // Do not overwrite the previous event's removed-pin entries.
        if (DateTime.UtcNow >= lastAutoRemovalUndoUntilUtc)
        {
            lastAutoRemovedPins.Clear();
            lastJobHandoffs.Clear();
        }
        lastJobHandoffs.AddRange(handoffs);
        // Index positions are relative to the current list, whose earlier
        // auto-removals may have shifted entries. Translate them back to the
        // prior Undo snapshot's coordinate space before appending.
        var priorIndices = lastAutoRemovedPins.Select(x => x.Index).OrderBy(x => x).ToArray();
        foreach (var entry in removed)
        {
            var restoredIndex = entry.Index;
            foreach (var priorIndex in priorIndices)
                if (priorIndex <= restoredIndex)
                    restoredIndex++;
            lastAutoRemovedPins.Add((entry.Pin, restoredIndex));
        }
        lastAutoRemovalUndoUntilUtc = DateTime.UtcNow.AddSeconds(30);
        SaveConfig();
        if (!config.CompletionChatNotifications) return;
        foreach (var handoff in handoffs)
            ChatGui.Print($"[CQG] {DescribePinTitle(handoff.Unlock)} {PinCompletionVerb(handoff.Unlock)}! Replaced with {DescribePinTitle(handoff.Progression)} at the same priority. Use \"/cqg undo\" within 30s.");
        var removedMessages = completedNames
            .Select(entry => $"[CQG] {entry.Title} {entry.Verb}! Removed from watchlist. Use \"/cqg undo\" within 30s (or Undo in Watchlist).")
            .ToList();
        PrintCompletionBatch(removedMessages);
    }

    private void PrintCompletionBatch(IReadOnlyList<string> messages)
    {
        if (!config.CompletionChatNotifications || messages.Count == 0) return;
        const int maxIndividualMessages = 3;
        for (var i = 0; i < Math.Min(messages.Count, maxIndividualMessages); i++)
            ChatGui.Print(messages[i]);
        if (messages.Count > maxIndividualMessages)
            ChatGui.Print($"[CQG] {messages.Count - maxIndividualMessages} more targets completed. Open Watchlist to review.");
    }

    /// <summary>
    /// Restore the most recently auto-removed batch, if its 30-second grace period
    /// is still active. Shared by the Watchlist button and /cqg undo.
    /// </summary>
    private bool TryUndoLastAutomaticRemoval(out int restoredCount)
    {
        restoredCount = 0;
        if (lastAutoRemovedPins.Count == 0 && lastJobHandoffs.Count == 0) return false;
        if (DateTime.UtcNow >= lastAutoRemovalUndoUntilUtc)
        {
            lastAutoRemovedPins.Clear();
            lastJobHandoffs.Clear();
            return false;
        }

        // Reverse handoffs in reverse order. A pre-existing progression pin
        // returns to its former priority; newly-created progressions go away.
        foreach (var handoff in lastJobHandoffs.AsEnumerable().Reverse())
        {
            config.PinnedTargets.Remove(handoff.Progression);
            if (handoff.PreviousProgressionIndex >= 0)
                config.PinnedTargets.Insert(Math.Min(handoff.PreviousProgressionIndex, config.PinnedTargets.Count), handoff.Progression);
            if (!config.PinnedTargets.Contains(handoff.Unlock))
            {
                config.PinnedTargets.Insert(Math.Min(handoff.UnlockIndex, config.PinnedTargets.Count), handoff.Unlock);
                undoRestoredCompletionKeys.Add(PinCompletionKey(handoff.Unlock));
                restoredCount++;
            }
        }
        lastJobHandoffs.Clear();

        foreach (var entry in lastAutoRemovedPins.OrderBy(x => x.Index))
        {
            if (config.PinnedTargets.Count >= PinnedTargetPolicy.MaximumPins) break;
            if (config.PinnedTargets.Any(x => PinCompletionKey(x) == PinCompletionKey(entry.Pin))) continue;
            config.PinnedTargets.Insert(Math.Min(entry.Index, config.PinnedTargets.Count), entry.Pin);
            undoRestoredCompletionKeys.Add(PinCompletionKey(entry.Pin));
            restoredCount++;
        }
        lastAutoRemovedPins.Clear();
        lastAutoRemovalUndoUntilUtc = DateTime.MinValue;
        if (restoredCount > 0)
            SaveConfig();
        return restoredCount > 0;
    }

    private void DrawPinnedCompletionUndo()
    {
        if ((lastAutoRemovedPins.Count == 0 && lastJobHandoffs.Count == 0) || DateTime.UtcNow >= lastAutoRemovalUndoUntilUtc)
            return;
        var seconds = (int)Math.Ceiling((lastAutoRemovalUndoUntilUtc - DateTime.UtcNow).TotalSeconds);
        ImGui.TextUnformatted($"{lastAutoRemovedPins.Count + lastJobHandoffs.Count} automatic change(s).");
        ImGui.SameLine();
        if (ImGui.SmallButton($"Undo ({seconds}s)"))
            TryUndoLastAutomaticRemoval(out _);
    }
}
