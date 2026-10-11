using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/cqg";
    private static readonly Vector4 OwnedGreen = new(0.35f, 1.00f, 0.45f, 1.00f);
    private static readonly Vector4 PartialBlue = new(0.35f, 0.70f, 1.00f, 1.00f);
    private static readonly Vector4 GlamourPink = new(1.00f, 0.62f, 0.78f, 1.00f);
    private static readonly Vector4 UnresolvedRed = new(1.00f, 0.30f, 0.30f, 1.00f);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerStateService { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private readonly Configuration config;
    private readonly SupplementalCsvIndex supplemental;
    private readonly CollectibleSourceIndex collectibleSources;
    private readonly GarlandSourceIndex garlandSources;
    private readonly TargetCatalog catalog;
    private readonly SetCatalog setCatalog;
    private readonly JobProgressionCatalog jobCatalog;
    private readonly QuestGuidanceIndex questGuidance;
    private readonly OwnershipIndex ownership;
    private readonly NpcLocationIndex npcLocations;
    private readonly DutyUnlockGuidanceIndex dutyUnlocks;
    private readonly SourceResolver resolver;
    private readonly SourceActionGuidanceResolver actionGuidance;

    private readonly Dictionary<string, uint> questRowIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<uint>> questRowIdsByName =
        new(StringComparer.OrdinalIgnoreCase);

    private bool windowOpen = false;
    private string targetSearch = string.Empty;
    private string exportStatus = string.Empty;
    private MainTab activeTab = MainTab.Items;
    private bool startupMessagePrinted;
    private DateTime nextManualSourceRefreshUtc = DateTime.MinValue;
    private string releaseDiagnosticsStatus = string.Empty;
    private bool configPersistenceDisabled;

    // Ownership polling is command-triggered and bounded. /cqg starts a 30-second
    // window, while each framework sample does only a small amount of work so the
    // command itself never performs a full ownership scan or causes a visible hitch.
    private static readonly TimeSpan OwnershipPollingDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OwnershipPollingInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OwnershipTransientRefreshInterval = TimeSpan.FromSeconds(2);
    private const int PermanentUnlockChecksPerPoll = 96;
    private DateTime ownershipPollingEndsUtc = DateTime.MinValue;
    private DateTime nextOwnershipPollUtc = DateTime.MaxValue;
    private DateTime nextOwnershipTransientRefreshUtc = DateTime.MaxValue;
    private uint[] ownershipItemIds = Array.Empty<uint>();
    private uint[] ownershipJobIds = Array.Empty<uint>();

    // Debug coverage used to scan the full ~44k target catalog every rendered
    // frame. Cache the snapshot briefly instead; async providers can still be
    // observed within a couple of seconds without turning Debug into a hot loop.
    private DateTime coverageMetricsExpiresUtc = DateTime.MinValue;
    private int cachedUnresolvedCount;
    private int cachedDirectCount;
    private int cachedSpecificityEnrichmentCandidates;
    private int cachedExactSpecificityBacklog;
    private readonly Dictionary<string, int> cachedResolutionTiers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> cachedSpecificity =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> cachedAvailability =
        new(StringComparer.OrdinalIgnoreCase);

    private string cachedSearchQuery = string.Empty;
    private IReadOnlyList<TargetEntry> cachedSearchResults = Array.Empty<TargetEntry>();

    private Configuration LoadConfiguration()
    {
        try
        {
            return PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        catch (Exception ex)
        {
            // A malformed/truncated config should never prevent CQG from loading.
            // Dalamud retains ownership of the underlying config file; CQG falls
            // back to defaults and avoids overwriting anything until a later save.
            Log.Error(ex, "CQG configuration could not be loaded; using safe defaults for this session.");
            configPersistenceDisabled = true;
            releaseDiagnosticsStatus = "Configuration load failed; safe defaults are in use and config writes are disabled for this session. See /xllog.";
            return new Configuration();
        }
    }

    public Plugin()
    {
        config = LoadConfiguration();
        if (ConfigurationMigrator.Migrate(config))
            SaveConfig();

        supplemental = new SupplementalCsvIndex(DataManager, Log);
        collectibleSources = new CollectibleSourceIndex(Log);
        garlandSources = new GarlandSourceIndex(
            PluginInterface.GetPluginConfigDirectory(),
            Log);
        catalog = new TargetCatalog(DataManager, supplemental);

        collectibleSources.BindTargets(catalog.Targets);
        _ = collectibleSources.RefreshAsync();

        setCatalog = new SetCatalog(catalog);
        jobCatalog = new JobProgressionCatalog(DataManager, Log);
        questGuidance = new QuestGuidanceIndex(DataManager, Log);
        ownership = new OwnershipIndex(DataManager, GameInventory, PlayerStateService, config, SaveConfig, Log);
        npcLocations = new NpcLocationIndex(DataManager, Log);
        dutyUnlocks = new DutyUnlockGuidanceIndex(DataManager, questGuidance, Log);
        resolver = new SourceResolver(
            DataManager,
            supplemental,
            collectibleSources,
            garlandSources,
            Log);
        actionGuidance = new SourceActionGuidanceResolver(
            questGuidance,
            dutyUnlocks,
            npcLocations,
            GetQuestStateByRowId,
            FindContainingSet);

        _ = garlandSources.RefreshAsync(
            catalog.Targets
                .Where(target => resolver.NeedsGarlandEnrichment(target))
                .Select(target => target.ItemId));

        BuildQuestCache();
        RepairSelection();

        // Materialize these once so each startup ownership sample does not rebuild
        // the full target/job ID sequences.
        ownershipItemIds = catalog.Targets
            .Select(target => target.ItemId)
            .Where(id => id != 0)
            .Distinct()
            .ToArray();
        ownershipJobIds = jobCatalog.Jobs
            .Select(job => job.ClassJobId)
            .Where(id => id != 0)
            .Distinct()
            .ToArray();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Custom Quest Guide. Optionally pass an item name or item ID to search/select it.",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainWindow;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;

        ClientState.Login += OnLogin;
        Framework.Update += OnFrameworkUpdate;
        if (ClientState.IsLoggedIn)
            PrintStartupMessage();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainWindow;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        ClientState.Login -= OnLogin;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        collectibleSources.Dispose();
        garlandSources.Dispose();
    }

    private void OnLogin()
    {
        PrintStartupMessage();
    }

    private void StartOwnershipPolling()
    {
        var now = DateTime.UtcNow;
        ownership.BeginPolling(ownershipItemIds);
        ownershipPollingEndsUtc = now + OwnershipPollingDuration;
        // Do not perform ownership work on the slash-command frame itself.
        nextOwnershipPollUtc = now + TimeSpan.FromMilliseconds(100);
        nextOwnershipTransientRefreshUtc = nextOwnershipPollUtc;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
            return;

        var now = DateTime.UtcNow;
        if (now >= ownershipPollingEndsUtc || now < nextOwnershipPollUtc)
            return;

        // Schedule the next small chunk before doing the work so a slow sample can
        // never cause several catch-up chunks on consecutive frames.
        nextOwnershipPollUtc = now + OwnershipPollingInterval;
        var refreshTransientSources = now >= nextOwnershipTransientRefreshUtc;
        if (refreshTransientSources)
            nextOwnershipTransientRefreshUtc = now + OwnershipTransientRefreshInterval;

        try
        {
            ownership.PollSnapshotStep(ownershipJobIds, PermanentUnlockChecksPerPoll, refreshTransientSources);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ownership command polling sample failed.");
        }
    }

    private void PrintStartupMessage()
    {
        if (startupMessagePrinted)
            return;

        startupMessagePrinted = true;

        // Keep this deliberately plain: no SeString payloads, custom tag color,
        // or other chat styling. It should look like an ordinary informational
        // line and only appear once per plugin/game startup.
        ChatGui.Print("[Custom Quest Guide] /cqg opens the guide and starts a 30-second ownership refresh in the background. /cqg <item name or item ID> also searches directly. See the Help tab for details.");
    }

    private void OnCommand(string command, string args)
    {
        // The slash command starts a bounded background ownership refresh. The
        // expensive work is spread across later framework updates rather than
        // running synchronously on the command frame.
        StartOwnershipPolling();
        windowOpen = true;
        activeTab = MainTab.Items;

        var query = args?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return;

        // /cqg <name-or-item-id> doubles as a fast global target lookup.
        targetSearch = query;

        TargetEntry? exact = null;

        if (uint.TryParse(query, out var itemId))
            exact = catalog.GetById(itemId);

        exact ??= catalog.Targets.FirstOrDefault(t =>
            t.Name.Equals(query, StringComparison.OrdinalIgnoreCase));

        if (exact is null)
            return;

        config.SelectedSetId = string.Empty;
        config.SelectedCategory = exact.Category;
        config.SelectedJob = "All";
        config.SelectedItemId = exact.ItemId;
        config.SelectedChainIndex = 0;
        SaveConfig();
    }

    private void OpenMainWindow() => windowOpen = true;

    private void Draw()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(780, 600), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Custom Quest Guide###CustomQuestGuide", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        DrawMainTabs();
        ImGui.Separator();

        switch (activeTab)
        {
            case MainTab.Jobs:
                DrawJobsAndProgressionTab();
                break;
            case MainTab.Pins:
                DrawPinnedTargetsTab();
                break;
            case MainTab.Help:
                DrawHelpTab();
                break;
            default:
                DrawItemsTab();
                break;
        }

        ImGui.End();
    }

    private void DrawMainTabs()
    {
        if (ImGui.Button(activeTab == MainTab.Items ? "[ Items ]" : "Items"))
            activeTab = MainTab.Items;

        ImGui.SameLine();
        if (ImGui.Button(activeTab == MainTab.Jobs ? "[ Jobs & Progression ]" : "Jobs & Progression"))
            activeTab = MainTab.Jobs;

        ImGui.SameLine();
        if (ImGui.Button(activeTab == MainTab.Pins ? $"[ Pinned Targets ({config.PinnedTargets.Count}) ]" : $"Pinned Targets ({config.PinnedTargets.Count})"))
            activeTab = MainTab.Pins;

        ImGui.SameLine();
        if (ImGui.Button(activeTab == MainTab.Help ? "[ Help ]" : "Help"))
            activeTab = MainTab.Help;
    }


    private void DrawHelpTab()
    {
        ImGui.TextUnformatted("CUSTOM QUEST GUIDE HELP");
        ImGui.TextDisabled("Quick reference for commands, colors, ownership detection, guidance, and common troubleshooting.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Quick command");
        ImGui.Separator();
        ImGui.BulletText("/cqg opens Custom Quest Guide.");
        ImGui.BulletText("/cqg <item name or item ID> opens the Items view and attempts to select that target directly.");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, UnresolvedRed);
        ImGui.TextWrapped("IMPORTANT: CQG starts a 30-second ownership polling window when you use /cqg, then stops. The refresh runs incrementally in the background so the slash command itself does not perform one large ownership scan. Run /cqg again whenever you want ownership refreshed.");
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.TextUnformatted("Color meanings");
        ImGui.Separator();
        DrawHelpColorRow(OwnedGreen, "Green", "Owned or complete. For jobs, green means the job is unlocked and all tracked formal progression is complete.");
        DrawHelpColorRow(PartialBlue, "Blue", "Partial job state. The job is unlocked, but CQG still detects tracked formal progression remaining.");
        DrawHelpColorRow(GlamourPink, "Soft pink", "Owned through the Glamour Dresser snapshot. Pink items count as fully owned for sets, pins, and completion.");
        DrawHelpColorRow(UnresolvedRed, "Red", "CQG has not resolved a trusted acquisition source for this unowned target yet.");
        ImGui.BulletText("Default / white: not yet owned or complete, with no special state that needs highlighting.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Common questions");
        ImGui.Separator();

        ImGui.TextWrapped("Why is an item in my Glamour Dresser not pink?");
        ImGui.BulletText("Open the Glamour Dresser once on that character so CQG can read and refresh its authoritative contents.");
        ImGui.BulletText("CQG stores a per-character snapshot, so detected dresser items remain pink after zoning, plugin reloads, and game restarts.");
        ImGui.BulletText("If you add or remove dresser items, reopen the dresser to refresh the snapshot.");

        ImGui.Spacing();
        ImGui.TextWrapped("Why is a job blue instead of green?");
        ImGui.BulletText("Blue means the job itself is unlocked, but its tracked formal job/class progression is not fully complete.");
        ImGui.BulletText("Individual completed progression steps remain green. Once every tracked step is complete, the job becomes green.");
        ImGui.BulletText("ARR base classes are considered complete when an unlocked derived job proves their required class progression was finished.");

        ImGui.Spacing();
        ImGui.TextWrapped("Why does CQG tell me to talk to an NPC instead of run the duty?");
        ImGui.BulletText("CQG checks whether supported duty content is unlocked. If it is locked, the next action is the unlock quest/NPC when that route can be resolved.");
        ImGui.BulletText("Once the duty is unlocked, guidance changes to running the duty itself.");

        ImGui.Spacing();
        ImGui.TextWrapped("Why is a completion-reward mount not the next set target?");
        ImGui.BulletText("Collection rewards are held behind their prerequisite set. CQG prioritizes actionable missing members first, then promotes the final reward when its prerequisites are complete.");

        ImGui.Spacing();
        ImGui.TextWrapped("Why does an owned item still look incomplete?");
        ImGui.BulletText("CQG checks ownership only during the 30 seconds after you use /cqg. When the window closes, it keeps using the last captured snapshot until you run /cqg again.");
        ImGui.BulletText("CQG can only use ownership sources exposed by the client or previously snapshotted while authoritative data was available.");
        ImGui.BulletText("For Glamour Dresser items, the persistent per-character dresser snapshot is still used. Opening the dresser updates that saved cache when authoritative dresser data is available.");

        ImGui.Spacing();
        ImGui.TextWrapped("What does Refresh acquisition sources do?");
        ImGui.BulletText("It refreshes external acquisition enrichment used when live game-sheet data is insufficient. Core game-data resolution and saved ownership snapshots remain local.");
        ImGui.BulletText("Manual source refreshes have a short cooldown so repeated clicks cannot spam community services.");
        ImGui.BulletText("If an external service is unavailable, CQG keeps usable cached/session data where possible and degrades gracefully rather than wiping working guidance.");

        ImGui.Spacing();
        ImGui.TextWrapped("How do pins work?");
        ImGui.BulletText("Items, sets, job unlocks, and job progression can be pinned from their respective views.");
        ImGui.BulletText("Pinned sets use the same smart frontier as the main set view, so their next action should follow the best currently actionable missing member.");

        ImGui.Spacing();
        ImGui.TextWrapped("Does CQG continuously monitor my character?");
        ImGui.BulletText("No. Ownership polling exists only for the 30-second window started by /cqg. After that window CQG uses the last captured state until /cqg is run again.");
        ImGui.BulletText("External acquisition-source refreshes are separate from ownership polling and are cached/rate-limited.");

        ImGui.Spacing();
        ImGui.TextWrapped("How can I diagnose bad guidance?");
        ImGui.BulletText("Enable Detailed, then Debug, on the relevant target. The debug panel exposes ownership, resolver, source, progression, and polling timing information used by CQG.");
        ImGui.BulletText("Export resolver audit CSV includes source provenance, next-action data, ownership state, and contradiction checks.");
        ImGui.BulletText("Debug exports intentionally avoid account credentials, auth tokens, and machine-secret data.");

        ImGui.Spacing();
        ImGui.TextDisabled("Custom Quest Guide 1.0.0");
    }

    private static void DrawHelpColorRow(Vector4 color, string label, string description)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextWrapped($"— {description}");
    }

    private void DrawItemsTab()
    {
        DrawTargetPicker();

        var selectedSet = setCatalog.Get(config.SelectedSetId);

        if (selectedSet is not null)
        {
            ImGui.Separator();
            DrawSetSummary(selectedSet);

            if (config.Detailed && !IsRelicSet(selectedSet))
                DrawSetDetailed(selectedSet);

            DrawDebugPanel(null, selectedSet);
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

        var result = resolver.Resolve(selected);
        DrawTargetSummary(result);

        if (config.Detailed)
            DrawDetailed(result);

        DrawDebugPanel(result, null);
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
                    config.SelectedClassJobId = candidate.ClassJobId;
                    SaveConfig();
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
        var unlockComplete = jobUnlocked || unlockState == QuestProgressState.Completed;
        var progressionState = ResolveJobQuestSequence(job.Progression);
        var derivedJobProof = GetUnlockedDerivedJob(job);
        var progressionInferredComplete = derivedJobProof is not null;
        var completedProgression = progressionInferredComplete
            ? job.Progression.Count
            : progressionState.Entries.Count(x => x.EffectiveState == QuestProgressState.Completed);
        var nextEntry = progressionInferredComplete ? null : progressionState.CurrentOrNext;
        var nextStep = nextEntry?.Info is { } nextInfo
            ? job.Progression.FirstOrDefault(x => x.QuestId == nextInfo.QuestId)
            : nextEntry is not null
                ? job.Progression.FirstOrDefault(x => x.Name.Equals(nextEntry.Name, StringComparison.OrdinalIgnoreCase))
                : null;

        ImGui.Spacing();
        DrawPinToggle("JobUnlock", job.ClassJobId, string.Empty, "Pin Unlock", "Unpin Unlock");
        ImGui.SameLine();
        DrawPinToggle("JobProgression", job.ClassJobId, string.Empty, "Pin Progression", "Unpin Progression");

        ImGui.Spacing();
        DrawColoredText("Unlock:", unlockComplete);
        ImGui.SameLine();
        if (job.UnlockQuest is null)
        {
            ImGui.TextDisabled("No explicit unlock quest relation found in the current game data.");
        }
        else
        {
            var levelText = job.UnlockQuest.Level > 0 ? $"Lv. {job.UnlockQuest.Level} — " : string.Empty;
            DrawColoredText($"{levelText}{job.UnlockQuest.Name}", unlockComplete);
            ImGui.SameLine();
            if (unlockComplete)
                DrawColoredText("[Unlocked]", true);
            else
                ImGui.TextDisabled(unlockState == QuestProgressState.InProgress ? "[In progress]" : "[Not started]");

            if (!string.IsNullOrWhiteSpace(job.UnlockQuest.QuestGiver))
            {
                var location = string.IsNullOrWhiteSpace(job.UnlockQuest.Location)
                    ? string.Empty
                    : $" — {job.UnlockQuest.Location}";
                var prefix = unlockComplete
                    ? "Unlocked via"
                    : unlockState == QuestProgressState.InProgress
                        ? "Quest started by"
                        : "Go to";
                ImGui.TextDisabled($"{prefix}: {job.UnlockQuest.QuestGiver}{location}");
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

        ImGui.Spacing();
        DrawColoredText(
            $"Formal progression: {completedProgression} / {job.Progression.Count} quests complete",
            job.Progression.Count > 0 && (progressionState.IsComplete || progressionInferredComplete));
        if (progressionInferredComplete && derivedJobProof is not null)
        {
            ImGui.TextDisabled(
                $"Class progression complete — inferred from unlocked {derivedJobProof.Name} ({derivedJobProof.Abbreviation}).");
        }
        if (nextStep is not null)
        {
            var nextLevel = nextStep.Level > 0 ? $"Lv. {nextStep.Level} — " : string.Empty;
            var active = progressionState.Active is not null;
            ImGui.TextWrapped($"{(active ? "Current quest" : "Next quest")}: {nextLevel}{nextStep.Name}");
            if (!string.IsNullOrWhiteSpace(nextStep.QuestGiver))
            {
                var location = string.IsNullOrWhiteSpace(nextStep.Location)
                    ? string.Empty
                    : $" — {nextStep.Location}";
                ImGui.TextDisabled($"{(active ? "Quest started by" : "Go to")}: {nextStep.QuestGiver}{location}");
            }
        }
        else if (job.Progression.Count > 0)
        {
            ImGui.TextDisabled("Formal class/job quest progression complete.");
        }

        ImGui.Spacing();
        for (var i = 0; i < job.Progression.Count; i++)
        {
            var step = job.Progression[i];
            var state = progressionInferredComplete
                ? QuestProgressState.Completed
                : i < progressionState.Entries.Count
                    ? progressionState.Entries[i].EffectiveState
                    : GetQuestStateByRowId(step.QuestId);
            var marker = state == QuestProgressState.Completed ? "[X]" :
                state == QuestProgressState.InProgress ? "[>]" : "[ ]";
            var level = step.Level > 0 ? $"Lv. {step.Level} — " : string.Empty;
            DrawColoredText($"{marker} {level}{step.Name}", state == QuestProgressState.Completed);

            if (state != QuestProgressState.Completed &&
                !string.IsNullOrWhiteSpace(step.QuestGiver) &&
                nextStep?.QuestId == step.QuestId)
            {
                ImGui.Indent();
                var location = string.IsNullOrWhiteSpace(step.Location)
                    ? string.Empty
                    : $" — {step.Location}";
                ImGui.TextDisabled($"{(state == QuestProgressState.InProgress ? "Quest started by" : "Go to")}: {step.QuestGiver}{location}");
                ImGui.Unindent();
            }
        }

        if (config.Detailed)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextDisabled($"ClassJob row: {job.ClassJobId}");
            ImGui.TextDisabled($"Live job unlocked: {ownership.IsJobUnlocked(job.ClassJobId)}");
            ImGui.TextDisabled($"Unlock resolution: {job.UnlockResolution}");
            ImGui.TextDisabled($"ClassJob.UnlockQuest row: {job.ClassJobUnlockQuestId}");
            ImGui.TextDisabled($"Formal quest nodes: {job.Progression.Count}");
        }
    }

    private void DrawPinnedTargetsTab()
    {
        ImGui.TextWrapped(
            "Unified priority queue for item, set, job-unlock, and formal job-progression targets. " +
            "Priority is the list order.");
        ImGui.Spacing();

        if (config.PinnedTargets.Count == 0)
        {
            ImGui.TextDisabled("No targets are pinned yet. Pin items, sets, or class/job progression from their tabs.");
            return;
        }

        for (var i = 0; i < config.PinnedTargets.Count; i++)
        {
            var pin = config.PinnedTargets[i];
            ImGui.PushID(i);

            var title = DescribePinTitle(pin);
            var subtitle = DescribePinSubtitle(pin);

            if (pin.Kind.Equals("JobProgression", StringComparison.OrdinalIgnoreCase) &&
                jobCatalog.Get(pin.NumericId) is { } pinnedJob &&
                ownership.IsJobUnlocked(pinnedJob.ClassJobId) &&
                !IsPinComplete(pin))
            {
                DrawJobStateText($"{i + 1}. {title}", JobVisualState.Partial);
            }
            else if (pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase))
            {
                var itemStatus = ownership.GetItemStatus(pin.NumericId);
                DrawOwnershipText($"{i + 1}. {title}", itemStatus);
            }
            else
            {
                DrawColoredText($"{i + 1}. {title}", IsPinComplete(pin));
            }
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                ImGui.Indent();
                ImGui.TextWrapped(subtitle);
                ImGui.Unindent();
            }

            if (ImGui.Button("Open"))
                OpenPinnedTarget(pin);

            ImGui.SameLine();
            if (i > 0 && ImGui.Button("Up"))
            {
                SwapPins(i, i - 1);
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (i < config.PinnedTargets.Count - 1 && ImGui.Button("Down"))
            {
                SwapPins(i, i + 1);
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (i > 0 && ImGui.Button("Top"))
            {
                var moving = config.PinnedTargets[i];
                config.PinnedTargets.RemoveAt(i);
                config.PinnedTargets.Insert(0, moving);
                SaveConfig();
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (ImGui.Button("Remove"))
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

        // ARR base classes are necessarily complete through their class quest line
        // once one of their derived jobs has been unlocked. This is also a useful
        // fallback when the Quest sheet does not expose the full base-class chain.
        if (GetUnlockedDerivedJob(job) is not null)
            return JobVisualState.Complete;

        if (job.Progression.Count == 0)
            return JobVisualState.Complete;

        return ResolveJobQuestSequence(job.Progression).IsComplete
            ? JobVisualState.Complete
            : JobVisualState.Partial;
    }

    private JobProgressionTarget? GetUnlockedDerivedJob(JobProgressionTarget baseClass) =>
        jobCatalog.Jobs.FirstOrDefault(candidate =>
            candidate.ClassJobId != baseClass.ClassJobId &&
            candidate.ParentClassJobId == baseClass.ClassJobId &&
            ownership.IsJobUnlocked(candidate.ClassJobId));

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
                return members.Length > 0 && members.All(x => ownership.GetItemStatus(x.ItemId).IsOwned);
            }
            case "JobUnlock":
                return ownership.IsJobUnlocked(pin.NumericId);
            case "JobProgression":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null)
                    return false;
                if (!ownership.IsJobUnlocked(job.ClassJobId))
                    return false;
                if (GetUnlockedDerivedJob(job) is not null)
                    return true;
                if (job.Progression.Count == 0)
                    return true;
                return ResolveJobQuestSequence(job.Progression).IsComplete;
            }
            default:
                return false;
        }
    }

    private TargetSet? FindContainingSet(uint itemId) =>
        setCatalog.Sets.FirstOrDefault(set => set.ItemIds.Contains(itemId));

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
            ImGui.TextUnformatted($"{frontier.Action.ActionLabel}: {frontier.Action.ActionText}");
            if (!string.IsNullOrWhiteSpace(frontier.Action.GoTo))
                ImGui.TextWrapped($"Go to: {frontier.Action.GoTo}");
            else if (!string.IsNullOrWhiteSpace(frontier.Action.QuestStartedBy))
                ImGui.TextDisabled($"Quest started by: {frontier.Action.QuestStartedBy}");
            return;
        }

        if (frontier.DutyAdvice is { } advice)
        {
            if (advice.IsUnlocked == true)
            {
                ImGui.TextUnformatted($"Run: {advice.DutyName}");
            }
            else
            {
                ImGui.TextUnformatted($"Unlock content: {advice.DutyName}");
                if (!string.IsNullOrWhiteSpace(advice.QuestName))
                    ImGui.TextUnformatted($"Unlock via: {advice.QuestName}");
                if (!string.IsNullOrWhiteSpace(advice.Contact))
                {
                    var where = string.IsNullOrWhiteSpace(advice.Location) ? string.Empty : $" — {advice.Location}";
                    ImGui.TextWrapped($"Talk to: {advice.Contact}{where}");
                }
                if (!string.IsNullOrWhiteSpace(advice.Note))
                    ImGui.TextDisabled(advice.Note);
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
            "Set" => setCatalog.Get(pin.StringId) is { } set
                ? $"[SET] {set.Name}{(string.IsNullOrWhiteSpace(pin.Variant) ? string.Empty : $" — {pin.Variant} {RelicWeaponType(pin.Variant)}")}"
                : $"[SET] Missing set {pin.StringId}",
            "JobUnlock" => jobCatalog.Get(pin.NumericId) is { } unlockJob
                ? $"[JOB] Unlock {unlockJob.Name}"
                : $"[JOB] Missing ClassJob #{pin.NumericId}",
            "JobProgression" => jobCatalog.Get(pin.NumericId) is { } progressionJob
                ? $"[JOB] {progressionJob.Name} Progression"
                : $"[JOB] Missing ClassJob #{pin.NumericId}",
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

                var ownedCount = members.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned);
                if (members.Length > 0 && ownedCount == members.Length)
                    return $"Complete — {ownedCount} / {members.Length} owned.";

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
                            return $"{prefix}: {frontier.Member.Name} — Run: {advice.DutyName}";

                        var via = string.IsNullOrWhiteSpace(advice.QuestName) ? string.Empty : $" via {advice.QuestName}";
                        var who = string.IsNullOrWhiteSpace(advice.Contact)
                            ? string.Empty
                            : $" — Talk to {advice.Contact}{(string.IsNullOrWhiteSpace(advice.Location) ? string.Empty : $" — {advice.Location}")}";
                        return $"{prefix}: {frontier.Member.Name} — Unlock: {advice.DutyName}{via}{who}";
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
            case "JobUnlock":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null)
                    return "Class/job is no longer present in the current game data.";
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

                var sequence = ResolveJobQuestSequence(job.Progression);
                var completed = sequence.Entries.Count(x => x.EffectiveState == QuestProgressState.Completed);
                var actionable = sequence.CurrentOrNext;
                if (actionable is null)
                    return $"Formal progression complete ({completed} / {job.Progression.Count}).";
                var who = sequence.Active is null ? FormatWhoWhere(actionable.Info) : string.Empty;
                var label = sequence.Active is not null ? "Current" : "Next";
                return string.IsNullOrWhiteSpace(who)
                    ? $"{completed} / {job.Progression.Count} complete — {label}: {actionable.Name}"
                    : $"{completed} / {job.Progression.Count} complete — {label}: {actionable.Name} — Go to {who}";
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
                config.SelectedSetId = string.Empty;
                config.SelectedCategory = item.Category;
                config.SelectedJob = "All";
                config.SelectedItemId = item.ItemId;
                targetSearch = string.Empty;
                activeTab = MainTab.Items;
                SaveConfig();
                break;
            }
            case "Set":
            {
                if (setCatalog.Get(pin.StringId) is null)
                    return;
                config.SelectedSetId = pin.StringId;
                if (!string.IsNullOrWhiteSpace(pin.Variant))
                    config.SelectedRelicJob = pin.Variant;
                activeTab = MainTab.Items;
                SaveConfig();
                break;
            }
            case "JobUnlock":
            case "JobProgression":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null)
                    return;
                config.SelectedProgressionCategory = job.Category;
                config.SelectedClassJobId = job.ClassJobId;
                activeTab = MainTab.Jobs;
                SaveConfig();
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
        string variant = "")
    {
        var index = FindPinIndex(kind, numericId, stringId, variant);
        var pinned = index >= 0;

        if (!ImGui.Button(pinned ? unpinLabel : pinLabel))
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

    private void DrawTargetPicker()
    {
        // ---------- Optional broad Set Target ----------
        var sets = setCatalog.Sets;
        var setLabels = new List<string> { "(Single item)" };
        setLabels.AddRange(sets.Select(s => $"{s.Group} — {s.Name}"));

        var selectedSetIndex = 0;

        if (!string.IsNullOrWhiteSpace(config.SelectedSetId))
        {
            var setIndex = sets
                .Select((set, index) => (set, index))
                .FirstOrDefault(x =>
                    string.Equals(
                        x.set.Id,
                        config.SelectedSetId,
                        StringComparison.OrdinalIgnoreCase))
                .index;

            if (sets.Count > 0 &&
                sets.Any(s => string.Equals(
                    s.Id,
                    config.SelectedSetId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                selectedSetIndex = setIndex + 1;
            }
        }

        var setLabelArray = setLabels.ToArray();

        if (ImGui.Combo(
                "Set Target",
                ref selectedSetIndex,
                setLabelArray,
                setLabelArray.Length))
        {
            config.SelectedSetId = selectedSetIndex == 0
                ? string.Empty
                : sets[selectedSetIndex - 1].Id;
            config.SelectedRelicJob = string.Empty;

            SaveConfig();
        }

        // A selected Set Target replaces the normal Category/Target selectors.
        if (!string.IsNullOrWhiteSpace(config.SelectedSetId))
        {
            DrawCommonTargetControls();
            return;
        }

        // ---------- Category ----------
        var categories = catalog.Categories;
        var categoryIndex = IndexOf(categories, config.SelectedCategory);
        if (categoryIndex < 0)
            categoryIndex = 0;

        var categoryNames = categories.ToArray();

        if (ImGui.Combo("Category", ref categoryIndex, categoryNames, categoryNames.Length))
        {
            config.SelectedCategory = categoryNames[categoryIndex];
            config.SelectedJob = "All";
            config.SelectedItemId = 0;
            config.SelectedChainIndex = 0;
            targetSearch = string.Empty;
            SelectFirstTarget();
            SaveConfig();
        }

        // ---------- Gear-only class/job filter ----------
        if (TargetCatalog.IsGearCategory(config.SelectedCategory))
        {
            var jobs = catalog.Jobs;
            var jobIndex = IndexOf(jobs, config.SelectedJob);
            if (jobIndex < 0)
                jobIndex = 0;

            var jobNames = jobs.ToArray();

            if (ImGui.Combo("Class / Job", ref jobIndex, jobNames, jobNames.Length))
            {
                config.SelectedJob = jobNames[jobIndex];
                config.SelectedItemId = 0;
                config.SelectedChainIndex = 0;
                SelectFirstTarget();
                SaveConfig();
            }
        }

        // ---------- Search + Target ----------
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint(
            "##TargetSearch",
            "Search all targets...",
            ref targetSearch,
            128);

        ImGui.SameLine();

        var allTargets = catalog.GetTargets(
            config.SelectedCategory,
            config.SelectedJob);

        // Once the user types, search the entire target catalog rather than
        // requiring them to already know which CQG category an item belongs to.
        // Exact and prefix matches float to the top; cap very broad searches so
        // one-character input cannot create an enormous ImGui combo.
        IReadOnlyList<TargetEntry> visibleTargets;

        if (string.IsNullOrWhiteSpace(targetSearch))
        {
            visibleTargets = allTargets;
        }
        else
        {
            visibleTargets = GetGlobalSearchResults(targetSearch.Trim());
        }

        if (!string.IsNullOrWhiteSpace(targetSearch) && visibleTargets.Count == 0)
            ImGui.TextDisabled("No targets match that search.");
        else if (!string.IsNullOrWhiteSpace(targetSearch) && visibleTargets.Count >= 500)
            ImGui.TextDisabled("Showing the first 500 matches. Refine the search for a narrower result list.");

        var selectedTarget =
            catalog.GetById(config.SelectedItemId)?.Name ??
            "Select a target...";

        ImGui.SetNextItemWidth(440f);

        if (ImGui.BeginCombo("Target", selectedTarget))
        {
            if (visibleTargets.Count == 0)
            {
                ImGui.TextDisabled("No targets match the current filters.");
            }
            else
            {
                foreach (var target in visibleTargets)
                {
                    var selected = target.ItemId == config.SelectedItemId;
                    var resolved = resolver.HasDirectSource(target);
                    var ownedStatus = ownership.GetItemStatus(target.ItemId);
                    var owned = ownedStatus.IsOwned;

                    // Ownership is the strongest visible state. Glamour-dresser
                    // ownership is intentionally distinct from ordinary possession.
                    // Unresolved remains red only when the target is not owned.
                    if (owned)
                        ImGui.PushStyleColor(ImGuiCol.Text, IsGlamourDresserOwnership(ownedStatus) ? GlamourPink : OwnedGreen);
                    else if (!resolved)
                        ImGui.PushStyleColor(ImGuiCol.Text, UnresolvedRed);

                    if (ImGui.Selectable(target.Name, selected))
                    {
                        config.SelectedItemId = target.ItemId;
                        config.SelectedChainIndex = 0;

                        if (!string.IsNullOrWhiteSpace(targetSearch))
                        {
                            config.SelectedCategory = target.Category;
                            config.SelectedJob = "All";
                        }

                        SaveConfig();
                    }

                    if (owned || !resolved)
                        ImGui.PopStyleColor();

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        DrawCommonTargetControls();
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
            // Manual refreshes are deliberately rate-limited so repeated clicks
            // cannot hammer community services or create avoidable network load.
            nextManualSourceRefreshUtc = DateTime.UtcNow.AddSeconds(15);
            coverageMetricsExpiresUtc = DateTime.MinValue;
            _ = collectibleSources.RefreshAsync();

            _ = garlandSources.RefreshAsync(
                catalog.Targets
                    .Where(target => resolver.NeedsGarlandEnrichment(target))
                    .Select(target => target.ItemId),
                force: true);
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
        ImGui.TextUnformatted($"Set Progress: {ownedCount} / {members.Length} owned; {resolved} / {members.Length} source-resolved");

        DrawSetFrontierGuidance(set, members);
        ImGui.Spacing();

        var prerequisiteMembers = members.Where(x => !IsCompletionReward(set, x)).ToArray();
        var prerequisiteOwned = prerequisiteMembers.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned);
        var prerequisitesComplete = prerequisiteMembers.Length == 0 || prerequisiteOwned == prerequisiteMembers.Length;

        foreach (var member in members)
        {
            var result = resolver.Resolve(member);
            var primary = result.Sources.FirstOrDefault();
            var owned = ownership.GetItemStatus(member.ItemId);

            if (owned.IsOwned)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, IsGlamourDresserOwnership(owned) ? GlamourPink : OwnedGreen);
                ImGui.BulletText($"{member.Name} — Owned ({owned.Source})");
                ImGui.PopStyleColor();
                continue;
            }

            if (IsCompletionReward(set, member) && !prerequisitesComplete)
            {
                ImGui.TextDisabled($"• {member.Name} — Locked until prerequisite collection is complete ({prerequisiteOwned} / {prerequisiteMembers.Length})");
                continue;
            }

            var action = actionGuidance.Resolve(result);
            if (!action.IsEmpty)
            {
                var who = string.IsNullOrWhiteSpace(action.GoTo) ? string.Empty : $" — Go to {action.GoTo}";
                ImGui.BulletText($"{member.Name} — {action.ActionLabel}: {action.ActionText}{who}");
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
        }
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
        if (ImGui.Combo("Job / Weapon##RelicJob", ref jobIndex, labels, labels.Length))
        {
            config.SelectedRelicJob = jobs[jobIndex];
            SaveConfig();
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
            DrawFilteredRelicGuidance(filtered);
    }

    private void DrawFilteredRelicGuidance(IReadOnlyList<TargetEntry> members)
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

        // No quest-backed stage relationship was exposed. Keep the resolver
        // fallback truthful rather than pretending a progression graph exists.
        foreach (var member in members)
        {
            var result = resolver.Resolve(member);
            var source = result.Sources.FirstOrDefault();
            var owned = ownership.GetItemStatus(member.ItemId);
            if (owned.IsOwned)
                ImGui.PushStyleColor(ImGuiCol.Text, IsGlamourDresserOwnership(owned) ? GlamourPink : OwnedGreen);
            ImGui.BulletText(owned.IsOwned
                ? $"{member.Name} — Owned ({owned.Source})"
                : source is null
                    ? $"{member.Name} — No source resolved"
                    : $"{member.Name} — {source.Name}");
            if (owned.IsOwned)
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
            var stageOwned = stageItems.All(x => ownership.GetItemStatus(x.ItemId).IsOwned);

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

    private void DrawDebugPanel(
        AcquisitionResult? selectedResult,
        TargetSet? selectedSet)
    {
        if (!config.Detailed)
            return;

        ImGui.Spacing();
        ImGui.Separator();

        var debug = config.Debug;
        if (ImGui.Checkbox("Debug", ref debug))
        {
            config.Debug = debug;
            SaveConfig();
        }

        if (!config.Debug)
            return;

        ImGui.Spacing();

        RefreshCoverageMetrics();
        var unresolvedCount = cachedUnresolvedCount;

        ImGui.TextDisabled($"Catalog targets: {catalog.Targets.Count:N0}");
        ImGui.TextDisabled($"Direct-source coverage: {cachedDirectCount:N0} / {catalog.Targets.Count:N0}");
        ImGui.TextDisabled($"Unresolved targets: {unresolvedCount:N0}");
        ImGui.TextDisabled($"Target sets: {setCatalog.Sets.Count:N0}");
        ImGui.TextDisabled(
            $"Job progression targets: {jobCatalog.Jobs.Count:N0} " +
            $"({jobCatalog.Jobs.Count(j => j.UnlockQuest is not null):N0} with unlock quest; " +
            $"{jobCatalog.Jobs.Sum(j => j.Progression.Count):N0} formal quest nodes)");
        ImGui.TextDisabled($"Pinned targets: {config.PinnedTargets.Count:N0}");
        var pollingRemaining = ownershipPollingEndsUtc > DateTime.UtcNow
            ? Math.Max(0, (ownershipPollingEndsUtc - DateTime.UtcNow).TotalSeconds)
            : 0;
        ImGui.TextDisabled(
            $"Ownership polling: {(pollingRemaining > 0 ? $"active ({pollingRemaining:F1}s remaining)" : "idle")}; " +
            $"last framework slice={ownership.LastPollDurationMs:F2} ms; " +
            $"permanent candidates={ownership.PermanentCandidateCount:N0}");
        if (!string.IsNullOrWhiteSpace(releaseDiagnosticsStatus))
            ImGui.TextDisabled($"Configuration diagnostic: {releaseDiagnosticsStatus}");

        var tierSummary = string.Join(
            ", ",
            cachedResolutionTiers
                .OrderByDescending(x => x.Value)
                .Select(x => $"{x.Key}={x.Value:N0}"));

        if (!string.IsNullOrWhiteSpace(tierSummary))
            ImGui.TextDisabled($"Resolution tiers: {tierSummary}");

        var specificitySummary = string.Join(
            ", ",
            cachedSpecificity
                .OrderByDescending(x => x.Value)
                .Select(x => $"{x.Key}={x.Value:N0}"));
        if (!string.IsNullOrWhiteSpace(specificitySummary))
            ImGui.TextDisabled($"Source specificity: {specificitySummary}");

        var namedSpecificityCount = cachedSpecificity
            .Where(x => x.Key.StartsWith("Named ", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Value);
        var namedSpecificityPercent = catalog.Targets.Count > 0
            ? (100.0 * namedSpecificityCount / catalog.Targets.Count)
            : 0.0;
        ImGui.TextDisabled(
            $"Named-source specificity coverage: {namedSpecificityCount:N0} / {catalog.Targets.Count:N0} ({namedSpecificityPercent:F1}%)");
        ImGui.TextDisabled(
            $"Specificity backlog: {cachedSpecificityEnrichmentCandidates:N0}");

        var exactSpecificityCount = cachedSpecificity
            .Where(x => x.Key.Equals("Named item-specific", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Value);
        var exactSpecificityPercent = catalog.Targets.Count > 0
            ? (100.0 * exactSpecificityCount / catalog.Targets.Count)
            : 0.0;
        ImGui.TextDisabled(
            $"Exact item-specific coverage: {exactSpecificityCount:N0} / {catalog.Targets.Count:N0} ({exactSpecificityPercent:F1}%)");
        ImGui.TextDisabled(
            $"Exact-source backlog: {cachedExactSpecificityBacklog:N0}");

        var availabilitySummary = string.Join(
            ", ",
            cachedAvailability
                .OrderByDescending(x => x.Value)
                .Select(x => $"{x.Key}={x.Value:N0}"));
        if (!string.IsNullOrWhiteSpace(availabilitySummary))
            ImGui.TextDisabled($"Availability classes: {availabilitySummary}");

        ImGui.TextDisabled(
            $"Supplemental data: Store={supplemental.StoreItems.Count:N0}, " +
            $"Unobtainable={supplemental.UnobtainableItems.Count:N0}, " +
            $"GenericSources={supplemental.ItemSources.Count:N0}, " +
            $"DutyItems={supplemental.DutyReferencesByItem.Count:N0}");
        ImGui.TextDisabled(
            $"Supplemental rows: StoreItem={supplemental.StoreItemRows:N0}, " +
            $"UnobtainableItem={supplemental.UnobtainableItemRows:N0}");
        ImGui.TextDisabled(
            $"Supplemental shop metadata: ShopNames={supplemental.ShopNames.Count:N0}, " +
            $"ShopNpcLinks={supplemental.ShopNpcIds.Sum(x => x.Value.Count):N0}");

        ImGui.TextDisabled(
            $"Community collectible sources: {collectibleSources.Status}");
        ImGui.TextDisabled(
            $"Garland fallback: {garlandSources.Status}");

        ImGui.Spacing();
        ImGui.TextDisabled(
            $"Game providers: Craft={resolver.CraftingSourceItems:N0}, " +
            $"GilShop={resolver.GilShopSourceItems:N0}, " +
            $"SpecialShop={resolver.SpecialShopSourceItems:N0}, " +
            $"GCShop={resolver.GrandCompanySourceItems:N0}");
        ImGui.TextDisabled(
            $"Game providers: Quest={resolver.QuestSourceItems:N0}, " +
            $"Achievement={resolver.AchievementSourceItems:N0}, " +
            $"Gathering={resolver.GatheringSourceItems:N0}, " +
            $"Leve={resolver.LeveSourceItems:N0}");
        ImGui.TextDisabled(
            $"Game providers: CompanyCraft={resolver.CompanyCraftSourceItems:N0}");

        ImGui.Spacing();
        ImGui.TextDisabled(
            $"Curated source fallbacks: total={resolver.CuratedFallbackItemCount:N0}, " +
            $"fallback-only={resolver.CuratedFallbackOnlyCount:N0}, " +
            $"superseded={resolver.CuratedFallbackSupersededCount:N0}");
        ImGui.TextDisabled(
            "Curated fallbacks are hidden whenever generic or metadata-driven data can replace them.");
        ImGui.TextDisabled(
            $"Catalog exclusions: StoreData={catalog.SupplementalStoreExclusionCount:N0}, " +
            $"SupplementalUnobtainable={catalog.SupplementalUnobtainableExclusionCount:N0}, " +
            $"LiveUnobtainable={catalog.LiveUnobtainableExclusionCount:N0}, " +
            $"Curated={catalog.CuratedExclusionCount:N0}");
        ImGui.TextDisabled(
            $"Non-item virtual progression rows excluded: " +
            $"{catalog.VirtualProgressionExclusionCount:N0}");
        ImGui.TextDisabled(
            $"Ephemeral task/turn-in items excluded: " +
            $"{catalog.EphemeralTaskItemExclusionCount:N0}");
        ImGui.TextDisabled(
            $"Explicit retired-system rows excluded: " +
            $"{catalog.RetiredSystemExclusionCount:N0}");

        if (selectedSet is not null)
        {
            ImGui.TextDisabled($"Selected Set ID: {selectedSet.Id}");
            ImGui.TextDisabled($"Selected Set Members: {selectedSet.ItemIds.Count}");
            var selectedMembers = selectedSet.ItemIds
                .Select(catalog.GetById)
                .Where(x => x is not null)
                .Cast<TargetEntry>()
                .ToArray();
            ImGui.TextDisabled($"Selected Set Owned: {selectedMembers.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned)} / {selectedMembers.Length}");
            foreach (var member in selectedMembers.Where(x => !ownership.GetItemStatus(x.ItemId).IsOwned))
            {
                var advice = GetDutyUnlockAdvice(selectedSet, member);
                if (advice is null)
                    continue;
                ImGui.TextDisabled($"Unlock trace: {advice.DutyName} => unlocked={advice.IsUnlocked?.ToString() ?? "unknown"}, quest={(string.IsNullOrWhiteSpace(advice.QuestName) ? "?" : advice.QuestName)}, contact={advice.Contact ?? "?"}, location={advice.Location ?? "?"}");
                break;
            }
        }
        else if (selectedResult is not null)
        {
            var owned = ownership.GetItemStatus(selectedResult.Target.ItemId);
            ImGui.TextDisabled($"Selected Item ID: {selectedResult.Target.ItemId}");
            ImGui.TextDisabled($"Selected UI Category: {selectedResult.Target.GameUiCategory}");
            ImGui.TextDisabled($"Selected Filter Group: {selectedResult.Target.FilterGroup}");
            ImGui.TextDisabled($"Selected ownership: {owned.IsOwned} {(owned.IsOwned ? $"({owned.Source})" : string.Empty)}");
            ImGui.TextDisabled(
                $"Selected direct source known: {resolver.HasDirectSource(selectedResult.Target)}");
        }

        ImGui.Spacing();

        if (ImGui.Button($"Export unresolved CSV ({unresolvedCount})"))
            ExportUnresolvedCsv();

        ImGui.SameLine();

        if (ImGui.Button("Export resolver audit CSV"))
            ExportResolverAuditCsv();

        ImGui.SameLine();

        if (ImGui.Button($"Export specificity backlog ({cachedSpecificityEnrichmentCandidates:N0})"))
            ExportSpecificityBacklogCsv();

        ImGui.SameLine();

        if (ImGui.Button($"Export exact-source backlog ({cachedExactSpecificityBacklog:N0})"))
            ExportExactSpecificityBacklogCsv();

        ImGui.SameLine();

        if (ImGui.Button("Export job progression audit"))
            ExportJobProgressionAuditCsv();

        if (selectedResult is not null || selectedSet is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Export selected quest guidance trace"))
                ExportSelectedQuestGuidanceCsv(selectedResult, selectedSet);
        }

        if (selectedSet is not null && IsRelicSet(selectedSet))
        {
            ImGui.SameLine();
            if (ImGui.Button("Export selected relic trace"))
                ExportRelicProgressionTraceCsv(selectedSet);
        }

        if (!string.IsNullOrWhiteSpace(exportStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(exportStatus);
        }
    }

    private void ExportSelectedQuestGuidanceCsv(
        AcquisitionResult? selectedResult,
        TargetSet? selectedSet)
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var fileName = $"CustomQuestGuide-QuestGuidanceTrace-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);
            var csv = new StringBuilder();
            csv.AppendLine("Target Type,Target ID,Target Name,Selected Variant,Source Kind,Source Name,Quest Name,Quest Row ID,Raw State,Quest Giver,Location,Previous Quest IDs");

            void AppendTarget(string targetType, TargetEntry target, string variant)
            {
                var result = resolver.Resolve(target);
                foreach (var source in result.Sources)
                {
                    var questNames = QuestGuidanceIndex.ExtractQuestNames(source);
                    foreach (var questName in questNames)
                    {
                        var infos = questGuidance.GetByName(questName);
                        if (infos.Count == 0)
                        {
                            csv.Append(Csv(targetType)); csv.Append(',');
                            csv.Append(Csv(target.ItemId.ToString())); csv.Append(',');
                            csv.Append(Csv(target.Name)); csv.Append(',');
                            csv.Append(Csv(variant)); csv.Append(',');
                            csv.Append(Csv(source.Kind)); csv.Append(',');
                            csv.Append(Csv(source.Name)); csv.Append(',');
                            csv.Append(Csv(questName)); csv.Append(",0,Unresolved,,,\n");
                            continue;
                        }

                        foreach (var info in infos)
                        {
                            csv.Append(Csv(targetType)); csv.Append(',');
                            csv.Append(Csv(target.ItemId.ToString())); csv.Append(',');
                            csv.Append(Csv(target.Name)); csv.Append(',');
                            csv.Append(Csv(variant)); csv.Append(',');
                            csv.Append(Csv(source.Kind)); csv.Append(',');
                            csv.Append(Csv(source.Name)); csv.Append(',');
                            csv.Append(Csv(questName)); csv.Append(',');
                            csv.Append(Csv(info.QuestId.ToString())); csv.Append(',');
                            csv.Append(Csv(GetQuestStateByRowId(info.QuestId).ToString())); csv.Append(',');
                            csv.Append(Csv(info.QuestGiver ?? string.Empty)); csv.Append(',');
                            csv.Append(Csv(info.Location ?? string.Empty)); csv.Append(',');
                            csv.Append(Csv(string.Join("|", info.PreviousQuestIds)));
                            csv.AppendLine();
                        }
                    }
                }
            }

            if (selectedResult is not null)
                AppendTarget("Item", selectedResult.Target, string.Empty);

            if (selectedSet is not null)
            {
                var variant = IsRelicSet(selectedSet) ? config.SelectedRelicJob ?? string.Empty : string.Empty;
                foreach (var target in selectedSet.ItemIds
                    .Select(catalog.GetById)
                    .Where(x => x is not null)
                    .Cast<TargetEntry>()
                    .Where(x => string.IsNullOrWhiteSpace(variant) || x.EquipJobs.Contains(variant)))
                {
                    AppendTarget("Set member", target, variant);
                }
            }

            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            exportStatus = $"Exported selected quest guidance trace to:\n{path}";
            Log.Information($"Exported CQG selected quest guidance trace to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Quest guidance trace export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG selected quest guidance trace.");
        }
    }

    private void ExportJobProgressionAuditCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-JobProgressionAudit-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();
            csv.AppendLine(
                "ClassJob ID,Name,Abbreviation,Category,Parent ClassJob ID,Unlock Resolution,ClassJob UnlockQuest ID,Resolved Unlock Quest ID,Resolved Unlock Quest,Unlock Quest Giver,Unlock Location,Unlock State,Formal Quest Count,Formal Effective Complete Count,Current/Next State,Current/Next Quest ID,Current/Next Quest,Current/Next Quest Giver,Current/Next Location");

            foreach (var job in jobCatalog.Jobs)
            {
                var unlockState = job.UnlockQuest is null
                    ? QuestProgressState.NotStarted
                    : GetQuestStateByRowId(job.UnlockQuest.QuestId);
                var progression = ResolveJobQuestSequence(job.Progression);
                var completed = progression.Entries.Count(x =>
                    x.EffectiveState == QuestProgressState.Completed);
                var nextEntry = progression.CurrentOrNext;
                var next = nextEntry?.Info is { } nextInfo
                    ? job.Progression.FirstOrDefault(x => x.QuestId == nextInfo.QuestId)
                    : nextEntry is not null
                        ? job.Progression.FirstOrDefault(x => x.Name.Equals(nextEntry.Name, StringComparison.OrdinalIgnoreCase))
                        : null;

                csv.Append(Csv(job.ClassJobId.ToString()));
                csv.Append(',');
                csv.Append(Csv(job.Name));
                csv.Append(',');
                csv.Append(Csv(job.Abbreviation));
                csv.Append(',');
                csv.Append(Csv(job.Category));
                csv.Append(',');
                csv.Append(Csv(job.ParentClassJobId.ToString()));
                csv.Append(',');
                csv.Append(Csv(job.UnlockResolution));
                csv.Append(',');
                csv.Append(Csv(job.ClassJobUnlockQuestId.ToString()));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest?.QuestId.ToString() ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest?.Name ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest?.QuestGiver ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest?.Location ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest is null ? "Unknown" : unlockState.ToString()));
                csv.Append(',');
                csv.Append(Csv(job.Progression.Count.ToString()));
                csv.Append(',');
                csv.Append(Csv(completed.ToString()));
                csv.Append(',');
                csv.Append(Csv(nextEntry?.RawState.ToString() ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(next?.QuestId.ToString() ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(next?.Name ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(next?.QuestGiver ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(next?.Location ?? string.Empty));
                csv.AppendLine();
            }

            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus =
                $"Exported {jobCatalog.Jobs.Count:N0} class/job progression target(s) to:\n{path}";
            Log.Information(
                $"Exported {jobCatalog.Jobs.Count} CQG class/job progression targets to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Job progression export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG class/job progression audit.");
        }
    }

    private void ExportRelicProgressionTraceCsv(TargetSet set)
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var fileName = $"CustomQuestGuide-RelicTrace-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);
            var selectedJob = config.SelectedRelicJob ?? string.Empty;

            var csv = new StringBuilder();
            csv.AppendLine("Set ID,Set Name,Selected Job,Weapon Type,Stage,Item ID,Item Name,Quest Name,Quest Row IDs,Raw Quest State,Inferred Stage Unlocked,Stage Frontier,Computed Route,Resolver Primary Source,Resolver Primary Detail");

            var members = set.ItemIds
                .Select(catalog.GetById)
                .Where(x => x is not null)
                .Cast<TargetEntry>()
                .Where(x => string.IsNullOrWhiteSpace(selectedJob) || x.EquipJobs.Contains(selectedJob))
                .ToArray();

            if (set.Id.Equals("relic-phantom", StringComparison.OrdinalIgnoreCase))
            {
                var progress = GetPhantomProgress();
                foreach (var stageState in progress.StageStates)
                {
                    var stage = stageState.Stage;
                    var stageMembers = members.Where(x => x.Name.EndsWith(" " + stage.Suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
                    foreach (var member in stageMembers)
                    {
                        var primary = resolver.Resolve(member).Sources.FirstOrDefault();
                        for (var i = 0; i < stage.QuestChain.Count; i++)
                        {
                            var questName = stage.QuestChain[i];
                            questRowIdsByName.TryGetValue(questName, out var ids);
                            var rawState = GetQuestState(questName);
                            var route = stageState.Unlocked ? stage.RepeatRoute :
                                stageState.IsFrontier ? $"Frontier: {stageState.ActiveQuest}{(string.IsNullOrWhiteSpace(stageState.ActiveQuest) ? stageState.NextQuest : string.Empty)}" :
                                "Locked by earlier one-time Phantom progression";
                            csv.Append(Csv(set.Id)); csv.Append(',');
                            csv.Append(Csv(set.Name)); csv.Append(',');
                            csv.Append(Csv(selectedJob)); csv.Append(',');
                            csv.Append(Csv(RelicWeaponType(selectedJob))); csv.Append(',');
                            csv.Append(Csv(stage.Label)); csv.Append(',');
                            csv.Append(Csv(member.ItemId.ToString())); csv.Append(',');
                            csv.Append(Csv(member.Name)); csv.Append(',');
                            csv.Append(Csv(questName)); csv.Append(',');
                            csv.Append(Csv(ids is null ? string.Empty : string.Join("|", ids))); csv.Append(',');
                            csv.Append(Csv(rawState.ToString())); csv.Append(',');
                            csv.Append(Csv(stageState.Unlocked ? "Yes" : "No")); csv.Append(',');
                            csv.Append(Csv(stageState.IsFrontier ? "Yes" : "No")); csv.Append(',');
                            csv.Append(Csv(route)); csv.Append(',');
                            csv.Append(Csv(primary?.Name ?? string.Empty)); csv.Append(',');
                            csv.Append(Csv(primary?.Detail ?? string.Empty));
                            csv.AppendLine();
                        }
                    }
                }
            }
            else
            {
                foreach (var member in members)
                {
                    var primary = resolver.Resolve(member).Sources.FirstOrDefault();
                    csv.Append(Csv(set.Id)); csv.Append(',');
                    csv.Append(Csv(set.Name)); csv.Append(',');
                    csv.Append(Csv(selectedJob)); csv.Append(',');
                    csv.Append(Csv(RelicWeaponType(selectedJob))); csv.Append(',');
                    csv.Append(Csv(string.Empty)); csv.Append(',');
                    csv.Append(Csv(member.ItemId.ToString())); csv.Append(',');
                    csv.Append(Csv(member.Name)); csv.Append(",,,,,");
                    csv.Append(Csv(primary?.Name ?? string.Empty)); csv.Append(',');
                    csv.Append(Csv(primary?.Detail ?? string.Empty));
                    csv.AppendLine();
                }
            }

            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            exportStatus = $"Exported relic progression trace to:\n{path}";
            Log.Information($"Exported CQG relic progression trace to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Relic trace export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG relic progression trace.");
        }
    }

    private void ExportUnresolvedCsv()
    {
        try
        {
            var unresolved = catalog.Targets
                .Where(t => !resolver.HasDirectSource(t))
                .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.ItemId)
                .ToArray();

            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-Unresolved-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";

            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();

            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Equip Jobs,Tradable,Resolution Status");

            foreach (var target in unresolved)
            {
                var jobs = target.EquipJobs.Count == 0
                    ? string.Empty
                    : string.Join(
                        "; ",
                        target.EquipJobs.OrderBy(
                            x => x,
                            StringComparer.OrdinalIgnoreCase));

                csv.Append(Csv(target.ItemId.ToString()));
                csv.Append(',');
                csv.Append(Csv(target.Name));
                csv.Append(',');
                csv.Append(Csv(target.Category));
                csv.Append(',');
                csv.Append(Csv(target.GameUiCategory));
                csv.Append(',');
                csv.Append(Csv(target.FilterGroup.ToString()));
                csv.Append(',');
                csv.Append(Csv(jobs));
                csv.Append(',');
                csv.Append(Csv(target.IsUntradable ? "No" : "Yes"));
                csv.Append(',');
                csv.Append(Csv("No direct source resolved"));
                csv.AppendLine();
            }

            // Emit a UTF-8 BOM so Excel opens localized item names cleanly.
            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus =
                $"Exported {unresolved.Length:N0} unresolved item(s) to:\\n{path}";

            Log.Information(
                $"Exported {unresolved.Length} unresolved CQG targets to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Export failed: {ex.Message}";
            Log.Error(ex, "Failed to export unresolved CQG targets.");
        }
    }


    private void ExportSpecificityBacklogCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-SpecificityBacklog-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();
            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Resolution Tier,Source Specificity,Primary Source Kind,Primary Source,Primary Source Detail,Garland Enrichment Eligible,Known Source Count");

            foreach (var target in catalog.Targets
                         .Where(resolver.NeedsSpecificityEnrichment)
                         .OrderBy(t => resolver.GetSourceSpecificity(t), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var result = resolver.Resolve(target);
                var primary = result.Sources.FirstOrDefault();

                csv.Append(Csv(target.ItemId.ToString()));
                csv.Append(',');
                csv.Append(Csv(target.Name));
                csv.Append(',');
                csv.Append(Csv(target.Category));
                csv.Append(',');
                csv.Append(Csv(target.GameUiCategory));
                csv.Append(',');
                csv.Append(Csv(target.FilterGroup.ToString()));
                csv.Append(',');
                csv.Append(Csv(resolver.GetResolutionTier(target)));
                csv.Append(',');
                csv.Append(Csv(resolver.GetSourceSpecificity(target)));
                csv.Append(',');
                csv.Append(Csv(primary?.Kind ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(primary?.Name ??
                    (result.Guide is not null ? result.Guide.Name : string.Empty)));
                csv.Append(',');
                csv.Append(Csv(primary?.Detail ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsGarlandEnrichment(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(result.Sources.Count.ToString()));
                csv.AppendLine();
            }

            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus = $"Exported specificity backlog to:\n{path}";
            Log.Information($"Exported CQG specificity backlog to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Specificity backlog export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG specificity backlog.");
        }
    }


    private void ExportExactSpecificityBacklogCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-ExactSourceBacklog-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();
            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Resolution Tier,Source Specificity,Primary Source Kind,Primary Source,Primary Source Detail,Garland Enrichment Eligible,Known Source Count");

            foreach (var target in catalog.Targets
                         .Where(resolver.NeedsExactItemSpecificity)
                         .OrderBy(t => resolver.GetSourceSpecificity(t), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var result = resolver.Resolve(target);
                var primary = result.Sources.FirstOrDefault();

                csv.Append(Csv(target.ItemId.ToString()));
                csv.Append(',');
                csv.Append(Csv(target.Name));
                csv.Append(',');
                csv.Append(Csv(target.Category));
                csv.Append(',');
                csv.Append(Csv(target.GameUiCategory));
                csv.Append(',');
                csv.Append(Csv(target.FilterGroup.ToString()));
                csv.Append(',');
                csv.Append(Csv(resolver.GetResolutionTier(target)));
                csv.Append(',');
                csv.Append(Csv(resolver.GetSourceSpecificity(target)));
                csv.Append(',');
                csv.Append(Csv(primary?.Kind ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(primary?.Name ??
                    (result.Guide is not null ? result.Guide.Name : string.Empty)));
                csv.Append(',');
                csv.Append(Csv(primary?.Detail ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsGarlandEnrichment(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(result.Sources.Count.ToString()));
                csv.AppendLine();
            }

            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus = $"Exported exact-source backlog to:\n{path}";
            Log.Information($"Exported CQG exact-source backlog to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Exact-source backlog export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG exact-source backlog.");
        }
    }

    private void ExportResolverAuditCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-ResolverAudit-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();
            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Owned,Ownership Source,Resolution Tier,Source Specificity,Availability Class,Direct Source Known,Curated Fallback Only,Primary Source Kind,Primary Source,Primary Source Detail,Next Action Label,Next Action,Next Action NPC/Location,Needs Specificity Enrichment,Needs Exact Item Specificity,Garland Enrichment Eligible,Known Source Count,Contradiction Check");

            foreach (var target in catalog.Targets
                         .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                csv.Append(Csv(target.ItemId.ToString()));
                csv.Append(',');
                csv.Append(Csv(target.Name));
                csv.Append(',');
                csv.Append(Csv(target.Category));
                csv.Append(',');
                csv.Append(Csv(target.GameUiCategory));
                csv.Append(',');
                csv.Append(Csv(target.FilterGroup.ToString()));
                csv.Append(',');
                var auditResult = resolver.Resolve(target);
                var primary = auditResult.Sources.FirstOrDefault();
                var ownedStatus = ownership.GetItemStatus(target.ItemId);
                var action = actionGuidance.Resolve(auditResult);
                var directSourceKnown = resolver.HasDirectSource(target);
                var availability = resolver.GetAvailabilityState(target);
                var contradiction = DescribeResolverContradiction(
                    auditResult, action, ownedStatus, directSourceKnown, availability);

                csv.Append(Csv(ownedStatus.IsOwned ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(ownedStatus.Source));
                csv.Append(',');
                csv.Append(Csv(resolver.GetResolutionTier(target)));
                csv.Append(',');
                csv.Append(Csv(resolver.GetSourceSpecificity(target)));
                csv.Append(',');
                csv.Append(Csv(availability));
                csv.Append(',');
                csv.Append(Csv(directSourceKnown ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(
                    resolver.IsCuratedFallbackOnly(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(primary?.Kind ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(primary?.Name ??
                    (auditResult.Guide is not null ? auditResult.Guide.Name : string.Empty)));
                csv.Append(',');
                csv.Append(Csv(primary?.Detail ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(action.ActionLabel));
                csv.Append(',');
                csv.Append(Csv(action.ActionText));
                csv.Append(',');
                csv.Append(Csv(!string.IsNullOrWhiteSpace(action.GoTo) ? action.GoTo : action.QuestStartedBy));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsSpecificityEnrichment(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsExactItemSpecificity(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsGarlandEnrichment(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(auditResult.Sources.Count.ToString()));
                csv.Append(',');
                csv.Append(Csv(contradiction));
                csv.AppendLine();
            }

            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus = $"Exported resolver audit to:\\n{path}";
            Log.Information($"Exported CQG resolver audit to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Resolver audit export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG resolver audit.");
        }
    }

    private static string DescribeResolverContradiction(
        AcquisitionResult result,
        SourceActionGuidance action,
        OwnershipStatus ownedStatus,
        bool directSourceKnown,
        string availability)
    {
        var issues = new List<string>();

        if (directSourceKnown && result.Sources.Count == 0 && result.Guide is null)
            issues.Add("Direct source flag without a source/guide");
        if (!directSourceKnown && (result.Sources.Count > 0 || result.Guide is not null))
            issues.Add("Source/guide exists while direct-source flag is false");
        if (!action.IsEmpty && action.Source is null)
            issues.Add("Next action has no source provenance");
        if (action.Source is not null && !result.Sources.Contains(action.Source))
            issues.Add("Next action source is absent from resolved sources");
        if (availability.Equals("Legacy / Existing Copies", StringComparison.OrdinalIgnoreCase) &&
            !action.IsEmpty &&
            (action.ActionLabel.Equals("Run", StringComparison.OrdinalIgnoreCase) ||
             action.ActionLabel.Equals("Unlock content", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add("Legacy-only item has live duty action");
        }

        // Owned targets may still show acquisition provenance in Detailed mode;
        // that is informational and intentionally is not treated as a contradiction.
        _ = ownedStatus;

        return string.Join("; ", issues);
    }

    private IReadOnlyList<TargetEntry> GetGlobalSearchResults(string query)
    {
        if (query.Equals(cachedSearchQuery, StringComparison.Ordinal))
            return cachedSearchResults;

        var hasItemId = uint.TryParse(query, out var queryItemId);

        cachedSearchResults = catalog.Targets
            .Where(t =>
                t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (hasItemId && t.ItemId == queryItemId))
            .OrderByDescending(t =>
                t.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                (hasItemId && t.ItemId == queryItemId))
            .ThenByDescending(t =>
                t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToArray();

        cachedSearchQuery = query;
        return cachedSearchResults;
    }

    private void RefreshCoverageMetrics(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < coverageMetricsExpiresUtc)
            return;

        cachedResolutionTiers.Clear();
        cachedSpecificity.Clear();
        cachedAvailability.Clear();
        var unresolved = 0;
        var specificityCandidates = 0;
        var exactSpecificityBacklog = 0;

        foreach (var target in catalog.Targets)
        {
            if (!resolver.HasDirectSource(target))
                unresolved++;

            var tier = resolver.GetResolutionTier(target);
            cachedResolutionTiers[tier] =
                cachedResolutionTiers.TryGetValue(tier, out var count)
                    ? count + 1
                    : 1;

            var specificity = resolver.GetSourceSpecificity(target);
            cachedSpecificity[specificity] =
                cachedSpecificity.TryGetValue(specificity, out var specificityCount)
                    ? specificityCount + 1
                    : 1;

            var availability = resolver.GetAvailabilityState(target);
            cachedAvailability[availability] =
                cachedAvailability.TryGetValue(availability, out var availabilityCount)
                    ? availabilityCount + 1
                    : 1;

            if (resolver.NeedsSpecificityEnrichment(target))
                specificityCandidates++;

            if (resolver.NeedsExactItemSpecificity(target))
                exactSpecificityBacklog++;
        }

        cachedUnresolvedCount = unresolved;
        cachedDirectCount = catalog.Targets.Count - unresolved;
        cachedSpecificityEnrichmentCandidates = specificityCandidates;
        cachedExactSpecificityBacklog = exactSpecificityBacklog;
        coverageMetricsExpiresUtc = now.AddSeconds(2);
    }

    private static string FriendlyResolutionTier(string tier) => tier switch
    {
        "Live/Game-sheet" => "Exact live game data",
        "FFXIV Collect" => "Exact collectible/community data",
        "LuminaSupplemental" => "Supplemental game-data relationship",
        "Garland" => "Item-specific community data",
        "Broad system" => "System-level acquisition source",
        "Curated fallback" => "Curated acquisition fallback",
        "Curated guide" => "Curated prerequisite guide",
        _ => tier,
    };

    private static string Csv(string value)
    {
        value ??= string.Empty;

        if (value.Contains('"'))
            value = value.Replace("\"", "\"\"");

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value}\""
            : value;
    }

    private void DrawTargetSummary(AcquisitionResult result)
    {
        ImGui.Spacing();
        var ownedStatus = ownership.GetItemStatus(result.Target.ItemId);
        DrawOwnershipText(result.Target.Name.ToUpperInvariant(), ownedStatus);
        ImGui.SameLine();
        DrawPinToggle("Item", result.Target.ItemId, string.Empty);
        if (ownedStatus.IsOwned)
        {
            ImGui.SameLine();
            DrawOwnershipText($"Owned — {ownedStatus.Source}", ownedStatus);
        }
        ImGui.Separator();
        ImGui.Spacing();

        var primary = result.Sources.FirstOrDefault();

        if (primary is not null)
        {
            ImGui.TextUnformatted("Source:");
            ImGui.SameLine();
            ImGui.TextUnformatted(primary.Name);

            if (!string.IsNullOrWhiteSpace(primary.Detail))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"— {primary.Detail}");
            }

            if (primary.IsUnlocked == true)
                ImGui.TextDisabled("Unlocked activity");
            else if (primary.IsUnlocked == false)
                ImGui.TextDisabled("Activity is not unlocked on this character.");

            if (result.Sources.Count > 1)
                ImGui.TextDisabled($"+ {result.Sources.Count - 1} other known source(s)");
        }
        else
        {
            ImGui.TextUnformatted("Source:");
            ImGui.SameLine();
            ImGui.TextDisabled("No source resolved yet");
        }

        if (result.Guide is null)
            DrawSourceActionGuidance(result);

        var resolutionTier = resolver.GetResolutionTier(result.Target);
        ImGui.TextDisabled($"Resolution quality: {FriendlyResolutionTier(resolutionTier)}");
        ImGui.TextDisabled($"Source specificity: {resolver.GetSourceSpecificity(result.Target)}");
        ImGui.TextDisabled($"Availability: {resolver.GetAvailabilityState(result.Target)}");

        if (result.Guide is not null)
        {
            var status = BuildGuideStatus(result.Guide);

            ImGui.Spacing();

            if (status.NextStep is null)
            {
                ImGui.TextUnformatted("Current Step:");
                ImGui.SameLine();
                ImGui.TextUnformatted("Complete");

                ImGui.TextUnformatted("Remaining Quests:");
                ImGui.SameLine();
                ImGui.TextUnformatted("0");
            }
            else
            {
                ImGui.TextUnformatted(status.IsActive ? "Current quest:" : "Next quest:");
                ImGui.SameLine();
                ImGui.TextUnformatted(status.NextStep.QuestName);

                ImGui.TextUnformatted("Remaining Quests:");
                ImGui.SameLine();
                ImGui.TextUnformatted(status.Remaining.ToString());

                ImGui.TextUnformatted(status.IsActive ? "Quest started by:" : "Go to:");
                ImGui.SameLine();
                ImGui.TextUnformatted(
                    $"{status.NextStep.QuestGiver} — {status.NextStep.Zone} " +
                    $"(X: {status.NextStep.X:0.0}, Y: {status.NextStep.Y:0.0})");
            }
        }
    }

    private void DrawDetailed(AcquisitionResult result)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted($"Item ID: {result.Target.ItemId}");
        ImGui.TextUnformatted($"Category: {result.Target.Category}");

        if (!string.IsNullOrWhiteSpace(result.Target.GameUiCategory))
            ImGui.TextUnformatted($"Game UI Category: {result.Target.GameUiCategory}");

        if (TargetCatalog.IsGearCategory(result.Target.Category) &&
            result.Target.EquipJobs.Count > 0)
        {
            ImGui.TextWrapped(
                $"Equippable by: {string.Join(", ", result.Target.EquipJobs.OrderBy(x => x))}");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Known Sources:");

        if (result.Sources.Count == 0)
        {
            ImGui.TextDisabled("No trusted source provider resolved this item yet.");
        }
        else
        {
            foreach (var source in result.Sources)
            {
                var suffix = source.IsUnlocked switch
                {
                    true => " — Unlocked",
                    false => " — Locked",
                    null => "",
                };

                var detail = string.IsNullOrWhiteSpace(source.Detail)
                    ? ""
                    : $" — {source.Detail}";

                ImGui.BulletText(
                    $"{source.Kind}: {source.Name}{detail}{suffix}");
            }
        }

        if (result.Guide is not null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawGuide(result.Guide);
        }
    }

    private void DrawGuide(CuratedGuide guide)
    {
        if (guide.Chains.Count == 0)
            return;

        var chainIndex = Math.Clamp(
            config.SelectedChainIndex,
            0,
            guide.Chains.Count - 1);

        var names = guide.Chains.Select(c => c.Name).ToArray();

        if (ImGui.Combo("Prerequisite Chain", ref chainIndex, names, names.Length))
        {
            config.SelectedChainIndex = chainIndex;
            SaveConfig();
        }

        var chain = guide.Chains[chainIndex];

        ImGui.TextWrapped(chain.Description);
        ImGui.Spacing();

        var sequence = questGuidance.ResolveSequence(
            chain.Steps.Select(step => step.QuestName),
            GetQuestStateByRowId);

        var completed = sequence.Entries.Count(x => x.EffectiveState == QuestProgressState.Completed);
        ImGui.TextUnformatted(
            $"Chain Progress: {completed} / {chain.Steps.Count} quests complete");

        if (sequence.CurrentOrNext is { } actionable)
        {
            var currentStep = chain.Steps.FirstOrDefault(x =>
                x.QuestName.Equals(actionable.Name, StringComparison.OrdinalIgnoreCase));
            if (currentStep is not null)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted(sequence.Active is not null ? "Current quest:" : "Next quest:");
                ImGui.SameLine();
                ImGui.TextUnformatted(currentStep.QuestName);
                if (sequence.Active is null)
                    ImGui.TextWrapped($"Go to: {currentStep.QuestGiver} — {currentStep.Zone} (X: {currentStep.X:0.0}, Y: {currentStep.Y:0.0})");
                else
                    ImGui.TextDisabled($"Quest started by: {currentStep.QuestGiver} — {currentStep.Zone}");
            }
        }

        ImGui.Spacing();

        for (var i = 0; i < chain.Steps.Count; i++)
        {
            var step = chain.Steps[i];
            var state = i < sequence.Entries.Count
                ? sequence.Entries[i].EffectiveState
                : QuestProgressState.NotStarted;
            var marker = state == QuestProgressState.Completed ? "[X]" :
                state == QuestProgressState.InProgress ? "[>]" : "[ ]";

            ImGui.TextUnformatted($"{marker} {i + 1}. {step.QuestName}");

            if (state != QuestProgressState.Completed)
            {
                ImGui.Indent();
                ImGui.TextDisabled(
                    $"{step.QuestGiver} — {step.Zone} " +
                    $"(X: {step.X:0.0}, Y: {step.Y:0.0})");

                if (!string.IsNullOrWhiteSpace(step.Note))
                    ImGui.TextWrapped(step.Note);

                ImGui.Unindent();
            }
        }
    }

    private GuideStatus BuildGuideStatus(CuratedGuide guide)
    {
        if (guide.Chains.Count == 0)
            return new GuideStatus(null, false, 0);

        var chainIndex = Math.Clamp(config.SelectedChainIndex, 0, guide.Chains.Count - 1);
        var chain = guide.Chains[chainIndex];
        var sequence = questGuidance.ResolveSequence(
            chain.Steps.Select(step => step.QuestName),
            GetQuestStateByRowId);
        var actionable = sequence.CurrentOrNext is null
            ? null
            : chain.Steps.FirstOrDefault(x =>
                x.QuestName.Equals(sequence.CurrentOrNext.Name, StringComparison.OrdinalIgnoreCase));
        var remaining = sequence.Entries.Count(x => x.EffectiveState != QuestProgressState.Completed);
        return new GuideStatus(actionable, sequence.Active is not null, remaining);
    }

    private void BuildQuestCache()
    {
        foreach (var quest in DataManager.GetExcelSheet<Quest>())
        {
            var name = quest.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            questRowIds.TryAdd(name, quest.RowId);
            if (!questRowIdsByName.TryGetValue(name, out var ids))
            {
                ids = [];
                questRowIdsByName[name] = ids;
            }
            ids.Add(quest.RowId);
        }
    }

    private static unsafe QuestProgressState GetQuestStateByRowId(uint rowId)
    {
        var uiState = UIState.Instance();
        if (uiState == null || rowId == 0)
            return QuestProgressState.NotStarted;

        var questUnlockLink = 0x10000u + rowId;
        if (uiState->IsUnlockLinkUnlockedOrQuestCompleted(questUnlockLink))
            return QuestProgressState.Completed;

        // Accepted quests can still be at sequence 0 immediately after pickup, so
        // checking only minimum progression misses exactly the state a quest
        // tracker needs. Ask QuestManager directly first, then retain the sequence
        // check as a fallback for live progression.
        var questManager = QuestManager.Instance();
        if (questManager != null && rowId <= ushort.MaxValue &&
            questManager->IsQuestAccepted((ushort)rowId))
            return QuestProgressState.InProgress;

        if (uiState->IsUnlockLinkUnlockedOrQuestCompleted(questUnlockLink, 1))
            return QuestProgressState.InProgress;

        return QuestProgressState.NotStarted;
    }

    private static bool IsQuestCompleteByRowId(uint rowId) =>
        GetQuestStateByRowId(rowId) == QuestProgressState.Completed;

    private QuestProgressState GetQuestState(string questName)
    {
        if (!questRowIdsByName.TryGetValue(questName, out var rowIds) || rowIds.Count == 0)
            return QuestProgressState.NotStarted;

        var best = QuestProgressState.NotStarted;
        foreach (var rowId in rowIds)
        {
            var state = GetQuestStateByRowId(rowId);
            if (state == QuestProgressState.Completed)
                return state;
            if (state == QuestProgressState.InProgress)
                best = state;
        }
        return best;
    }

    private bool IsQuestComplete(string questName) =>
        GetQuestState(questName) == QuestProgressState.Completed;

    private QuestSequenceStatus ResolveJobQuestSequence(IReadOnlyList<JobQuestStep> steps) =>
        questGuidance.ResolveQuestIds(steps.Select(x => x.QuestId), GetQuestStateByRowId);

    private QuestSequenceStatus ResolveSourceQuestSequence(SourceEntry source) =>
        questGuidance.ResolveSequence(
            QuestGuidanceIndex.ExtractQuestNames(source),
            GetQuestStateByRowId);

    private static string FormatWhoWhere(QuestGuidanceInfo? info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.QuestGiver))
            return string.Empty;

        return string.IsNullOrWhiteSpace(info.Location)
            ? info.QuestGiver!
            : $"{info.QuestGiver} — {info.Location}";
    }

    private void DrawSourceActionGuidance(AcquisitionResult result)
    {
        var guidance = actionGuidance.Resolve(result);
        if (guidance.IsEmpty)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted($"{guidance.ActionLabel}:");
        ImGui.SameLine();
        ImGui.TextUnformatted(guidance.ActionText);

        if (!string.IsNullOrWhiteSpace(guidance.GoTo))
        {
            ImGui.TextUnformatted("Go to:");
            ImGui.SameLine();
            ImGui.TextUnformatted(guidance.GoTo);
        }
        else if (!string.IsNullOrWhiteSpace(guidance.QuestStartedBy))
        {
            ImGui.TextDisabled($"Quest started by: {guidance.QuestStartedBy}");
        }
    }

    private void RepairSelection()
    {
        if (!string.IsNullOrWhiteSpace(config.SelectedSetId) &&
            setCatalog.Get(config.SelectedSetId) is null)
        {
            config.SelectedSetId = string.Empty;
        }

        var categories = catalog.Categories;
        if (categories.Count == 0)
            return;

        if (!categories.Contains(config.SelectedCategory, StringComparer.OrdinalIgnoreCase))
            config.SelectedCategory = categories.Contains("Weapons") ? "Weapons" : categories[0];

        if (!catalog.Jobs.Contains(config.SelectedJob, StringComparer.OrdinalIgnoreCase))
            config.SelectedJob = "All";

        var validTargets = catalog.GetTargets(config.SelectedCategory, config.SelectedJob);
        var selected = catalog.GetById(config.SelectedItemId);

        if (selected is null || !validTargets.Any(t => t.ItemId == selected.ItemId))
            SelectFirstTarget();

        var progressionCategories = jobCatalog.Categories;
        if (progressionCategories.Count > 0)
        {
            if (!progressionCategories.Contains(
                    config.SelectedProgressionCategory,
                    StringComparer.OrdinalIgnoreCase))
            {
                config.SelectedProgressionCategory = progressionCategories[0];
                config.SelectedClassJobId = 0;
            }

            var progressionJobs = jobCatalog.GetByCategory(config.SelectedProgressionCategory);
            if (progressionJobs.Count > 0 &&
                !progressionJobs.Any(j => j.ClassJobId == config.SelectedClassJobId))
            {
                config.SelectedClassJobId = progressionJobs[0].ClassJobId;
            }
        }
    }

    private void SelectFirstTarget()
    {
        config.SelectedItemId =
            catalog.GetTargets(config.SelectedCategory, config.SelectedJob)
                .FirstOrDefault()
                ?.ItemId ?? 0;
    }

    private static int IndexOf(IReadOnlyList<string> values, string target)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], target, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private void SaveConfig()
    {
        if (configPersistenceDisabled)
            return;

        try
        {
            PluginInterface.SavePluginConfig(config);
        }
        catch (Exception ex)
        {
            releaseDiagnosticsStatus = "Configuration save failed. Changes may not persist; see /xllog.";
            Log.Error(ex, "CQG configuration save failed.");
        }
    }

    private enum MainTab
    {
        Items,
        Jobs,
        Pins,
        Help,
    }

    private sealed record GuideStatus(
        GuideStep? NextStep,
        bool IsActive,
        int Remaining);
}
