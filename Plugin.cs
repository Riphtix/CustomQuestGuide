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

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/cqg";
    private static readonly Vector4 OwnedGreen = new(0.35f, 1.00f, 0.45f, 1.00f);
    private static readonly Vector4 PartialBlue = new(0.35f, 0.70f, 1.00f, 1.00f);
    private static readonly Vector4 GlamourPink = new(1.00f, 0.62f, 0.78f, 1.00f);
    private static readonly Vector4 UnresolvedRed = new(1.00f, 0.30f, 0.30f, 1.00f);

    // Keep primary controls visually stable as the window grows. Controls may
    // shrink only when the window itself becomes narrower than their preferred
    // width, but they never stretch to fill arbitrary remaining space.
    private const float TargetComboWidth = 520f;
    private const float StandardComboWidth = 320f;
    private const float JobComboWidth = 380f;
    private const float ChainComboWidth = 440f;
    private const float ProgressBarWidth = 420f;
    private const float PinnedActionButtonWidth = 68f;

    // Some quest/NPC destinations are interior territories whose own map cannot
    // accept a normal flag. Route those names to a useful overworld entrance
    // instead of presenting a dead map button. Keys are the displayed interior
    // PlaceName; values are standard zone map-link targets.
    private static readonly Dictionary<string, (string Zone, float X, float Y)> InteriorEntranceFallbacks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ARR / common inn rooms
            ["The Waking Sands"] = ("Western Thanalan", 11.6f, 13.8f),
            ["The Rising Stones"] = ("Mor Dhona", 21.9f, 8.8f),
            ["Dawn's Respite"] = ("Mor Dhona", 21.9f, 8.8f),
            ["The Roost"] = ("New Gridania", 12.0f, 13.0f),
            ["Mizzenmast Inn"] = ("Limsa Lominsa Upper Decks", 11.0f, 11.0f),
            ["The Hourglass"] = ("Ul'dah - Steps of Nald", 11.5f, 9.0f),

            // Heavensward
            ["Fortemps Manor"] = ("Foundation", 10.2f, 11.5f),
            ["Cloud Nine"] = ("Foundation", 13.0f, 12.0f),

            // Stormblood
            ["The House of the Fierce"] = ("Yanxia", 25.3f, 12.7f),
            ["Kienkan"] = ("The Doman Enclave", 6.6f, 7.1f),
            ["Bokairo Inn"] = ("Kugane", 9.5f, 7.0f),

            // Shadowbringers
            ["The Ocular"] = ("The Crystarium", 13.5f, 11.3f),
            ["The Pendants"] = ("The Crystarium", 12.5f, 16.2f),

            // Endwalker / post-Endwalker
            ["The Baldesion Annex"] = ("Old Sharlayan", 9.2f, 11.2f),
            ["Andron"] = ("Old Sharlayan", 9.2f, 11.2f),
            ["The Andron"] = ("Old Sharlayan", 9.2f, 11.2f),
            ["The Omphalos"] = ("Mor Dhona", 24.5f, 12.7f),
            ["Meghaduta Guest Chambers"] = ("Radz-at-Han", 4.2f, 9.6f),
            ["Estinien's Chambers"] = ("Radz-at-Han", 4.2f, 9.6f),

            // Dawntrail / post-Dawntrail
            ["The For'ard Cabins"] = ("Tuliyollal", 10.0f, 15.1f),
            ["The Backroom"] = ("Solution Nine", 11.0f, 3.2f),
            ["Break Room"] = ("Solution Nine", 11.0f, 3.2f),
            ["Throne Room"] = ("Solution Nine", 4.3f, 19.0f),
        };

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerStateService { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
#pragma warning disable Dalamud001
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;
#pragma warning restore Dalamud001

    private readonly Configuration config;
    private readonly NetworkRequestTracker networkRequests;
    private readonly SupplementalCsvIndex supplemental;
    private readonly CollectibleSourceIndex collectibleSources;
    private readonly GarlandSourceIndex garlandSources;
    private readonly GarlandDutyUnlockIndex garlandDutyUnlocks;
    private readonly CommunityDutyUnlockIndex communityDutyUnlocks;
    private readonly TargetCatalog catalog;
    private readonly EmoteCatalog emoteCatalog;
    private readonly TitleCatalog titleCatalog;
    private readonly SharedFateProgressIndex sharedFates;
    private readonly SetCatalog setCatalog;
    private readonly JobProgressionCatalog jobCatalog;
    private readonly DutyCatalog dutyCatalog;
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

    // Ownership scanning is command-triggered, one-shot, and incremental. /cqg starts
    // exactly one full pass. Each queued permanent item and duty is checked at most
    // once in that pass; transient inventory/job sources are snapshotted once at the
    // beginning. Nothing continues checking after the queue completes.
    // One-shot ownership work advances every framework update, but each slice is
    // bounded by the OwnershipIndex time budget. There is no idle/session polling.
    private const int PermanentUnlockChecksPerPoll = 2048;
    private bool ownershipPollingActive;
    private bool ownershipTransientSnapshotCaptured;
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

        networkRequests = new NetworkRequestTracker();
        supplemental = new SupplementalCsvIndex(DataManager, Log);
        collectibleSources = new CollectibleSourceIndex(Log, networkRequests);
        garlandSources = new GarlandSourceIndex(
            PluginInterface.GetPluginConfigDirectory(),
            Log,
            networkRequests);
        garlandDutyUnlocks = new GarlandDutyUnlockIndex(
            PluginInterface.GetPluginConfigDirectory(),
            Log,
            networkRequests);
        communityDutyUnlocks = new CommunityDutyUnlockIndex(
            PluginInterface.GetPluginConfigDirectory(),
            Log,
            networkRequests);
        catalog = new TargetCatalog(DataManager, supplemental);
        emoteCatalog = new EmoteCatalog(DataManager, Log);
        titleCatalog = new TitleCatalog(DataManager, Log);
        sharedFates = new SharedFateProgressIndex(Log);

        collectibleSources.BindTargets(catalog.Targets);
        // Network enrichment is deliberately not refreshed at plugin startup.
        // CQG's normal ownership scan is local-only; community HTTP is manual
        // or target-scoped on demand.

        setCatalog = new SetCatalog(catalog);
        jobCatalog = new JobProgressionCatalog(DataManager, Log);
        dutyCatalog = new DutyCatalog(DataManager, Log);
        questGuidance = new QuestGuidanceIndex(DataManager, Log);
        ownership = new OwnershipIndex(DataManager, GameInventory, PlayerStateService, UnlockState, config, SaveConfig, Log);
        npcLocations = new NpcLocationIndex(DataManager, Log);
        dutyUnlocks = new DutyUnlockGuidanceIndex(DataManager, questGuidance, communityDutyUnlocks, garlandDutyUnlocks, Log);
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
            HelpMessage = "Open Custom Quest Guide. Optionally pass an item, unlock, job, or duty name (or item ID) to search/select a target.",
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
        garlandDutyUnlocks.Dispose();
        communityDutyUnlocks.Dispose();
    }

    private void OnLogin()
    {
        PrintStartupMessage();
    }

    private IEnumerable<uint> BuildSelectedOwnershipItemIds()
    {
        var prioritized = new List<uint>();

        var selectedSet = setCatalog.Get(config.SelectedSetId);
        if (selectedSet is not null)
            prioritized.AddRange(selectedSet.ItemIds);

        if (config.SelectedItemId != 0)
            prioritized.Add(config.SelectedItemId);

        return prioritized
            .Where(id => id != 0)
            .Distinct();
    }

    private IEnumerable<uint> BuildPinnedOwnershipItemIds()
    {
        var prioritized = new List<uint>();

        foreach (var pin in config.PinnedTargets)
        {
            if (pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase) && pin.NumericId != 0)
            {
                prioritized.Add(pin.NumericId);
                continue;
            }

            if (!pin.Kind.Equals("Set", StringComparison.OrdinalIgnoreCase))
                continue;

            var set = setCatalog.Get(pin.StringId);
            if (set is null)
                continue;

            prioritized.AddRange(set.ItemIds);
        }

        return prioritized
            .Where(id => id != 0)
            .Distinct();
    }

    private void StartOwnershipPolling()
    {
        ownership.BeginPolling(
            BuildSelectedOwnershipItemIds(),
            BuildPinnedOwnershipItemIds(),
            ownershipItemIds);
        dutyUnlocks.BeginPolling();
        emoteCatalog.BeginPolling();
        titleCatalog.BeginPolling();

        ownershipPollingActive = true;
        // Do not perform ownership work on the slash-command frame itself. The
        // first framework update after the command captures transient sources and
        // begins the bounded one-shot permanent scan.
        ownershipTransientSnapshotCaptured = false;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn || !ownershipPollingActive)
            return;

        var refreshTransientSources = !ownershipTransientSnapshotCaptured;

        try
        {
            ownership.PollSnapshotStep(ownershipJobIds, PermanentUnlockChecksPerPoll, refreshTransientSources);
            if (refreshTransientSources)
            {
                sharedFates.CaptureSnapshot();
                ownershipTransientSnapshotCaptured = true;
            }
            dutyUnlocks.PollSnapshotStep(256);
            emoteCatalog.PollSnapshotStep();
            titleCatalog.PollSnapshotStep();

            if (ownership.PermanentPollingComplete && dutyUnlocks.PollingComplete && emoteCatalog.PollingComplete && titleCatalog.PollingComplete)
            {
                ownership.FinishPolling();
                ownershipPollingActive = false;
            }
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
        ChatGui.Print("[Custom Quest Guide] /cqg opens the guide and starts one one-shot ownership scan in the background. Each queued item, permanent unlock, and duty is checked once, then CQG stops checking until /cqg is run again. See Help for details.");
    }

    private void OnCommand(string command, string args)
    {
        // The slash command starts one complete background ownership refresh. The
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

        if (exact is not null)
        {
            SelectItemTarget(exact);
            return;
        }

        var exactEmote = emoteCatalog.Emotes.FirstOrDefault(e =>
            e.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            e.Command.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exactEmote is not null)
        {
            SelectEmoteTarget(exactEmote);
            return;
        }

        var exactTitle = titleCatalog.Titles.FirstOrDefault(t =>
            t.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exactTitle is not null)
        {
            SelectTitleTarget(exactTitle);
            return;
        }

        var exactJob = jobCatalog.Jobs.FirstOrDefault(x =>
            x.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            x.Abbreviation.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exactJob is not null)
        {
            SelectJobTarget(exactJob);
            return;
        }

        var exactDuty = dutyCatalog.GetByName(query);
        if (exactDuty is not null)
        {
            SelectDutyTarget(exactDuty);
            return;
        }

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
        if (activeTab == MainTab.Items)
            DrawItemsNavigation();
        else if (activeTab is MainTab.Jobs or MainTab.Duties)
            DrawUnifiedTargetSelector();
        ImGui.Separator();

        switch (activeTab)
        {
            case MainTab.Jobs:
                DrawJobsAndProgressionTab();
                break;
            case MainTab.Duties:
                DrawDutiesTab();
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
        if (ImGui.Button(activeTab == MainTab.Duties ? "[ Duties ]" : "Duties"))
            activeTab = MainTab.Duties;

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
        ImGui.BulletText("/cqg <target name or item ID> searches items, permanent unlocks, jobs, and duties; sets are chosen from the separate Set dropdown on the Items tab.");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, UnresolvedRed);
        ImGui.TextWrapped("IMPORTANT: CQG starts one complete one-shot ownership, permanent-unlock, and duty-state scan when you use /cqg. The scan advances in small frame-budgeted slices until complete, then CQG performs no further ownership checks until /cqg is run again. Selecting a target during an active scan moves that target and related remaining checks to the front of the queue.");
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
        ImGui.BulletText("Unlocked duties are shown in green anywhere CQG exposes them as a dependency or direct target.");
        ImGui.BulletText("Locked duties can be pinned directly from the Duties tab or from an item/source that depends on them.");

        ImGui.Spacing();
        ImGui.TextWrapped("How do I place a map flag from CQG?");
        ImGui.BulletText("When CQG has an authoritative NPC placement or explicit map coordinates, the displayed location becomes clickable.");
        ImGui.BulletText("Click the location name to open the in-game map and place a flag at that location.");

        ImGui.Spacing();
        ImGui.TextWrapped("How does the Target selector work?");
        ImGui.BulletText("The Target control combines searching and selecting for items, permanent unlocks, jobs, and duties. Sets use the separate Set dropdown on the Items tab.");
        ImGui.BulletText("With an empty search it acts as a browser for the current tab/category; typing searches across all supported target types.");

        ImGui.Spacing();
        ImGui.TextWrapped("What new unlock targets can CQG track?");
        ImGui.BulletText("Emotes and titles are first-class targets. Emotes can resolve through their teaching item or direct quest reward, while titles link back to their achievement.");
        ImGui.BulletText("Hairstyles, fashion accessories, facewear, chocobo barding, Framer's Kits, and riding maps are exposed as dedicated item-backed categories.");
        ImGui.BulletText("Riding Map sets are grouped by expansion. Endwalker and Dawntrail normal-zone maps can also show Shared FATE Rank 2 progress when the client exposes the zone counter.");
        ImGui.BulletText("Progress bars are shown only for measurable requirements or collection progress; exact current/required values remain visible on the bar.");

        ImGui.Spacing();
        ImGui.TextWrapped("Why is a completion-reward mount not the next set target?");
        ImGui.BulletText("Collection rewards are held behind their prerequisite set. CQG prioritizes actionable missing members first, then promotes the final reward when its prerequisites are complete.");

        ImGui.Spacing();
        ImGui.TextWrapped("Why does an owned item still look incomplete?");
        ImGui.BulletText("CQG checks ownership only during the one-shot background scan started by /cqg. Each queued check runs once; when the scan finishes, CQG keeps using that snapshot until /cqg is run again.");
        ImGui.BulletText("CQG can only use ownership sources exposed by the client or previously snapshotted while authoritative data was available.");
        ImGui.BulletText("For Glamour Dresser items, the persistent per-character dresser snapshot is still used. Opening the dresser updates that saved cache when authoritative dresser data is available.");

        ImGui.Spacing();
        ImGui.TextWrapped("What does Refresh acquisition sources do?");
        ImGui.BulletText("It refreshes external acquisition enrichment used when live game-sheet data is insufficient. Core game-data resolution and saved ownership snapshots remain local.");
        ImGui.BulletText("Manual source refreshes have a 5-minute cooldown. They refresh the compact collectible indexes only; Garland item documents are requested one selected target at a time.");
        ImGui.BulletText("If an external service is unavailable, CQG keeps usable cached/session data where possible and degrades gracefully rather than wiping working guidance.");

        ImGui.Spacing();
        ImGui.TextWrapped("How do pins work?");
        ImGui.BulletText("Items, sets, duties, job unlocks, and job progression can be pinned from their respective views.");
        ImGui.BulletText("Pinned sets use the same smart frontier as the main set view, so their next action should follow the best currently actionable missing member.");

        ImGui.Spacing();
        ImGui.TextWrapped("Does CQG continuously monitor my character?");
        ImGui.BulletText("No. Ownership checking exists only while the one-shot scan started by /cqg is running. After the queue finishes, there are no ownership checks until /cqg is run again.");
        ImGui.BulletText("External acquisition-source refreshes are separate from ownership polling and are cached/rate-limited.");

        ImGui.Spacing();
        ImGui.TextWrapped("How can I diagnose bad guidance?");
        ImGui.BulletText("Enable Detailed, then Debug, on the relevant target. The debug panel exposes ownership, resolver, source, progression, and polling timing information used by CQG.");
        ImGui.BulletText("Export resolver audit CSV includes source provenance, next-action data, ownership state, and contradiction checks.");
        ImGui.BulletText("Debug exports intentionally avoid account credentials, auth tokens, and machine-secret data.");

        ImGui.Spacing();
        ImGui.TextDisabled("Custom Quest Guide 1.2.0");
    }

    private static void DrawHelpColorRow(Vector4 color, string label, string description)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextWrapped($"— {description}");
    }

    private static float GetControlWidth(float preferredWidth)
    {
        var available = ImGui.GetContentRegionAvail().X;
        if (available <= 0f)
            return preferredWidth;
        return Math.Max(160f, Math.Min(preferredWidth, available));
    }

    private static void SetNextControlWidth(float preferredWidth) =>
        ImGui.SetNextItemWidth(GetControlWidth(preferredWidth));

    private void DrawUnifiedTargetSelector()
    {
        var preview = GetUnifiedTargetPreview();
        SetNextControlWidth(TargetComboWidth);
        if (!ImGui.BeginCombo("Target##UnifiedTarget", preview))
            return;

        SetNextControlWidth(TargetComboWidth - 24f);
        ImGui.InputTextWithHint(
            "##UnifiedTargetSearch",
            "Search items, unlocks, jobs, or duties...",
            ref targetSearch,
            128);
        ImGui.Separator();

        var query = targetSearch.Trim();
        var shown = 0;

        if (query.Length == 0)
        {
            DrawUnifiedCurrentContextTargets(ref shown);
        }
        else
        {
            foreach (var job in jobCatalog.Jobs
                         .Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     x.Abbreviation.Contains(query, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => SearchRank(x.Name, query))
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(80))
            {
                var visualState = GetJobVisualState(job);
                if (visualState != JobVisualState.Locked)
                    ImGui.PushStyleColor(ImGuiCol.Text, visualState == JobVisualState.Complete ? OwnedGreen : PartialBlue);
                if (ImGui.Selectable($"[Job] {job.Name} ({job.Abbreviation})##job-{job.ClassJobId}"))
                {
                    SelectJobTarget(job);
                    ImGui.CloseCurrentPopup();
                }
                if (visualState != JobVisualState.Locked)
                    ImGui.PopStyleColor();
                shown++;
            }

            foreach (var duty in dutyCatalog.Duties
                         .Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     x.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => SearchRank(x.Name, query))
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(120))
            {
                var unlocked = dutyUnlocks.IsDutyUnlocked(duty.Name) == true;
                if (unlocked)
                    ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                if (ImGui.Selectable($"[Duty] {duty.Name}##duty-{duty.ContentFinderConditionId}"))
                {
                    SelectDutyTarget(duty);
                    ImGui.CloseCurrentPopup();
                }
                if (unlocked)
                    ImGui.PopStyleColor();
                shown++;
            }

            foreach (var emote in emoteCatalog.Emotes
                         .Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     x.Command.Contains(query, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => SearchRank(x.Name, query))
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(100))
            {
                var unlocked = emoteCatalog.IsUnlocked(emote.EmoteId) == true;
                if (unlocked)
                    ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                var commandSuffix = string.IsNullOrWhiteSpace(emote.Command) ? string.Empty : $" ({emote.Command})";
                if (ImGui.Selectable($"[Emote] {emote.Name}{commandSuffix}##emote-{emote.EmoteId}"))
                {
                    SelectEmoteTarget(emote);
                    ImGui.CloseCurrentPopup();
                }
                if (unlocked)
                    ImGui.PopStyleColor();
                shown++;
            }

            foreach (var title in titleCatalog.Titles
                         .Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     x.AchievementName.Contains(query, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => SearchRank(x.Name, query))
                         .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(80))
            {
                var unlocked = titleCatalog.IsUnlocked(title.TitleId) == true;
                if (unlocked)
                    ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                if (ImGui.Selectable($"[Title] {title.Name}##title-{title.TitleId}"))
                {
                    SelectTitleTarget(title);
                    ImGui.CloseCurrentPopup();
                }
                if (unlocked)
                    ImGui.PopStyleColor();
                shown++;
            }

            foreach (var target in GetGlobalSearchResults(query).Take(240))
            {
                var ownedStatus = ownership.GetItemStatus(target.ItemId);
                var resolved = resolver.HasDirectSource(target);
                if (ownedStatus.IsOwned)
                    ImGui.PushStyleColor(ImGuiCol.Text, IsGlamourDresserOwnership(ownedStatus) ? GlamourPink : OwnedGreen);
                else if (!resolved)
                    ImGui.PushStyleColor(ImGuiCol.Text, UnresolvedRed);

                if (ImGui.Selectable($"[Item] {target.Name}##item-{target.ItemId}"))
                {
                    SelectItemTarget(target);
                    ImGui.CloseCurrentPopup();
                }

                if (ownedStatus.IsOwned || !resolved)
                    ImGui.PopStyleColor();
                shown++;
            }
        }

        if (shown == 0)
            ImGui.TextDisabled(query.Length == 0 ? "No targets are available in this view." : "No targets match that search.");

        ImGui.EndCombo();
    }

    private void DrawUnifiedCurrentContextTargets(ref int shown)
    {
        switch (activeTab)
        {
            case MainTab.Items:
            {
                if (config.SelectedCategory.Equals("Emotes", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var emote in emoteCatalog.Emotes.Take(300))
                    {
                        var unlocked = emoteCatalog.IsUnlocked(emote.EmoteId) == true;
                        if (unlocked)
                            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                        var commandSuffix = string.IsNullOrWhiteSpace(emote.Command) ? string.Empty : $" ({emote.Command})";
                        if (ImGui.Selectable($"[Emote] {emote.Name}{commandSuffix}##emote-current-{emote.EmoteId}"))
                        {
                            SelectEmoteTarget(emote);
                            ImGui.CloseCurrentPopup();
                        }
                        if (unlocked)
                            ImGui.PopStyleColor();
                        shown++;
                    }
                    break;
                }

                if (config.SelectedCategory.Equals("Titles", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var title in titleCatalog.Titles.Take(300))
                    {
                        var unlocked = titleCatalog.IsUnlocked(title.TitleId) == true;
                        if (unlocked)
                            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                        if (ImGui.Selectable($"[Title] {title.Name}##title-current-{title.TitleId}"))
                        {
                            SelectTitleTarget(title);
                            ImGui.CloseCurrentPopup();
                        }
                        if (unlocked)
                            ImGui.PopStyleColor();
                        shown++;
                    }
                    break;
                }

                foreach (var target in catalog.GetTargets(config.SelectedCategory, config.SelectedJob).Take(300))
                {
                    var ownedStatus = ownership.GetItemStatus(target.ItemId);
                    var resolved = resolver.HasDirectSource(target);
                    if (ownedStatus.IsOwned)
                        ImGui.PushStyleColor(ImGuiCol.Text, IsGlamourDresserOwnership(ownedStatus) ? GlamourPink : OwnedGreen);
                    else if (!resolved)
                        ImGui.PushStyleColor(ImGuiCol.Text, UnresolvedRed);
                    if (ImGui.Selectable($"[Item] {target.Name}##item-current-{target.ItemId}"))
                    {
                        SelectItemTarget(target);
                        ImGui.CloseCurrentPopup();
                    }
                    if (ownedStatus.IsOwned || !resolved)
                        ImGui.PopStyleColor();
                    shown++;
                }
                break;
            }
            case MainTab.Jobs:
                foreach (var job in jobCatalog.GetByCategory(config.SelectedProgressionCategory))
                {
                    var state = GetJobVisualState(job);
                    if (state != JobVisualState.Locked)
                        ImGui.PushStyleColor(ImGuiCol.Text, state == JobVisualState.Complete ? OwnedGreen : PartialBlue);
                    if (ImGui.Selectable($"[Job] {job.Name} ({job.Abbreviation})##job-current-{job.ClassJobId}"))
                    {
                        SelectJobTarget(job);
                        ImGui.CloseCurrentPopup();
                    }
                    if (state != JobVisualState.Locked)
                        ImGui.PopStyleColor();
                    shown++;
                }
                break;
            case MainTab.Duties:
                foreach (var duty in dutyCatalog.GetByCategory(config.SelectedDutyCategory).Take(300))
                {
                    var unlocked = dutyUnlocks.IsDutyUnlocked(duty.Name) == true;
                    if (unlocked)
                        ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                    if (ImGui.Selectable($"[Duty] {duty.Name}##duty-current-{duty.ContentFinderConditionId}"))
                    {
                        SelectDutyTarget(duty);
                        ImGui.CloseCurrentPopup();
                    }
                    if (unlocked)
                        ImGui.PopStyleColor();
                    shown++;
                }
                break;
        }
    }

    private string GetUnifiedTargetPreview()
    {
        if (activeTab == MainTab.Duties && dutyCatalog.Get(config.SelectedDutyId) is { } duty)
            return $"[Duty] {duty.Name}";
        if (activeTab == MainTab.Jobs && jobCatalog.Get(config.SelectedClassJobId) is { } job)
            return $"[Job] {job.Name} ({job.Abbreviation})";
        if (activeTab == MainTab.Items && config.SelectedCategory.Equals("Emotes", StringComparison.OrdinalIgnoreCase) &&
            emoteCatalog.Get(config.SelectedEmoteId) is { } emote)
            return $"[Emote] {emote.Name}{(string.IsNullOrWhiteSpace(emote.Command) ? string.Empty : $" ({emote.Command})")}";
        if (activeTab == MainTab.Items && config.SelectedCategory.Equals("Titles", StringComparison.OrdinalIgnoreCase) &&
            titleCatalog.Get(config.SelectedTitleId) is { } title)
            return $"[Title] {title.Name}";
        if (activeTab == MainTab.Items && catalog.GetById(config.SelectedItemId) is { } item)
            return $"[Item] {item.Name}";
        return "Search or select a target...";
    }

    private void DrawSetSelector()
    {
        var selected = setCatalog.Get(config.SelectedSetId);
        var preview = selected is null ? "No set selected" : selected.Name;

        SetNextControlWidth(TargetComboWidth);
        if (!ImGui.BeginCombo("Set##SeparateSetTarget", preview))
            return;

        if (ImGui.Selectable("No set / single target", selected is null))
        {
            config.SelectedSetId = string.Empty;
            config.SelectedRelicJob = string.Empty;
            SaveConfig();
            ImGui.CloseCurrentPopup();
        }

        string? currentGroup = null;
        foreach (var set in setCatalog.Sets
                     .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(currentGroup, set.Group, StringComparison.OrdinalIgnoreCase))
            {
                currentGroup = set.Group;
                ImGui.Separator();
                ImGui.TextUnformatted(currentGroup);
                ImGui.Separator();
            }

            if (ImGui.Selectable($"{set.Name}##set-separate-{set.Id}", selected?.Id == set.Id))
            {
                SelectSetTarget(set);
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.EndCombo();
    }

    private static int SearchRank(string value, string query)
    {
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    private void PromoteActiveScanTargets(
        IEnumerable<uint>? itemIds,
        IEnumerable<string>? dutyNames,
        string reason)
    {
        if (!ownershipPollingActive)
            return;

        if (itemIds is not null)
            ownership.PromoteCandidates(itemIds, reason);
        if (dutyNames is not null)
            dutyUnlocks.PromoteDuties(dutyNames);
    }

    private IEnumerable<string> GetRelatedDutyNames(TargetEntry target)
    {
        AcquisitionResult result;
        try
        {
            result = resolver.Resolve(target);
        }
        catch
        {
            yield break;
        }

        foreach (var source in result.Sources)
        {
            var dutyName = DutyUnlockGuidanceIndex.ExtractDutyName(source);
            if (!string.IsNullOrWhiteSpace(dutyName) &&
                dutyUnlocks.GetContentFinderConditionId(dutyName) != 0)
                yield return dutyName;
        }
    }

    private void RequestTargetScopedSourceEnrichment(TargetEntry target)
    {
        // Only a user-selected target may trigger Garland item enrichment.
        // Never bulk-query Garland from /cqg, startup, drawing, or background
        // ownership scanning. Positive results and misses are cached by the
        // index, so repeatedly selecting the same target does not spam HTTP.
        if (resolver.NeedsGarlandEnrichment(target))
            garlandSources.EnsureRequested(target.ItemId);

        // Exchange chains are part of the selected target's answer. If a cost
        // item/token has no actionable local/cached source, queue only that
        // dependency for the same paced, cached target-scoped enrichment.
        // This is bounded so a pathological shop row cannot fan out requests.
        foreach (var dependency in resolver.GetExchangeDependencies(target.ItemId).Take(4))
        {
            if (dependency.CostItemName.Equals("Gil", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!resolver.HasKnownActionableSourceForItemId(dependency.CostItemId) &&
                !garlandSources.IsChecked(dependency.CostItemId))
            {
                garlandSources.EnsureRequested(dependency.CostItemId);
            }
        }
    }

    private void SelectItemTarget(TargetEntry target)
    {
        config.SelectedSetId = string.Empty;
        config.SelectedCategory = target.Category;
        config.SelectedJob = "All";
        config.SelectedItemId = target.ItemId;
        config.SelectedChainIndex = 0;
        activeTab = MainTab.Items;
        SaveConfig();
        RequestTargetScopedSourceEnrichment(target);
        PromoteActiveScanTargets(
            new[] { target.ItemId },
            GetRelatedDutyNames(target),
            $"selected item: {target.Name}");
    }

    private void SelectSetTarget(TargetSet set)
    {
        config.SelectedSetId = set.Id;
        config.SelectedRelicJob = string.Empty;
        activeTab = MainTab.Items;
        SaveConfig();

        var members = set.ItemIds
            .Select(catalog.GetById)
            .Where(x => x is not null)
            .Cast<TargetEntry>()
            .ToArray();
        PromoteActiveScanTargets(
            set.ItemIds,
            members.SelectMany(GetRelatedDutyNames).Distinct(StringComparer.OrdinalIgnoreCase),
            $"selected set: {set.Name}");
    }

    private void SelectEmoteTarget(EmoteTarget emote)
    {
        config.SelectedSetId = string.Empty;
        config.SelectedCategory = "Emotes";
        config.SelectedItemId = 0;
        config.SelectedEmoteId = emote.EmoteId;
        config.SelectedTitleId = 0;
        activeTab = MainTab.Items;
        SaveConfig();

        if (emote.BackingItemId != 0 && catalog.GetById(emote.BackingItemId) is { } teachingItem)
        {
            RequestTargetScopedSourceEnrichment(teachingItem);
            PromoteActiveScanTargets(
                new[] { teachingItem.ItemId },
                GetRelatedDutyNames(teachingItem),
                $"selected emote: {emote.Name}");
        }
    }

    private void SelectTitleTarget(TitleTarget title)
    {
        config.SelectedSetId = string.Empty;
        config.SelectedCategory = "Titles";
        config.SelectedItemId = 0;
        config.SelectedEmoteId = 0;
        config.SelectedTitleId = title.TitleId;
        activeTab = MainTab.Items;
        SaveConfig();
    }

    private void SelectJobTarget(JobProgressionTarget job)
    {
        config.SelectedProgressionCategory = job.Category;
        config.SelectedClassJobId = job.ClassJobId;
        activeTab = MainTab.Jobs;
        SaveConfig();
    }

    private void SelectDutyTarget(DutyTarget duty)
    {
        config.SelectedDutyCategory = duty.Category;
        config.SelectedDutyId = duty.ContentFinderConditionId;
        activeTab = MainTab.Duties;
        SaveConfig();
        PromoteActiveScanTargets(
            null,
            new[] { duty.Name },
            $"selected duty: {duty.Name}");
    }

    private void DrawItemsTab()
    {
        DrawCommonTargetControls();

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

        var result = resolver.Resolve(selected);
        DrawTargetSummary(result);
        DrawRidingMapProgress(selected);

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
                var prefix = unlockComplete
                    ? "Unlocked via"
                    : unlockState == QuestProgressState.InProgress
                        ? "Quest started by"
                        : "Go to";
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
                var prefix = active ? "Quest started by" : "Go to";
                if (!string.IsNullOrWhiteSpace(nextStep.Location))
                    DrawNpcLocationLine(prefix, nextStep.QuestGiver!, nextStep.Location, $"job-next-{job.ClassJobId}-{nextStep.QuestId}");
                else
                    ImGui.TextDisabled($"{prefix}: {nextStep.QuestGiver}");
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
                var prefix = state == QuestProgressState.InProgress ? "Quest started by" : "Go to";
                if (!string.IsNullOrWhiteSpace(step.Location))
                    DrawNpcLocationLine(prefix, step.QuestGiver!, step.Location, $"job-step-{job.ClassJobId}-{step.QuestId}");
                else
                    ImGui.TextDisabled($"{prefix}: {step.QuestGiver}");
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
        ImGui.TextUnformatted(duty.Name.ToUpperInvariant());
        if (unlocked == true)
            ImGui.PopStyleColor();

        ImGui.SameLine();
        DrawPinToggle("Duty", duty.ContentFinderConditionId, string.Empty, "Pin Duty", "Unpin Duty");

        ImGui.TextDisabled(duty.Category + (duty.RequiredLevel > 0 ? $" — Lv. {duty.RequiredLevel}" : string.Empty));
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

    private void DrawPinnedTargetsTab()
    {
        ImGui.TextWrapped(
            "Unified priority queue for item, emote, title, set, duty, job-unlock, and formal job-progression targets. " +
            "Priority is the list order.");
        ImGui.Spacing();

        if (config.PinnedTargets.Count == 0)
        {
            ImGui.TextDisabled("No targets are pinned yet. Pin items, emotes, titles, sets, duties, or class/job progression from their tabs.");
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
            else if (pin.Kind.Equals("Emote", StringComparison.OrdinalIgnoreCase))
            {
                DrawColoredText($"{i + 1}. {title}", pin.NumericId <= ushort.MaxValue && emoteCatalog.IsUnlocked((ushort)pin.NumericId) == true);
            }
            else if (pin.Kind.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                DrawColoredText($"{i + 1}. {title}", pin.NumericId <= ushort.MaxValue && titleCatalog.IsUnlocked((ushort)pin.NumericId) == true);
            }
            else
            {
                DrawColoredText($"{i + 1}. {title}", IsPinComplete(pin));
            }
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                ImGui.Indent();
                ImGui.TextWrapped(subtitle);
                DrawPinnedLocationLink(pin, i);
                ImGui.Unindent();
            }

            DrawPinnedProgress(pin);

            var pinButtonSize = new Vector2(PinnedActionButtonWidth, 0f);
            if (ImGui.Button("Open", pinButtonSize))
                OpenPinnedTarget(pin);

            ImGui.SameLine();
            var canMoveUp = i > 0;
            if (!canMoveUp)
                ImGui.BeginDisabled();
            var moveUp = ImGui.Button("Up", pinButtonSize);
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
            var moveDown = ImGui.Button("Down", pinButtonSize);
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
            var moveTop = ImGui.Button("Top", pinButtonSize);
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
            if (ImGui.Button("Remove", pinButtonSize))
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


    private void DrawPinnedProgress(PinnedTargetConfig pin)
    {
        switch (pin.Kind)
        {
            case "Set":
            {
                var set = setCatalog.Get(pin.StringId);
                if (set is null)
                    return;

                var members = set.ItemIds
                    .Select(catalog.GetById)
                    .Where(x => x is not null)
                    .Cast<TargetEntry>()
                    .Where(x => string.IsNullOrWhiteSpace(pin.Variant) || x.EquipJobs.Contains(pin.Variant))
                    .ToArray();
                if (members.Length == 0)
                    return;

                var owned = members.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned);
                ImGui.Indent();
                DrawProgressBar("Collection progress", owned, members.Length, "owned");
                ImGui.Unindent();
                break;
            }
            case "Item":
            {
                var item = catalog.GetById(pin.NumericId);
                if (item is null || ownership.GetItemStatus(item.ItemId).IsOwned ||
                    !item.Category.Equals("Riding Maps", StringComparison.OrdinalIgnoreCase))
                    return;

                ImGui.Indent();
                DrawSharedFateRequirement(item, compact: true);
                ImGui.Unindent();
                break;
            }
            case "JobProgression":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null || !ownership.IsJobUnlocked(job.ClassJobId) || job.Progression.Count == 0)
                    return;

                var sequence = ResolveJobQuestSequence(job.Progression);
                var completed = sequence.Entries.Count(x => x.EffectiveState == QuestProgressState.Completed);
                ImGui.Indent();
                DrawProgressBar("Quest progression", completed, job.Progression.Count, "quests");
                ImGui.Unindent();
                break;
            }
        }
    }

    private void DrawPinnedLocationLink(PinnedTargetConfig pin, int index)
    {
        switch (pin.Kind)
        {
            case "Item":
            {
                var item = catalog.GetById(pin.NumericId);
                if (item is null || ownership.GetItemStatus(item.ItemId).IsOwned)
                    return;
                var action = actionGuidance.Resolve(resolver.Resolve(item));
                if (!string.IsNullOrWhiteSpace(action.GoTo))
                    DrawCombinedWhoWhereLine("Map", action.GoTo, $"pin-item-{index}");
                else if (!string.IsNullOrWhiteSpace(action.QuestStartedBy))
                    DrawCombinedWhoWhereLine("Map", action.QuestStartedBy, $"pin-item-start-{index}");
                break;
            }
            case "Emote":
            {
                if (pin.NumericId > ushort.MaxValue || emoteCatalog.Get((ushort)pin.NumericId) is not { } emote ||
                    emoteCatalog.IsUnlocked(emote.EmoteId) == true)
                    return;

                if (emote.BackingItemId != 0 && catalog.GetById(emote.BackingItemId) is { } item)
                {
                    var action = actionGuidance.Resolve(resolver.Resolve(item));
                    if (!string.IsNullOrWhiteSpace(action.GoTo))
                        DrawCombinedWhoWhereLine("Map", action.GoTo, $"pin-emote-{index}");
                    else if (!string.IsNullOrWhiteSpace(action.QuestStartedBy))
                        DrawCombinedWhoWhereLine("Map", action.QuestStartedBy, $"pin-emote-start-{index}");
                    return;
                }

                if (emote.QuestId != 0 && questGuidance.GetById(emote.QuestId) is { } quest &&
                    !string.IsNullOrWhiteSpace(quest.QuestGiver) && !string.IsNullOrWhiteSpace(quest.Location))
                {
                    DrawNpcLocationLine("Map", quest.QuestGiver!, quest.Location, $"pin-emote-quest-{index}");
                }
                break;
            }
            case "Duty":
            {
                var duty = dutyCatalog.Get(pin.NumericId);
                if (duty is null || dutyUnlocks.IsDutyUnlocked(duty.Name) == true)
                    return;
                var advice = dutyUnlocks.GetAdvice(duty.Name);
                if (!string.IsNullOrWhiteSpace(advice?.Contact))
                    DrawNpcLocationLine("Map", advice!.Contact!, advice.Location, $"pin-duty-{index}");
                else if (!string.IsNullOrWhiteSpace(advice?.Location))
                    DrawLocationOnlyLine("Map", advice!.Location!, $"pin-duty-{index}");
                break;
            }
            case "JobUnlock":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job?.UnlockQuest is null || ownership.IsJobUnlocked(job.ClassJobId))
                    return;
                if (!string.IsNullOrWhiteSpace(job.UnlockQuest.QuestGiver) && !string.IsNullOrWhiteSpace(job.UnlockQuest.Location))
                    DrawNpcLocationLine("Map", job.UnlockQuest.QuestGiver!, job.UnlockQuest.Location, $"pin-job-unlock-{index}");
                break;
            }
            case "JobProgression":
            {
                var job = jobCatalog.Get(pin.NumericId);
                if (job is null)
                    return;
                var sequence = ResolveJobQuestSequence(job.Progression);
                var actionable = sequence.CurrentOrNext;
                if (!string.IsNullOrWhiteSpace(actionable?.Info?.QuestGiver) && !string.IsNullOrWhiteSpace(actionable?.Info?.Location))
                    DrawNpcLocationLine("Map", actionable!.Info!.QuestGiver!, actionable.Info.Location, $"pin-job-progress-{index}");
                break;
            }
            case "Set":
            {
                var set = setCatalog.Get(pin.StringId);
                if (set is null)
                    return;
                var members = set.ItemIds
                    .Select(catalog.GetById)
                    .Where(x => x is not null)
                    .Cast<TargetEntry>()
                    .Where(x => string.IsNullOrWhiteSpace(pin.Variant) || x.EquipJobs.Contains(pin.Variant))
                    .ToArray();
                var frontier = GetSetFrontierChoice(set, members);
                if (frontier is null)
                    return;
                if (!string.IsNullOrWhiteSpace(frontier.Action.GoTo))
                    DrawCombinedWhoWhereLine("Map", frontier.Action.GoTo, $"pin-set-{index}");
                else if (!string.IsNullOrWhiteSpace(frontier.DutyAdvice?.Contact))
                    DrawNpcLocationLine("Map", frontier.DutyAdvice!.Contact!, frontier.DutyAdvice.Location, $"pin-set-duty-{index}");
                break;
            }
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
                return members.Length > 0 && members.All(x => ownership.GetItemStatus(x.ItemId).IsOwned);
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
        ImGui.TextUnformatted(dutyName);
        if (unlocked == true)
            ImGui.PopStyleColor();

        ImGui.SameLine();
        DrawPinToggle("Duty", dutyPinId, string.Empty, "Pin Duty", "Unpin Duty");

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
            if (actionIsUnlockedDuty)
                ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
            ImGui.TextUnformatted($"{frontier.Action.ActionLabel}: {frontier.Action.ActionText}");
            if (actionIsUnlockedDuty)
                ImGui.PopStyleColor();

            var actionDuty = dutyCatalog.GetByName(frontier.Action.ActionText);
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
                ImGui.TextUnformatted($"Run: {advice.DutyName}");
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextUnformatted($"Unlock content: {advice.DutyName}");
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
                ? $"[JOB] Unlock {unlockJob.Name}"
                : $"[JOB] Missing ClassJob #{pin.NumericId}",
            "JobProgression" => jobCatalog.Get(pin.NumericId) is { } progressionJob
                ? $"[JOB] {progressionJob.Name} Progression"
                : $"[JOB] Missing ClassJob #{pin.NumericId}",
            "Duty" => dutyCatalog.Get(pin.NumericId) is { } duty
                ? $"[DUTY] {duty.Name}"
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
                RequestTargetScopedSourceEnrichment(item);
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
                if (setCatalog.Get(pin.StringId) is null)
                    return;
                config.SelectedSetId = pin.StringId;
                if (!string.IsNullOrWhiteSpace(pin.Variant))
                    config.SelectedRelicJob = pin.Variant;
                activeTab = MainTab.Items;
                SaveConfig();
                break;
            }
            case "Duty":
            {
                var duty = dutyCatalog.Get(pin.NumericId);
                if (duty is null)
                    return;
                config.SelectedDutyCategory = duty.Category;
                config.SelectedDutyId = duty.ContentFinderConditionId;
                activeTab = MainTab.Duties;
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
        string variant = "",
        Vector2? buttonSize = null)
    {
        var index = FindPinIndex(kind, numericId, stringId, variant);
        var pinned = index >= 0;

        if (!ImGui.Button(pinned ? unpinLabel : pinLabel, buttonSize ?? Vector2.Zero))
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

    private void DrawItemsNavigation()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Set Target");
        ImGui.Separator();
        DrawSetSelector();

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
            // Manual refreshes are deliberately rate-limited so repeated clicks
            // cannot hammer community services or create avoidable network load.
            nextManualSourceRefreshUtc = DateTime.UtcNow.AddMinutes(5);
            coverageMetricsExpiresUtc = DateTime.MinValue;
            _ = collectibleSources.RefreshAsync();
            // Garland is intentionally target-scoped. A manual refresh must not
            // fan out into thousands of per-item HTTP requests. Select an item
            // that still needs enrichment to request that one document.
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

        // Never scan/resolve the full catalog from Draw(). v1.1.12.6 made
        // provenance resolution more thorough, which turned the old automatic
        // two-second coverage refresh into a very expensive render-thread task.
        // Full-catalog analysis now happens only through the explicit Ambiguity
        // Audit export; normal Debug rendering stays O(1).
        ImGui.TextDisabled($"Catalog targets: {catalog.Targets.Count:N0}");
        ImGui.TextDisabled($"Target sets: {setCatalog.Sets.Count:N0}");
        ImGui.TextDisabled(
            $"Job progression targets: {jobCatalog.Jobs.Count:N0} " +
            $"({jobCatalog.Jobs.Count(j => j.UnlockQuest is not null):N0} with unlock quest; " +
            $"{jobCatalog.Jobs.Sum(j => j.Progression.Count):N0} formal quest nodes)");
        ImGui.TextDisabled($"Pinned targets: {config.PinnedTargets.Count:N0}");
        ImGui.TextDisabled(
            $"Ownership scan: {(ownershipPollingActive ? "active (one-shot full pass)" : "idle")}; " +
            $"permanent={ownership.PermanentCandidateCursor:N0}/{ownership.PermanentCandidateCount:N0}; " +
            $"duties={dutyUnlocks.PollingCursor:N0}/{dutyUnlocks.PollingCandidateCount:N0}");
        ImGui.TextDisabled("Catalog-wide resolver coverage is computed only by the on-demand Ambiguity Audit (never during UI rendering).");
        if (!string.IsNullOrWhiteSpace(releaseDiagnosticsStatus))
            ImGui.TextDisabled($"Configuration diagnostic: {releaseDiagnosticsStatus}");

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

        if (ImGui.Button("Clear cached items"))
        {
            ownershipPollingActive = false;
            var removedDresserSnapshot = ownership.ClearCachedOwnershipForCurrentCharacter();
            exportStatus = removedDresserSnapshot
                ? "Cleared cached ownership for this character, including its saved Glamour Dresser snapshot. Run /cqg to start a fresh full ownership scan."
                : "Cleared cached ownership for this character. Run /cqg to start a fresh full ownership scan.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Clears CQG's cached item/job ownership state for the current character. " +
                "This also clears the saved Glamour Dresser ownership snapshot, but does not clear acquisition-source or duty resolver caches. " +
                "Run /cqg afterward for a fresh one-shot ownership scan.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Clear network caches"))
        {
            var collectibleCleared = collectibleSources.ClearSessionCache();
            var garlandItemsCleared = garlandSources.ClearCache();
            var garlandDutiesCleared = garlandDutyUnlocks.ClearCache();
            var communityDutiesCleared = communityDutyUnlocks.ClearCache();

            if (collectibleCleared && garlandItemsCleared && garlandDutiesCleared && communityDutiesCleared)
            {
                networkRequests.Reset();
                nextManualSourceRefreshUtc = DateTime.MinValue;
                coverageMetricsExpiresUtc = DateTime.MinValue;
                exportStatus =
                    "Cleared persisted/community network caches and FFXIV Collect session data. " +
                    "The next unresolved target may perform fresh, rate-limited network lookups.";
            }
            else
            {
                exportStatus =
                    "Could not clear every network cache because a network lookup/refresh is currently active. " +
                    "Wait for it to finish and try again.";
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Deletes CQG's persisted Community Duty, Garland Item, and Garland Duty caches, " +
                "clears FFXIV Collect data loaded in this plugin session, and resets network diagnostics. " +
                "This does not clear item ownership or Glamour Dresser ownership. " +
                "Use this only for diagnostics/fresh-network testing; normal users should leave network caches intact to minimize HTTP traffic.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Output target information"))
            OutputTargetInformation(selectedResult, selectedSet);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Writes everything CQG currently knows about the selected target to a text file. " +
                "This is a read-only snapshot: pressing this button never starts a network request. " +
                "Already-cached community data is included when present.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Export ambiguity audit"))
            ExportAmbiguityAuditCsv();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Audits ambiguous/incomplete item resolutions using only local game data and already-cached community data. " +
                "The audit makes ZERO network calls. Cases that could normally use uncached community enrichment are marked 'missing network data'.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Export unresolved CSV"))
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

    private void OutputTargetInformation(
        AcquisitionResult? selectedResult,
        TargetSet? selectedSet)
    {
        try
        {
            if (selectedResult is null && selectedSet is null)
            {
                exportStatus = "No item or set target is currently selected.";
                return;
            }

            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, $"CustomQuestGuide-TargetInformation-{stamp}.txt");
            var output = new StringBuilder();
            output.AppendLine("Custom Quest Guide — selected target information");
            output.AppendLine("READ-ONLY SNAPSHOT: this export does not start network requests.");
            output.AppendLine("Cached community data is included when already present.");
            output.AppendLine();

            if (selectedSet is not null)
            {
                output.AppendLine("TARGET TYPE: Set");
                output.AppendLine($"Set ID: {selectedSet.Id}");
                output.AppendLine($"Group: {selectedSet.Group}");
                output.AppendLine($"Name: {selectedSet.Name}");
                output.AppendLine($"Description: {selectedSet.Description}");
                output.AppendLine($"Members: {selectedSet.ItemIds.Count}");
                output.AppendLine($"Completion reward IDs: {string.Join(", ", selectedSet.CompletionRewardItemIds)}");
                output.AppendLine();

                foreach (var itemId in selectedSet.ItemIds)
                {
                    var target = catalog.GetById(itemId);
                    if (target is null)
                    {
                        output.AppendLine($"[{itemId}] <catalog row missing>");
                        continue;
                    }

                    output.AppendLine(new string('-', 80));
                    AppendTargetInformation(output, target, selectedSet.Id);
                }
            }
            else if (selectedResult is not null)
            {
                output.AppendLine("TARGET TYPE: Item");
                AppendTargetInformation(output, selectedResult.Target, FindContainingSet(selectedResult.Target.ItemId)?.Id);
            }

            File.WriteAllText(path, output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            exportStatus = $"Output target information to:\n{path}";
            Log.Information($"Output CQG target information to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Target information output failed: {ex.Message}";
            Log.Error(ex, "Failed to output CQG target information.");
        }
    }

    private void AppendTargetInformation(StringBuilder output, TargetEntry target, string? containingSetId)
    {
        // IMPORTANT: ResolveDiagnostic() consumes only local/live data plus caches.
        // It intentionally includes internal resolver relationships because this
        // export is a Debug diagnostic; player-facing UI uses Resolve(), which
        // filters those records out. The cached-only action/duty methods below
        // explicitly suppress every EnsureLoaded/EnsureRequested network path.
        var result = resolver.ResolveDiagnostic(target);
        var primary = result.Sources.FirstOrDefault();
        var owned = ownership.GetItemStatus(target.ItemId);
        var action = actionGuidance.Resolve(result, allowNetworkRequests: false);
        var availability = resolver.GetAvailabilityState(target);
        var direct = resolver.HasDirectSource(target);
        var contradiction = DescribeResolverContradiction(result, action, owned, direct, availability);
        var collectCached = collectibleSources.GetSources(target.ItemId);
        var garlandCached = garlandSources.GetSources(target.ItemId);
        var dependencies = resolver.GetExchangeDependencies(target.ItemId);

        output.AppendLine($"Item ID: {target.ItemId}");
        output.AppendLine($"Name: {target.Name}");
        output.AppendLine($"Category: {target.Category}");
        output.AppendLine($"Game UI Category: {target.GameUiCategory}");
        output.AppendLine($"Filter Group: {target.FilterGroup}");
        output.AppendLine($"Equip Jobs: {(target.EquipJobs.Count == 0 ? "<none>" : string.Join(", ", target.EquipJobs.OrderBy(x => x)))}");
        output.AppendLine($"Untradable: {target.IsUntradable}");
        output.AppendLine($"Owned: {owned.IsOwned}");
        output.AppendLine($"Ownership Source: {owned.Source}");
        output.AppendLine($"Containing Set: {containingSetId ?? "<none>"}");
        output.AppendLine();

        output.AppendLine("RESOLUTION");
        output.AppendLine($"Resolution Tier: {resolver.GetResolutionTier(target)}");
        output.AppendLine($"Source Specificity: {resolver.GetSourceSpecificity(target)}");
        output.AppendLine($"Availability: {availability}");
        output.AppendLine($"Direct Source Known: {direct}");
        output.AppendLine($"Curated Fallback Only: {resolver.IsCuratedFallbackOnly(target)}");
        output.AppendLine($"Needs Specificity Enrichment: {resolver.NeedsSpecificityEnrichment(target)}");
        output.AppendLine($"Needs Exact Item Specificity: {resolver.NeedsExactItemSpecificity(target)}");
        output.AppendLine($"Garland Enrichment Eligible: {resolver.NeedsGarlandEnrichment(target)}");
        output.AppendLine($"Contradiction Check: {(string.IsNullOrWhiteSpace(contradiction) ? "<none>" : contradiction)}");
        output.AppendLine();

        output.AppendLine("CURRENT PRIMARY / ACTION");
        output.AppendLine($"Primary: {(primary is null ? "<none>" : FormatSourceForDiagnostic(primary))}");
        output.AppendLine($"Action Label: {action.ActionLabel}");
        output.AppendLine($"Action Text: {action.ActionText}");
        output.AppendLine($"Action GoTo: {action.GoTo}");
        output.AppendLine($"Action QuestStartedBy: {action.QuestStartedBy}");
        output.AppendLine($"Action Source: {(action.Source is null ? "<none>" : FormatSourceForDiagnostic(action.Source))}");
        output.AppendLine();

        output.AppendLine($"ALL CURRENT RESOLVED SOURCES ({result.Sources.Count})");
        if (result.Sources.Count == 0)
            output.AppendLine("  <none>");
        for (var i = 0; i < result.Sources.Count; i++)
            output.AppendLine($"  [{i}] {FormatSourceForDiagnostic(result.Sources[i])}");
        output.AppendLine();

        output.AppendLine("CACHED COMMUNITY DATA");
        output.AppendLine($"FFXIV Collect cached source count: {collectCached.Count}");
        foreach (var source in collectCached)
            output.AppendLine($"  Collect: {FormatSourceForDiagnostic(source)}");
        output.AppendLine($"Garland item checked/cached: {garlandSources.IsChecked(target.ItemId)}");
        output.AppendLine($"Garland item cached miss: {garlandSources.IsCachedMiss(target.ItemId)}");
        output.AppendLine($"Garland cached source count: {garlandCached.Count}");
        foreach (var source in garlandCached)
            output.AppendLine($"  Garland: {FormatSourceForDiagnostic(source)}");
        if (resolver.NeedsGarlandEnrichment(target) && !garlandSources.IsChecked(target.ItemId))
            output.AppendLine("  Network status: missing network data (eligible item has no cached Garland lookup result)");
        output.AppendLine();

        output.AppendLine($"EXCHANGE DEPENDENCIES ({dependencies.Count})");
        if (dependencies.Count == 0)
            output.AppendLine("  <none>");
        foreach (var dependency in dependencies)
        {
            output.AppendLine(
                $"  CostItemId={dependency.CostItemId}; CostItem={dependency.CostItemName}; Amount={dependency.Amount}; " +
                $"Shop={dependency.ShopName}; NPCs={(dependency.NpcNames.Count == 0 ? "<none>" : string.Join(" | ", dependency.NpcNames))}");

            var costSources = resolver.GetKnownSourcesForItemId(dependency.CostItemId);
            output.AppendLine($"    Cost item current sources ({costSources.Count}):");
            foreach (var source in costSources)
                output.AppendLine($"      {FormatSourceForDiagnostic(source)}");

            var costTarget = catalog.GetById(dependency.CostItemId);
            if (costTarget is not null &&
                resolver.NeedsGarlandEnrichment(costTarget) &&
                !garlandSources.IsChecked(costTarget.ItemId))
            {
                output.AppendLine("      Network status: missing network data (cost item is eligible for uncached Garland enrichment)");
            }
            else if (costSources.Count == 0 && !garlandSources.IsChecked(dependency.CostItemId))
            {
                output.AppendLine("      Network status: missing network data (dependency has no cached exact source)");
            }
        }
        output.AppendLine();

        output.AppendLine("SUPPLEMENTAL RAW RELATIONSHIPS");
        if (supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var dutyRefs) && dutyRefs.Count > 0)
        {
            output.AppendLine($"  Duty references ({dutyRefs.Count}): {string.Join(" | ", dutyRefs.Select(x => $"{x.Kind}:{x.Id}"))}");
        }
        else
        {
            output.AppendLine("  Duty references: <none>");
        }

        if (supplemental.ItemSources.TryGetValue(target.ItemId, out var supplementalRows) && supplementalRows.Count > 0)
        {
            output.AppendLine($"  Supplemental source rows ({supplementalRows.Count}):");
            foreach (var row in supplementalRows)
            {
                var fields = string.Join("; ", row.Row.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}"));
                output.AppendLine($"    Dataset={row.Dataset}; Kind={row.Kind}; {fields}");
            }
        }
        else
        {
            output.AppendLine("  Supplemental source rows: <none>");
        }
        output.AppendLine();

        var resolvedDuties = result.Sources
            .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var supplementalDutyCandidates = resolver.GetSupplementalDutyCandidateNames(target.ItemId);
        var duties = resolvedDuties
            .Concat(supplementalDutyCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        output.AppendLine($"DUTY CANDIDATES ({duties.Length})");
        if (duties.Length == 0)
            output.AppendLine("  <none>");
        foreach (var duty in duties)
        {
            var provenance = resolvedDuties.Contains(duty, StringComparer.OrdinalIgnoreCase)
                ? "resolved/current"
                : "supplemental candidate (suppressed from current answer)";
            output.AppendLine($"  [{provenance}]");
            AppendDutyDiagnostic(output, duty, containingSetId, "    ");
        }

        if (result.Guide is not null)
        {
            output.AppendLine();
            output.AppendLine($"CURATED GUIDE: {result.Guide.Name}");
            foreach (var chain in result.Guide.Chains)
            {
                output.AppendLine($"  Chain: {chain.Name} — {chain.Description}");
                foreach (var step in chain.Steps)
                    output.AppendLine($"    {step.QuestName} | {step.QuestGiver} | {step.Zone} ({step.X:F1},{step.Y:F1}) | {step.Note}");
            }
        }
    }

    private void AppendDutyDiagnostic(StringBuilder output, string dutyName, string? setId, string indent)
    {
        var cfcId = dutyUnlocks.GetContentFinderConditionId(dutyName);
        var instanceId = dutyUnlocks.GetInstanceContentId(dutyName);
        var advice = string.IsNullOrWhiteSpace(setId)
            ? dutyUnlocks.GetAdviceCachedOnly(dutyName)
            : dutyUnlocks.GetAdviceCachedOnly(setId, dutyName);
        var communityQuest = communityDutyUnlocks.GetCachedQuestId(dutyName);
        var garlandQuest = instanceId == 0 ? null : garlandDutyUnlocks.GetCachedQuestId(instanceId);
        var garlandChecked = instanceId != 0 && garlandDutyUnlocks.IsChecked(instanceId);

        output.AppendLine($"{indent}{dutyName}");
        output.AppendLine($"{indent}  ContentFinderConditionId: {cfcId}");
        output.AppendLine($"{indent}  InstanceContentId: {instanceId}");
        output.AppendLine($"{indent}  Unlock snapshot: {dutyUnlocks.IsDutyUnlocked(dutyName)?.ToString() ?? "unknown"}");
        output.AppendLine($"{indent}  Cached community quest ID: {(communityQuest?.ToString() ?? "<none>")}");
        output.AppendLine($"{indent}  Cached Garland checked: {garlandChecked}");
        output.AppendLine($"{indent}  Cached Garland quest ID: {(garlandQuest?.ToString() ?? "<none>")}");
        if (advice is null)
        {
            output.AppendLine($"{indent}  Cached/local advice: <none>");
            return;
        }

        output.AppendLine($"{indent}  Advice quest: {advice.QuestName}");
        output.AppendLine($"{indent}  Advice contact: {advice.Contact}");
        output.AppendLine($"{indent}  Advice location: {advice.Location}");
        output.AppendLine($"{indent}  Advice note: {advice.Note}");
    }

    private static string FormatSourceForDiagnostic(SourceEntry source) =>
        $"Kind={source.Kind}; Name={source.Name}; Detail={source.Detail ?? string.Empty}; " +
        $"Unlocked={(source.IsUnlocked.HasValue ? source.IsUnlocked.Value.ToString() : "unknown")}; " +
        $"Priority={source.Priority}; SpecificityHint={source.SpecificityHint ?? string.Empty}";

    private void ExportAmbiguityAuditCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var fileName = $"CustomQuestGuide-AmbiguityAudit-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);
            var csv = new StringBuilder();

            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Equip Jobs,Owned,Ownership Source," +
                "Ambiguity Reasons,Missing Network Data,Resolution Tier,Source Specificity,Availability," +
                "Direct Source Known,Curated Fallback Only,Needs Specificity Enrichment,Needs Exact Item Specificity," +
                "Garland Enrichment Eligible,Garland Cached Checked,Garland Cached Miss," +
                "Primary Source,Current Action Label,Current Action Text,Current Action Location," +
                "All Current Sources,Cached FFXIV Collect Sources,Cached Garland Sources," +
                "Exchange Dependencies,Supplemental Duty References,Supplemental Duty Candidate Names,Supplemental Raw Rows,Duty Diagnostics,Contradiction Check");

            var exported = 0;
            foreach (var target in catalog.Targets
                         .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var result = resolver.Resolve(target);
                var primary = result.Sources.FirstOrDefault();
                var owned = ownership.GetItemStatus(target.ItemId);
                var action = actionGuidance.Resolve(result, allowNetworkRequests: false);
                var availability = resolver.GetAvailabilityState(target);
                var direct = resolver.HasDirectSource(target);
                var contradiction = DescribeResolverContradiction(result, action, owned, direct, availability);
                var dependencies = resolver.GetExchangeDependencies(target.ItemId);
                var reasons = new List<string>();
                var missingNetwork = new List<string>();

                // A client-data item with explicitly unknown/future availability is
                // already being described truthfully. It is not a resolver ambiguity
                // and should not pollute the actionable acquisition audit.
                if (availability.Equals("Future / Unknown", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (result.Sources.Count == 0 && result.Guide is null)
                    reasons.Add("No source resolved");
                if (resolver.NeedsSpecificityEnrichment(target))
                    reasons.Add("Source specificity incomplete");
                if (resolver.NeedsExactItemSpecificity(target))
                    reasons.Add("Exact item-specific source missing");
                if (!string.IsNullOrWhiteSpace(contradiction))
                    reasons.Add("Resolver contradiction");
                if (primary is not null &&
                    (primary.Name.Contains("exact duty unresolved", StringComparison.OrdinalIgnoreCase) ||
                     primary.Detail?.Contains("exact duty unresolved", StringComparison.OrdinalIgnoreCase) == true ||
                     primary.Detail?.Contains("exact item-specific duty", StringComparison.OrdinalIgnoreCase) == true ||
                     primary.Name.Equals("Duty Chest", StringComparison.OrdinalIgnoreCase) ||
                     primary.Name.Equals("Duty Boss", StringComparison.OrdinalIgnoreCase) ||
                     primary.Name.Equals("Duty Boss Chest", StringComparison.OrdinalIgnoreCase) ||
                     primary.Name.Equals("Duty / Instance Drop", StringComparison.OrdinalIgnoreCase)))
                {
                    reasons.Add("Duty family known but exact duty unresolved");
                }

                var resolvedDuties = result.Sources
                    .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var supplementalDutyCandidates = resolver.GetSupplementalDutyCandidateNames(target.ItemId);

                // Multiple exact/current duties can be legitimate for materials or
                // currencies. Flag only unresolved supplemental ambiguity, where CQG
                // lacks an authoritative item-specific duty and therefore cannot say
                // which candidate is the intended acquisition route.
                if (supplementalDutyCandidates.Count > 1 &&
                    resolvedDuties.Length == 0 &&
                    !garlandSources.HasSource(target.ItemId))
                {
                    reasons.Add($"Conflicting supplemental duty candidates ({supplementalDutyCandidates.Count})");
                }

                var dutiesForDiagnostics = resolvedDuties
                    .Concat(supplementalDutyCandidates)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var dutyDiagnostics = new List<string>();
                var containingSetId = FindContainingSet(target.ItemId)?.Id;
                foreach (var duty in dutiesForDiagnostics)
                {
                    var cfcId = dutyUnlocks.GetContentFinderConditionId(duty);
                    var instanceId = dutyUnlocks.GetInstanceContentId(duty);
                    var advice = string.IsNullOrWhiteSpace(containingSetId)
                        ? dutyUnlocks.GetAdviceCachedOnly(duty)
                        : dutyUnlocks.GetAdviceCachedOnly(containingSetId, duty);
                    var communityQuest = communityDutyUnlocks.GetCachedQuestId(duty);
                    var garlandQuest = instanceId == 0 ? null : garlandDutyUnlocks.GetCachedQuestId(instanceId);
                    var garlandChecked = instanceId != 0 && garlandDutyUnlocks.IsChecked(instanceId);
                    var note = advice?.Note ?? string.Empty;
                    var isCurrent = resolvedDuties.Contains(duty, StringComparer.OrdinalIgnoreCase);

                    if (note.Contains("Missing network data", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep suppressed candidates in the diagnostic trail, but
                        // do not classify their missing enrichment as an active
                        // problem once CQG has a different authoritative duty.
                        // An already-unlocked current duty also does not need an
                        // unlock-quest network lookup.
                        if (isCurrent && dutyUnlocks.IsDutyUnlocked(duty) != true)
                            missingNetwork.Add($"Duty {duty}: {note}");
                    }
                    else if (isCurrent &&
                             advice is not null &&
                             advice.IsUnlocked != true &&
                             string.IsNullOrWhiteSpace(advice.QuestName))
                    {
                        reasons.Add($"Locked duty has no resolved unlock quest: {duty}");
                    }

                    dutyDiagnostics.Add(
                        $"{duty} [provenance={(isCurrent ? "current" : "supplemental-candidate")}; CFC={cfcId}; Instance={instanceId}; unlocked={dutyUnlocks.IsDutyUnlocked(duty)?.ToString() ?? "unknown"}; " +
                        $"quest={advice?.QuestName ?? string.Empty}; contact={advice?.Contact ?? string.Empty}; location={advice?.Location ?? string.Empty}; " +
                        $"communityQuest={communityQuest?.ToString() ?? string.Empty}; garlandChecked={garlandChecked}; garlandQuest={garlandQuest?.ToString() ?? string.Empty}; note={note}]");
                }

                if (resolver.NeedsGarlandEnrichment(target) && !garlandSources.IsChecked(target.ItemId))
                    missingNetwork.Add("Item-specific Garland enrichment is eligible but not cached");

                if (dependencies.Count > 0)
                {
                    foreach (var dependency in dependencies)
                    {
                        // Gil is a universal terminal currency, not an acquisition
                        // dependency that needs its own resolver chain. Asking CQG to
                        // explain where Gil comes from adds noise rather than guidance.
                        if (dependency.CostItemName.Equals("Gil", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var costSources = resolver.GetKnownSourcesForItemId(dependency.CostItemId);
                        if (costSources.Count == 0)
                        {
                            reasons.Add($"Exchange dependency source unresolved: {dependency.CostItemName}");
                            if (!garlandSources.IsChecked(dependency.CostItemId))
                                missingNetwork.Add($"Exchange cost item {dependency.CostItemName}: missing network data");
                        }

                        var costTarget = catalog.GetById(dependency.CostItemId);
                        if (costTarget is not null &&
                            resolver.NeedsGarlandEnrichment(costTarget) &&
                            !garlandSources.IsChecked(costTarget.ItemId))
                        {
                            missingNetwork.Add($"Exchange cost item {dependency.CostItemName} is eligible for uncached Garland enrichment");
                        }
                    }
                }

                if (IsShopDiagnostic(primary) && action.IsEmpty)
                    reasons.Add("Shop/exchange source lacks actionable vendor/location guidance");

                if (reasons.Count == 0 && missingNetwork.Count == 0)
                    continue;

                exported++;
                var collectCached = collectibleSources.GetSources(target.ItemId);
                var garlandCached = garlandSources.GetSources(target.ItemId);
                supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var dutyRefs);
                supplemental.ItemSources.TryGetValue(target.ItemId, out var supplementalRows);

                var allSources = string.Join(" || ", result.Sources.Select(FormatSourceForDiagnostic));
                var collectSources = string.Join(" || ", collectCached.Select(FormatSourceForDiagnostic));
                var garlandSourcesText = string.Join(" || ", garlandCached.Select(FormatSourceForDiagnostic));
                var dependencyText = string.Join(" || ", dependencies.Select(x =>
                    $"CostItemId={x.CostItemId}; CostItem={x.CostItemName}; Amount={x.Amount}; Shop={x.ShopName}; NPCs={string.Join(" | ", x.NpcNames)}"));
                var dutyRefText = dutyRefs is null
                    ? string.Empty
                    : string.Join(" || ", dutyRefs.Select(x => $"{x.Kind}:{x.Id}"));
                var supplementalDutyCandidateText = string.Join(" || ", supplementalDutyCandidates);
                var supplementalText = supplementalRows is null
                    ? string.Empty
                    : string.Join(" || ", supplementalRows.Select(row =>
                        $"Dataset={row.Dataset}; Kind={row.Kind}; " +
                        string.Join("; ", row.Row.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}"))));

                var fields = new[]
                {
                    target.ItemId.ToString(), target.Name, target.Category, target.GameUiCategory, target.FilterGroup.ToString(),
                    string.Join(" | ", target.EquipJobs.OrderBy(x => x)), owned.IsOwned ? "Yes" : "No", owned.Source,
                    string.Join("; ", reasons.Distinct()), missingNetwork.Count == 0 ? "No" : string.Join("; ", missingNetwork.Distinct()),
                    resolver.GetResolutionTier(target), resolver.GetSourceSpecificity(target), availability,
                    direct ? "Yes" : "No", resolver.IsCuratedFallbackOnly(target) ? "Yes" : "No",
                    resolver.NeedsSpecificityEnrichment(target) ? "Yes" : "No",
                    resolver.NeedsExactItemSpecificity(target) ? "Yes" : "No",
                    resolver.NeedsGarlandEnrichment(target) ? "Yes" : "No",
                    garlandSources.IsChecked(target.ItemId) ? "Yes" : "No",
                    garlandSources.IsCachedMiss(target.ItemId) ? "Yes" : "No",
                    primary is null ? string.Empty : FormatSourceForDiagnostic(primary),
                    action.ActionLabel, action.ActionText,
                    !string.IsNullOrWhiteSpace(action.GoTo) ? action.GoTo : action.QuestStartedBy,
                    allSources, collectSources, garlandSourcesText, dependencyText, dutyRefText, supplementalDutyCandidateText, supplementalText,
                    string.Join(" || ", dutyDiagnostics), contradiction,
                };
                csv.AppendLine(string.Join(",", fields.Select(Csv)));
            }

            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            exportStatus = $"Exported ambiguity audit ({exported:N0} entries) to:\n{path}";
            Log.Information($"Exported {exported:N0} CQG ambiguity audit entries to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Ambiguity audit export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG ambiguity audit.");
        }
    }

    private static bool IsShopDiagnostic(SourceEntry? source)
    {
        if (source is null)
            return false;
        return source.Kind.Contains("Vendor", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
               source.Name.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
               source.Name.Contains("Vendor", StringComparison.OrdinalIgnoreCase);
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
        var hasMeaningfulResolvedSource = result.Sources.Any(source =>
            !source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase));
        if (!directSourceKnown && (hasMeaningfulResolvedSource || result.Guide is not null))
            issues.Add("Actionable source/guide exists while direct-source flag is false");
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
            var primaryDuty = GetDutyFromSource(primary);
            var primaryDutyUnlocked = primaryDuty is not null && dutyUnlocks.IsDutyUnlocked(primaryDuty.Name) == true;
            if (SourceActionGuidanceResolver.TryExtractGatheringLocation(primary, out var gatheringMethod, out var gatheringPlace))
            {
                ImGui.TextUnformatted($"{gatheringMethod}:");
                ImGui.SameLine();
                DrawClickableLocation(gatheringPlace, $"source-gathering-{result.Target.ItemId}", null);
            }
            else
            {
                if (primaryDutyUnlocked)
                    ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                ImGui.TextUnformatted(primary.Name);
                if (primaryDutyUnlocked)
                    ImGui.PopStyleColor();
            }

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

        DrawDutyDependency(result, $"item-{result.Target.ItemId}");
        DrawExchangePrerequisiteControls(result.Target.ItemId);

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

                DrawNpcLocationLine(
                    status.IsActive ? "Quest started by" : "Go to",
                    status.NextStep.QuestGiver,
                    $"{status.NextStep.Zone} (X:{status.NextStep.X:0.0}, Y:{status.NextStep.Y:0.0})",
                    $"guide-summary-{result.Target.ItemId}");
            }
        }
    }

    private void DrawExchangePrerequisiteControls(uint targetItemId)
    {
        var dependencies = resolver.GetExchangeDependencies(targetItemId);
        if (dependencies.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted(dependencies.Count == 1 ? "Prerequisite item:" : "Prerequisite items:");

        for (var i = 0; i < dependencies.Count; i++)
        {
            var dependency = dependencies[i];
            if (dependency.CostItemName.Equals("Gil", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = catalog.GetById(dependency.CostItemId);
            var owned = target is null
                ? OwnershipStatus.Missing
                : ownership.GetItemStatus(dependency.CostItemId);

            if (owned.IsOwned)
                ImGui.PushStyleColor(ImGuiCol.Text, IsGlamourDresserOwnership(owned) ? GlamourPink : OwnedGreen);
            ImGui.TextUnformatted($"{dependency.Amount:N0} {dependency.CostItemName}");
            if (owned.IsOwned)
                ImGui.PopStyleColor();

            // Tomestones are system currencies, not useful "open this item"
            // prerequisites. Show the practical repeatable earning route directly
            // in the compact summary (Detailed off or on) instead of making the
            // player open the currency target to learn how to farm it.
            if (dependency.CostItemName.StartsWith("Allagan Tomestone of ", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("— Earn via eligible duties, roulettes, hunts, and other level-capped content");
                continue;
            }

            if (target is null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(not selectable as a CQG target)");
                continue;
            }

            // Keep the two prerequisite actions visually identical in size.
            // Use the wider pin/unpin label as the shared width so neither
            // button changes size when the pin state changes.
            var dependencyButtonWidth =
                Math.Max(ImGui.CalcTextSize("Pin Item").X, ImGui.CalcTextSize("Unpin Item").X) +
                ImGui.GetStyle().FramePadding.X * 2.0f;
            var dependencyButtonSize = new Vector2(dependencyButtonWidth, 0);

            ImGui.SameLine();
            if (ImGui.Button($"Open##dependency-open-{targetItemId}-{dependency.CostItemId}-{i}", dependencyButtonSize))
                SelectItemTarget(target);

            ImGui.SameLine();
            DrawPinToggle(
                "Item",
                dependency.CostItemId,
                string.Empty,
                "Pin Item",
                "Unpin Item",
                buttonSize: dependencyButtonSize);
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
            var hasGameplaySource = result.Sources.Any(source =>
                !(source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase) &&
                  source.Name.Equals("Market Board / Player Trade", StringComparison.OrdinalIgnoreCase)));

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

                var duty = GetDutyFromSource(source);
                var dutyUnlocked = duty is not null && dutyUnlocks.IsDutyUnlocked(duty.Name) == true;
                if (SourceActionGuidanceResolver.TryExtractGatheringLocation(source, out var detailedMethod, out var detailedPlace))
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{source.Kind}: {detailedMethod}:");
                    ImGui.SameLine();
                    DrawClickableLocation(detailedPlace, $"detail-gathering-{result.Target.ItemId}-{detailedPlace}", null);
                    if (!string.IsNullOrWhiteSpace(detail) || !string.IsNullOrWhiteSpace(suffix))
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"{detail}{suffix}");
                    }
                }
                else
                {
                    if (dutyUnlocked)
                        ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                    var sourceLabel = hasGameplaySource &&
                                      source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase) &&
                                      source.Name.Equals("Market Board / Player Trade", StringComparison.OrdinalIgnoreCase)
                        ? "Alternative"
                        : source.Kind;

                    ImGui.BulletText(
                        $"{sourceLabel}: {source.Name}{detail}{suffix}");
                    if (dutyUnlocked)
                        ImGui.PopStyleColor();
                }
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

        SetNextControlWidth(ChainComboWidth);
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
                    DrawNpcLocationLine("Go to", currentStep.QuestGiver, $"{currentStep.Zone} (X:{currentStep.X:0.0}, Y:{currentStep.Y:0.0})", $"guide-current-{chainIndex}");
                else
                    DrawNpcLocationLine("Quest started by", currentStep.QuestGiver, currentStep.Zone, $"guide-active-{chainIndex}");
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
                DrawNpcLocationLine("Go to", step.QuestGiver, $"{step.Zone} (X:{step.X:0.0}, Y:{step.Y:0.0})", $"guide-step-{chainIndex}-{i}");

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

        // NPC/vendor guidance historically encoded "NPC — Location" in ActionText.
        // Render that through the shared clickable location path instead of as
        // plain text so vendor/field-operation destinations such as Gerolt —
        // North Shroud get the same map behavior as quest guidance.
        if (guidance.ActionLabel.Equals("Go to", StringComparison.OrdinalIgnoreCase))
        {
            DrawCombinedWhoWhereLine("Go to", guidance.ActionText, $"guidance-action-{result.Target.ItemId}", guidance.Source?.NpcId);
            return;
        }

        var runUnlockedDuty = guidance.ActionLabel.Equals("Run", StringComparison.OrdinalIgnoreCase);
        if (runUnlockedDuty)
            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
        ImGui.TextUnformatted($"{guidance.ActionLabel}:");
        ImGui.SameLine();
        ImGui.TextUnformatted(guidance.ActionText);
        if (runUnlockedDuty)
            ImGui.PopStyleColor();

        if (!string.IsNullOrWhiteSpace(guidance.GoTo))
        {
            DrawCombinedWhoWhereLine("Go to", guidance.GoTo, $"guidance-{result.Target.ItemId}");
        }
        else if (!string.IsNullOrWhiteSpace(guidance.QuestStartedBy))
        {
            DrawCombinedWhoWhereLine("Quest started by", guidance.QuestStartedBy, $"guidance-start-{result.Target.ItemId}");
        }
    }

    private void DrawCombinedWhoWhereLine(string prefix, string value, string id, uint? npcId = null)
    {
        var parts = value.Split(" — ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var npc = parts[0];
            var location = string.Join(" — ", parts.Skip(1));
            DrawNpcLocationLine(prefix, npc, location, id, npcId);
            return;
        }

        var inferred = npcLocations.GetBestLocation(value);
        if (!string.IsNullOrWhiteSpace(inferred))
        {
            DrawNpcLocationLine(prefix, value, inferred, id, npcId);
            return;
        }

        if (npcLocations.GetBestPlaceMapLocation(value) is not null)
        {
            DrawLocationOnlyLine(prefix, value, id);
            return;
        }

        ImGui.TextUnformatted($"{prefix}: {value}");
    }

    private void DrawNpcLocationLine(string prefix, string npcName, string? location, string id, uint? npcId = null)
    {
        ImGui.TextUnformatted($"{prefix}: {npcName}");
        if (!string.IsNullOrWhiteSpace(location))
        {
            ImGui.SameLine();
            DrawClickableLocation(location!, id, npcName, npcId);
        }
    }

    private void DrawLocationOnlyLine(string prefix, string location, string id)
    {
        ImGui.TextUnformatted($"{prefix}:");
        ImGui.SameLine();
        DrawClickableLocation(location, id, null);
    }

    private void DrawClickableLocation(string location, string id, string? npcName, uint? npcId = null)
    {
        var displayedPlaceName = GetDisplayedPlaceName(location);
        var hasInteriorEntranceFallback = TryGetInteriorEntranceFallback(displayedPlaceName, out _, out _, out _);

        var canPin = hasInteriorEntranceFallback;
        if (!canPin && npcId is > 0)
            canPin = npcLocations.GetBestMapLocation(npcId.Value) is not null;
        if (!canPin && !string.IsNullOrWhiteSpace(npcName))
            canPin = npcLocations.GetBestMapLocation(npcName!) is not null;
        if (!canPin)
            canPin = npcLocations.GetBestPlaceMapLocation(displayedPlaceName) is not null;
        if (!canPin)
            canPin = TryParseLocationCoordinates(location, out _, out _, out _);

        if (!canPin)
        {
            ImGui.TextDisabled(location);
            return;
        }

        if (ImGui.SmallButton($"{location}##map-{id}"))
            OpenMapLocation(location, npcName, npcId);

        if (ImGui.IsItemHovered())
        {
            if (hasInteriorEntranceFallback)
                ImGui.SetTooltip($"This destination is inside {displayedPlaceName}; place a flag at its overworld entrance instead.");
            else
                ImGui.SetTooltip("Open the map and place a flag at this location.");
        }
    }

    private void OpenMapLocation(string location, string? npcName, uint? npcId = null)
    {
        try
        {
            var displayedPlaceName = GetDisplayedPlaceName(location);

            // Prefer an explicit exterior entrance for known interior-only places.
            // The interior coordinates may be perfectly valid inside that territory
            // while still being useless as a world-map navigation flag.
            if (TryGetInteriorEntranceFallback(displayedPlaceName, out var entranceZone, out var entranceX, out var entranceY))
            {
                if (TryOpenCoordinateMapLink(entranceZone, entranceX, entranceY))
                    return;
            }

            if (npcId is > 0 && npcLocations.GetBestMapLocation(npcId.Value) is { } exactNpcMap)
            {
                GameGui.OpenMapWithMapLink(exactNpcMap.TerritoryTypeId, exactNpcMap.MapId, exactNpcMap.WorldPosition);
                return;
            }

            if (!string.IsNullOrWhiteSpace(npcName) && npcLocations.GetBestMapLocation(npcName!) is { } npcMap)
            {
                GameGui.OpenMapWithMapLink(npcMap.TerritoryTypeId, npcMap.MapId, npcMap.WorldPosition);
                return;
            }

            // Location labels commonly include '(X:.., Y:..)'. Strip that suffix
            // before consulting the representative PlaceName index so a named
            // landmark/entrance can be used when the literal interior coordinate
            // cannot accept a normal flag.
            if (npcLocations.GetBestPlaceMapLocation(displayedPlaceName) is { } placeMap)
            {
                GameGui.OpenMapWithMapLink(placeMap.TerritoryTypeId, placeMap.MapId, placeMap.WorldPosition);
                return;
            }

            if (!TryParseLocationCoordinates(location, out var placeName, out var x, out var y))
                return;

            TryOpenCoordinateMapLink(placeName, x, y);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, $"Could not open map location '{location}'.");
        }
    }

    private static string GetDisplayedPlaceName(string location)
    {
        if (TryParseLocationCoordinates(location, out var placeName, out _, out _))
            return placeName;

        return (location ?? string.Empty).Trim();
    }

    private static bool TryGetInteriorEntranceFallback(
        string placeName,
        out string zone,
        out float x,
        out float y)
    {
        zone = string.Empty;
        x = 0f;
        y = 0f;

        placeName = (placeName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(placeName) ||
            !InteriorEntranceFallbacks.TryGetValue(placeName, out var fallback))
            return false;

        zone = fallback.Zone;
        x = fallback.X;
        y = fallback.Y;
        return true;
    }

    private static bool TryOpenCoordinateMapLink(string placeName, float x, float y)
    {
        var link = SeString.CreateMapLink(placeName, x, y);
        var payload = link?.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (payload is null)
            return false;

        GameGui.OpenMapWithMapLink(payload);
        return true;
    }

    private static bool TryParseLocationCoordinates(string location, out string placeName, out float x, out float y)
    {
        placeName = string.Empty;
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(location))
            return false;

        var match = Regex.Match(
            location,
            @"^(?<place>.+?)\s*\(\s*X\s*:\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*Y\s*:\s*(?<y>-?\d+(?:\.\d+)?)\s*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        placeName = match.Groups["place"].Value.Trim();
        return float.TryParse(match.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
               float.TryParse(match.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

    private void RepairSelection()
    {
        if (!string.IsNullOrWhiteSpace(config.SelectedSetId) &&
            setCatalog.Get(config.SelectedSetId) is null)
        {
            config.SelectedSetId = string.Empty;
        }

        var categories = GetBrowseCategories();
        if (categories.Count == 0)
            return;

        if (!categories.Contains(config.SelectedCategory, StringComparer.OrdinalIgnoreCase))
            config.SelectedCategory = categories.Contains("Weapons") ? "Weapons" : categories[0];

        if (!catalog.Jobs.Contains(config.SelectedJob, StringComparer.OrdinalIgnoreCase))
            config.SelectedJob = "All";

        if (config.SelectedCategory.Equals("Emotes", StringComparison.OrdinalIgnoreCase))
        {
            if (emoteCatalog.Get(config.SelectedEmoteId) is null)
                config.SelectedEmoteId = emoteCatalog.Emotes.FirstOrDefault()?.EmoteId ?? 0;
            config.SelectedItemId = 0;
            config.SelectedTitleId = 0;
        }
        else if (config.SelectedCategory.Equals("Titles", StringComparison.OrdinalIgnoreCase))
        {
            if (titleCatalog.Get(config.SelectedTitleId) is null)
                config.SelectedTitleId = titleCatalog.Titles.FirstOrDefault()?.TitleId ?? 0;
            config.SelectedItemId = 0;
            config.SelectedEmoteId = 0;
        }
        else
        {
            var validTargets = catalog.GetTargets(config.SelectedCategory, config.SelectedJob);
            var selected = catalog.GetById(config.SelectedItemId);
            if (selected is null || !validTargets.Any(t => t.ItemId == selected.ItemId))
                SelectFirstTarget();
        }

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

        if (dutyCatalog.Categories.Count > 0)
        {
            if (!dutyCatalog.Categories.Contains(config.SelectedDutyCategory, StringComparer.OrdinalIgnoreCase))
            {
                config.SelectedDutyCategory = dutyCatalog.Categories.Contains("Trials", StringComparer.OrdinalIgnoreCase)
                    ? "Trials"
                    : dutyCatalog.Categories[0];
                config.SelectedDutyId = 0;
            }

            var duty = dutyCatalog.Get(config.SelectedDutyId);
            if (duty is null || !duty.Category.Equals(config.SelectedDutyCategory, StringComparison.OrdinalIgnoreCase))
                config.SelectedDutyId = dutyCatalog.GetByCategory(config.SelectedDutyCategory).FirstOrDefault()?.ContentFinderConditionId ?? 0;
        }
    }

    private void SelectFirstTarget()
    {
        if (config.SelectedCategory.Equals("Emotes", StringComparison.OrdinalIgnoreCase))
        {
            config.SelectedItemId = 0;
            config.SelectedEmoteId = emoteCatalog.Emotes.FirstOrDefault()?.EmoteId ?? 0;
            config.SelectedTitleId = 0;
            return;
        }

        if (config.SelectedCategory.Equals("Titles", StringComparison.OrdinalIgnoreCase))
        {
            config.SelectedItemId = 0;
            config.SelectedEmoteId = 0;
            config.SelectedTitleId = titleCatalog.Titles.FirstOrDefault()?.TitleId ?? 0;
            return;
        }

        config.SelectedEmoteId = 0;
        config.SelectedTitleId = 0;
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
        Duties,
        Pins,
        Help,
    }

    private sealed record GuideStatus(
        GuideStep? NextStep,
        bool IsActive,
        int Remaining);
}
