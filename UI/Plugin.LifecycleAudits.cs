using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace CustomQuestGuide;

/// <summary>Explicit, read-only diagnostics. Never run audit loops during Draw or on game events.</summary>
public sealed partial class Plugin
{
    private int lifecycleAuditItem;

    private void DrawLifecycleAuditTools()
    {
        if (!ImGui.CollapsingHeader("1.4 Lifecycle audits (manual / read-only)")) return;
        ImGui.TextDisabled("Reports are saved to the plugin config directory. No unlocks, pin changes, or network calls.");
        string[] reports =
        {
            "Class & job progression", "Arcanist branching", "Event listener coverage",
            "Target lifecycle & Undo", "Performance & resource structure"
        };
        ImGui.SetNextItemWidth(MathF.Min(320f, ImGui.GetContentRegionAvail().X * 0.65f));
        ImGui.Combo("Report##cqg-lifecycle-audit", ref lifecycleAuditItem, reports, reports.Length);
        if (ImGui.Button("Export selected report##cqg-lifecycle-audit"))
        {
            switch (lifecycleAuditItem)
            {
                case 0: ExportJobProgressionAuditCsv(); break;
                case 1: ExportArcanistBranchAudit(); break;
                case 2: ExportEventCoverageAudit(); break;
                case 3: ExportPinLifecycleAudit(); break;
                case 4: ExportMonitoringStructureAudit(); break;
            }
        }
        ImGui.TextDisabled("Diagnostic snapshots only; actual event delivery and FPS require in-game tests.");
    }

