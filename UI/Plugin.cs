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

public sealed partial class Plugin : IDalamudPlugin
{
    private uint autoEnrichmentRequestedItemId;

    // Selected-target enrichment is allowed a small, bounded self-heal window.
    // This prevents a transient provider failure from leaving the currently
    // selected source permanently half-resolved, while still honoring CQG's
    // no-persistent-polling requirement.
    private readonly Dictionary<uint, int> selectedEnrichmentRecoveryAttempts = new();
    private readonly Dictionary<uint, DateTime> nextSelectedEnrichmentRecoveryUtc = new();
    private const int MaxSelectedEnrichmentRecoveryAttempts = 2;

    private const string CommandName = "/cqg";
    private static readonly Vector4 OwnedGreen = new(0.35f, 1.00f, 0.45f, 1.00f);
    private static readonly Vector4 PartialBlue = new(0.35f, 0.70f, 1.00f, 1.00f);
    private static readonly Vector4 GlamourPink = new(1.00f, 0.62f, 0.78f, 1.00f);
    private static readonly Vector4 UnresolvedRed = new(1.00f, 0.30f, 0.30f, 1.00f);
    private static readonly Vector4 AvailabilityWarning = new(1.00f, 0.72f, 0.28f, 1.00f);

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
    private readonly OnlineStoreLookupIndex onlineStoreLookup;
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

    // Session-only browser-style navigation history. Target changes create
    // entries; merely switching top-level tabs does not. This is intentionally
    // not persisted to Configuration.
    private const int NavigationHistoryLimit = 100;
    private readonly List<NavigationState> navigationBack = [];
    private readonly List<NavigationState> navigationForward = [];

    // Session-only crafting drill-down path. This is deliberately separate from
    // the general Back/Forward history: breadcrumbs exist only when the player
    // opens an ingredient from a recipe and never become universal navigation.
    private readonly List<uint> craftingBreadcrumbPath = [];