    private void WriteLifecycleAudit(string name, IEnumerable<string> lines)
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"CustomQuestGuide-{name}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            exportStatus = $"Exported {name}:\n{path}";
            Log.Information($"Exported CQG {name} to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"{name} export failed: {ex.Message}";
            Log.Error(ex, $"CQG {name} export failed");
        }
    }

    private void ExportArcanistBranchAudit()
    {
        var arcanist = jobCatalog.Jobs.FirstOrDefault(j => j.Abbreviation.Equals("ACN", StringComparison.OrdinalIgnoreCase));
        var lines = new List<string> { "ClassJobId,Name,Abbreviation,ParentId,UnlockQuestId,ProgressionQuestCount,LiveUnlock,BranchStatus,Note" };
        if (arcanist is null)
        {
            lines.Add("0,Arcanist,ACN,0,0,0,Unknown,Arcanist missing from catalog");
        }
        else
        {
            var family = jobCatalog.Jobs.Where(j => j.ClassJobId == arcanist.ClassJobId || j.ParentClassJobId == arcanist.ClassJobId);
            foreach (var job in family.OrderBy(j => j.ClassJobId))
            {
                lines.Add(string.Join(",", Csv(job.ClassJobId.ToString()), Csv(job.Name), Csv(job.Abbreviation),
                    Csv(job.ParentClassJobId.ToString()), Csv(job.ClassJobUnlockQuestId.ToString()),
                    Csv(GetApplicableProgression(job).Count.ToString()), Csv(ClientState.IsLoggedIn ? ownership.IsJobCurrentlyUnlocked(job.ClassJobId).ToString() : "Unavailable"),
                    Csv(job.ClassJobId == arcanist.ClassJobId ? "Manual branch selection required" : "Independent soul-crystal verification"),
                    Csv("Each derived job must have independent unlock verification; simulation and game events not tested by this export")));
            }
        }
        lines.Add("BranchingPolicy,Arcanist,ACN,26,0,0,Unavailable,No automatic branch selection,Choose Summoner or Scholar independently");
        WriteLifecycleAudit("ArcanistBranchAudit", lines);
    }

    private void ExportEventCoverageAudit()
    {
        // Architectural coverage inventory, not an assertion that every event fires on every game build.
        var lines = new List<string> { "TargetCategory,TriggerOrDependency,Validation,KnownLimitation,RuntimeDeliveryVerified" };
        var entries = new (string Category, string Trigger, string Validation, string Limitation)[]
        {
            ("Items", "InventoryChanged", "Targeted item quantity verification", "Container availability and inventory initialization"),
            ("Quantity goals", "InventoryChanged", "Recount changed tracked IDs", "Retainer containers may be unavailable"),
            ("Classes", "Unlock and quest toast / ClassJobChanged", "Live class unlock state", "Confirm event delivery in game"),
            ("Advanced jobs", "Soul crystal inventory and unlock / quest events", "Actual job soul crystal ownership", "Confirm event delivery and crystal inventory semantics"),
            ("Job progression", "ClassJobChanged / LevelChanged / quest events", "Formal quest completion state", "Quest catalog completeness must be audited"),
            ("Duties", "TerritoryChanged / relevant quest events", "Authoritative duty unlock state", "May not trigger immediately on unlock"),
            ("Mounts and minions", "Unlock/quest events", "Permanent collection ownership", "Per-category trigger coverage not fully verified"),
            ("Emotes and titles", "Unlock/quest events", "Permanent unlock state", "Per-category trigger coverage not fully verified"),
            ("Sets", "Member completion dependencies", "All member states", "Dependency-index coverage needs verification"),
            ("Relic progression", "Item/quest events", "Current stage", "Stage-specific event coverage unverified"),
            ("Prerequisites", "Quest/duty/item change events", "Affected prerequisite", "Dependency-index coverage needs verification")
        };
        foreach (var e in entries)
            lines.Add(string.Join(",", Csv(e.Category), Csv(e.Trigger), Csv(e.Validation), Csv(e.Limitation), Csv("No - static checklist only")));
        WriteLifecycleAudit("EventCoverageAudit", lines);
    }

    private void ExportPinLifecycleAudit()
    {
        var lines = new List<string> { "Priority,Kind,NumericId,StringId,Variant,Sticky,Quantity,DuplicateKey,SuppressedAfterUndo,UndoWindowActive" };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < config.PinnedTargets.Count; i++)
        {
            var pin = config.PinnedTargets[i];
            var key = PinCompletionKey(pin);
            var duplicate = !keys.Add(key);
            lines.Add(string.Join(",", Csv((i + 1).ToString()), Csv(pin.Kind), Csv(pin.NumericId.ToString()),
                Csv(pin.StringId), Csv(pin.Variant), Csv(pin.IsSticky.ToString()), Csv(pin.DesiredQuantity.ToString()),
                Csv(duplicate.ToString()), Csv(undoRestoredCompletionKeys.Contains(key).ToString()),
                Csv((DateTime.UtcNow < lastAutoRemovalUndoUntilUtc).ToString())));
        }
        WriteLifecycleAudit("PinLifecycleAudit", lines);
    }

    private void ExportMonitoringStructureAudit()
    {
        // Counts, not instrumentation: no unsupported claims about measured frame timings.
        var lines = new List<string> { "Metric,Value,Interpretation" };
        void Add(string metric, string value, string note) => lines.Add(string.Join(",", Csv(metric), Csv(value), Csv(note)));
        Add("Pin limit", PinnedTargetPolicy.MaximumPins.ToString(), "Configured maximum");
        Add("Current pins", config.PinnedTargets.Count.ToString(), "Saved wishlist count");
        Add("Tracked item IDs", config.PinnedTargets.Where(p => p.Kind == "Item").Select(p => p.NumericId).Distinct().Count().ToString(), "Potential item monitoring index size");
        Add("Pending inventory IDs", pendingCompletionItems.Count.ToString(), "Snapshot of queued IDs");
        Add("Pending job/other events", pendingEventCompletionPins.Count.ToString(), "Snapshot of queued pins");
        Add("Queued reconciliation", completionReconciliationQueue.Count.ToString(), "Snapshot of pending checks");
        Add("Index dirty", pinnedCompletionIndexDirty.ToString(), "Index is rebuilt on demand");
        Add("Full ownership polling", ownershipPollingActive.ToString(), "Existing explicit one-shot ownership scan state");
        Add("Measured game frame duration", "Not measured", "Use an in-game profiler under combat and inventory load");
        Add("Allocated bytes per event", "Not measured", "Requires runtime allocation instrumentation");
        Add("Event subscriber disposal", "Not statically verified", "Verify service unsubscribe paths separately");
        WriteLifecycleAudit("MonitoringStructureAudit", lines);
    }
}