    private bool restoringNavigationState;
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
        onlineStoreLookup = new OnlineStoreLookupIndex(
            PluginInterface.GetPluginConfigDirectory(),
            Log,
            networkRequests);
        garlandSources = new GarlandSourceIndex(
            DataManager,
            PluginInterface.GetPluginConfigDirectory(),
            GameDataFingerprint.Create(DataManager),
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
        onlineStoreLookup.Dispose();
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
        if (!ClientState.IsLoggedIn)
            return;

        // Selected-source recovery is lifecycle work, not rendering work. Keep
        // it on the framework update path so drawing the UI never causes hidden
        // network side effects. Only the item currently visible in CQG is
        // eligible, and ReconcileSelectedTargetEnrichment() strictly bounds
        // automatic retry attempts.
        if (windowOpen &&
            config.SelectedItemId != 0 &&
            catalog.GetById(config.SelectedItemId) is { } selectedTarget)
        {
            ReconcileSelectedTargetEnrichment(selectedTarget);
        }

        if (!ownershipPollingActive)
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
        // The loot-window marker is independent of the CQG main window and must
        // remain visible while Need/Greed is open even when CQG itself is closed.
        DrawLootTargetIndicators();

        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(780, 600), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Custom Quest Guide###CustomQuestGuide", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        DrawMainTabs();
        DrawNavigationHistoryControls();
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
        {
            if (activeTab != MainTab.Pins)
            {
                pinnedUiDerivedCache.Clear();
                nextPinnedUiRefreshUtc = DateTime.MinValue;
            }
            activeTab = MainTab.Pins;
        }

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
        ImGui.TextWrapped("How do I open a location on the map from CQG?");
        ImGui.BulletText("Map-opening actions use the label View Map so they are distinct from CQG persistent target pins.");
        ImGui.BulletText("When CQG has an authoritative NPC placement or explicit map coordinates, the displayed location may also be clickable and opens the in-game map at that location.");
        ImGui.BulletText("Pin Target, Pin Item, Pin Duty, and similar Pin/Unpin actions always refer to CQG persistent targets, never a temporary map flag.");

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
        ImGui.BulletText("Broad source refreshes have a 5-minute cooldown. They refresh compact community indexes only; Garland item documents remain selected-target scoped.");
        ImGui.BulletText("If one selected target is stale or incomplete, use Rebuild selected source. It clears only that target's external source/enrichment cache and immediately reacquires what that target needs without wiping unrelated caches.");
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
            NavigateTo(() =>
            {
                config.SelectedSetId = string.Empty;
                config.SelectedRelicJob = string.Empty;
                activeTab = MainTab.Items;
            });
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

    private void ResetSelectedEnrichmentRecovery(uint itemId)
    {
        if (itemId == 0)
            return;

        selectedEnrichmentRecoveryAttempts[itemId] = 0;
        nextSelectedEnrichmentRecoveryUtc[itemId] = DateTime.MinValue;
    }

    private int GetSelectedEnrichmentRecoveryAttempts(uint itemId) =>
        itemId != 0 && selectedEnrichmentRecoveryAttempts.TryGetValue(itemId, out var attempts)
            ? attempts
            : 0;

    private DateTime GetNextSelectedEnrichmentRecoveryUtc(uint itemId) =>
        itemId != 0 && nextSelectedEnrichmentRecoveryUtc.TryGetValue(itemId, out var nextUtc)
            ? nextUtc
            : DateTime.MinValue;

    /// <summary>
    /// Keeps the actively-selected target from becoming stranded in a partial
    /// enrichment state after a transient provider failure. Recovery bookkeeping
    /// is target-scoped, not selection-scoped, so browser Back/Forward never
    /// resets a target's lifecycle or causes an already-visited item to start its
    /// enrichment sequence over from scratch. Provider-validated missing data is
    /// not retried, and there is no background sweep or indefinite polling.
    /// </summary>
    private void ReconcileSelectedTargetEnrichment(TargetEntry target)
    {
        var monsterCandidates = resolver.GetMonsterEnrichmentCandidates(target);
        var monsterPipelineOwnsMissingMetadata = garlandSources.EnsureMonsterEnrichment(
            target.ItemId,
            monsterCandidates);

        if (!resolver.HasIncompleteMonsterResolution(target))
        {
            selectedEnrichmentRecoveryAttempts[target.ItemId] = 0;
            nextSelectedEnrichmentRecoveryUtc[target.ItemId] = DateTime.MinValue;
            return;
        }

        // Exact monster identities are enriched by their own keyed pipeline.
        // Once that pipeline owns the missing metadata, never re-download the
        // parent item document as a side effect of a slow/failed mob request.
        if (monsterPipelineOwnsMissingMetadata)
            return;

        // At this point the item document itself is the missing discovery step
        // (for example, local data knows a monster name but not exact identity).
        if (!resolver.NeedsGarlandEnrichment(target))
            return;

        var attempts = GetSelectedEnrichmentRecoveryAttempts(target.ItemId);
        var nextRecoveryUtc = GetNextSelectedEnrichmentRecoveryUtc(target.ItemId);

        if (garlandSources.IsRequestInFlight(target.ItemId) ||
            attempts >= MaxSelectedEnrichmentRecoveryAttempts ||
            DateTime.UtcNow < nextRecoveryUtc)
        {
            return;
        }

        attempts++;
        selectedEnrichmentRecoveryAttempts[target.ItemId] = attempts;
        nextSelectedEnrichmentRecoveryUtc[target.ItemId] = DateTime.UtcNow.AddSeconds(
            attempts == 1 ? 4 : 10);

        garlandSources.EnsureRequested(
            target.ItemId,
            requireAdditionalEnrichment: true);
    }

    private void RequestTargetScopedSourceEnrichment(
        TargetEntry target,
        bool forceSelectedItemRequest = false)
    {
        // Selected collectible/event targets should enrich themselves without
        // requiring the user to press the broad Refresh acquisition sources
        // button. The index limits this to the collection families relevant to
        // this one target and merges the result live when the request completes.
        _ = collectibleSources.EnsureTargetAsync(target);

        // Only a user-selected target may trigger Garland item enrichment.
        // Never bulk-query Garland from /cqg, startup, drawing, or background
        // ownership scanning. Positive results and misses are cached by the
        // index, so repeatedly selecting the same target does not spam HTTP.
        //
        // A manual "Rebuild selected source" is intentionally different from
        // ordinary selection. The rebuild action first invalidates this target's
        // persisted external cache, then forces one fresh selected-item request.
        // That guarantees the button cannot immediately reuse the stale answer
        // it was explicitly asked to discard, even if local data alone currently
        // makes the source family look complete.
        var monsterCandidates = resolver.GetMonsterEnrichmentCandidates(target);
        var monsterPipelineOwnsMissingMetadata =
            garlandSources.EnsureMonsterEnrichment(target.ItemId, monsterCandidates);

        var cachedItemNeedsRevalidation =
            garlandSources.IsChecked(target.ItemId) &&
            !garlandSources.IsValidatedForCurrentGameData(target.ItemId);

        if (forceSelectedItemRequest ||
            cachedItemNeedsRevalidation ||
            (!monsterPipelineOwnsMissingMetadata && resolver.NeedsGarlandEnrichment(target)))
        {
            // Item documents discover source families/identities. Dedicated
            // source-family enrichment (currently monster metadata) is scheduled
            // separately and must not force the item document to be downloaded
            // again merely because enrichment is still progressing.
            garlandSources.EnsureRequested(
                target.ItemId,
                requireAdditionalEnrichment: true);
        }

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

    private void SelectItemTarget(TargetEntry target, bool clearCraftingBreadcrumbs = true)
    {
        if (clearCraftingBreadcrumbs)
            craftingBreadcrumbPath.Clear();

        // Explicit selection is always a fresh opportunity to satisfy any
        // missing target-scoped enrichment. DrawItemsTab will see the marker
        // already set after this request and will not queue a duplicate.
        autoEnrichmentRequestedItemId = target.ItemId;
        NavigateTo(() =>
        {
            config.SelectedSetId = string.Empty;
            config.SelectedCategory = target.Category;
            config.SelectedJob = "All";
            config.SelectedItemId = target.ItemId;
            config.SelectedChainIndex = 0;
            activeTab = MainTab.Items;
        });
        ResetSelectedEnrichmentRecovery(target.ItemId);
        RequestTargetScopedSourceEnrichment(target);
        PromoteActiveScanTargets(
            new[] { target.ItemId },
            GetRelatedDutyNames(target),
            $"selected item: {target.Name}");
    }


    private void SelectCraftingIngredientTarget(uint parentItemId, TargetEntry target)
    {
        // Build the path only from explicit recipe drill-down. If the current
        // path no longer represents the parent, start a fresh crafting trail.
        if (craftingBreadcrumbPath.Count == 0 || craftingBreadcrumbPath[^1] != parentItemId)
        {
            craftingBreadcrumbPath.Clear();
            craftingBreadcrumbPath.Add(parentItemId);
        }

        var existingIndex = craftingBreadcrumbPath.IndexOf(target.ItemId);
        if (existingIndex >= 0)
        {
            if (existingIndex + 1 < craftingBreadcrumbPath.Count)
                craftingBreadcrumbPath.RemoveRange(existingIndex + 1, craftingBreadcrumbPath.Count - existingIndex - 1);
        }
        else
        {
            craftingBreadcrumbPath.Add(target.ItemId);
        }

        SelectItemTarget(target, clearCraftingBreadcrumbs: false);
    }

    private void NavigateCraftingBreadcrumbTo(int index)
    {
        if (index < 0 || index >= craftingBreadcrumbPath.Count)
            return;

        var itemId = craftingBreadcrumbPath[index];
        var target = catalog.GetById(itemId);
        if (target is null)
        {
            craftingBreadcrumbPath.Clear();
            return;
        }

        if (index + 1 < craftingBreadcrumbPath.Count)
            craftingBreadcrumbPath.RemoveRange(index + 1, craftingBreadcrumbPath.Count - index - 1);

        SelectItemTarget(target, clearCraftingBreadcrumbs: false);
    }

    private void SelectSetTarget(TargetSet set, string relicJob = "")
    {
        NavigateTo(() =>
        {
            config.SelectedSetId = set.Id;
            config.SelectedRelicJob = relicJob ?? string.Empty;
            activeTab = MainTab.Items;
        });

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
        NavigateTo(() =>
        {
            config.SelectedSetId = string.Empty;
            config.SelectedCategory = "Emotes";
            config.SelectedItemId = 0;
            config.SelectedEmoteId = emote.EmoteId;
            config.SelectedTitleId = 0;
            activeTab = MainTab.Items;
        });

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
        NavigateTo(() =>
        {
            config.SelectedSetId = string.Empty;
            config.SelectedCategory = "Titles";
            config.SelectedItemId = 0;
            config.SelectedEmoteId = 0;
            config.SelectedTitleId = title.TitleId;
            activeTab = MainTab.Items;
        });
    }

    private void SelectJobTarget(JobProgressionTarget job)
    {
        NavigateTo(() =>
        {
            config.SelectedProgressionCategory = job.Category;
            config.SelectedClassJobId = job.ClassJobId;
            activeTab = MainTab.Jobs;
        });
    }

    private void SelectDutyTarget(DutyTarget duty)
    {
        NavigateTo(() =>
        {
            config.SelectedDutyCategory = duty.Category;
            config.SelectedDutyId = duty.ContentFinderConditionId;
            activeTab = MainTab.Duties;
        });
        PromoteActiveScanTargets(
            null,
            new[] { duty.Name },
            $"selected duty: {duty.Name}");
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
        // Any persisted pin/order/target mutation may change the derived text or
        // route shown on the Pins tab. Drop its snapshot eagerly; the next draw
        // rebuilds once, then the tab remains stable for another 30 seconds.
        pinnedUiDerivedCache.Clear();
        nextPinnedUiRefreshUtc = DateTime.UtcNow.Add(PinnedUiRefreshInterval);

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

    private sealed record NavigationState(
        MainTab Tab,
        string SelectedCategory,
        string SelectedJob,
        uint SelectedItemId,
        string SelectedSetId,
        string SelectedRelicJob,
        ushort SelectedEmoteId,
        ushort SelectedTitleId,
        string SelectedProgressionCategory,
        uint SelectedClassJobId,
        string SelectedDutyCategory,
        uint SelectedDutyId,
        int SelectedChainIndex);

    private sealed record GuideStatus(
        GuideStep? NextStep,
        bool IsActive,
        int Remaining);
}
