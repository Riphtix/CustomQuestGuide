using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace CustomQuestGuide;

public sealed record OwnershipStatus(bool IsOwned, string Source)
{
    public static OwnershipStatus Missing { get; } = new(false, string.Empty);
}

/// <summary>
/// Character-local ownership/unlock detector used everywhere CQG renders a target.
/// It intentionally combines physical possession with permanent collection unlocks:
/// inventory/armoury/saddlebags, cached glamour dresser, loaded armoire, and the
/// game's mount/minion/card/orchestrion/ornament unlock flags.
/// </summary>
public sealed class OwnershipIndex
{
    private readonly IDataManager data;
    private readonly IGameInventory inventory;
    private readonly IPlayerState playerState;
    private readonly Configuration config;
    private readonly System.Action saveConfig;
    private readonly IPluginLog log;

    private readonly Dictionary<uint, uint> cabinetByItem = [];
    private readonly Dictionary<uint, uint> mountByItem = [];
    private readonly Dictionary<uint, uint> minionByItem = [];
    private readonly Dictionary<uint, uint> cardByItem = [];
    private readonly Dictionary<uint, uint> orchestrionByItem = [];
    private readonly Dictionary<uint, uint> ornamentByItem = [];
    private readonly HashSet<uint> permanentUnlockCandidateItems = [];
    private readonly HashSet<uint> explicitPermanentUnlockCandidateItems = [];
    // ItemAction rows that the game itself has told us do not carry any unlock
    // state (IsItemActionUnlocked == 4). This is static item metadata, so once
    // learned it is safe to skip for later /cqg scans in the same plugin session.
    private readonly HashSet<uint> knownNonUnlockableItemActions = [];

    private readonly HashSet<uint> physicalItems = [];
    private readonly HashSet<uint> glamourItems = [];
    private readonly HashSet<uint> armoireItems = [];
    private readonly Dictionary<uint, OwnershipStatus> ownershipSnapshot = [];
    private readonly Dictionary<uint, bool> jobUnlockSnapshot = [];
    private bool hasSnapshot;
    private uint[] activePermanentCandidates = Array.Empty<uint>();
    private int permanentCandidateCursor;
    private ulong snapshotContentId;

    public int PermanentCandidateCount => activePermanentCandidates.Length;
    public int PermanentCandidateCursor => permanentCandidateCursor;
    public bool PermanentPollingComplete => permanentCandidateCursor >= activePermanentCandidates.Length;

    /// <summary>
    /// Clears all cached ownership state for the currently loaded character so the
    /// next /cqg pass behaves like a cold ownership scan. This intentionally does
    /// not touch acquisition-source, duty-unlock, or community resolver caches.
    /// The saved Glamour Dresser snapshot for the current character is also
    /// removed because it is item-ownership cache; opening the dresser again will
    /// repopulate it from authoritative live data.
    /// </summary>
    public bool ClearCachedOwnershipForCurrentCharacter()
    {
        activePermanentCandidates = Array.Empty<uint>();
        permanentCandidateCursor = 0;

        physicalItems.Clear();
        glamourItems.Clear();
        armoireItems.Clear();
        knownNonUnlockableItemActions.Clear();
        ownershipSnapshot.Clear();
        jobUnlockSnapshot.Clear();
        snapshotContentId = 0;
        hasSnapshot = false;

        var removedPersistentGlamourSnapshot = false;
        try
        {
            var contentId = playerState.IsLoaded ? playerState.ContentId : 0;
            if (contentId != 0)
            {
                var snapshotKey = contentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                removedPersistentGlamourSnapshot = config.GlamourDresserSnapshots.Remove(snapshotKey);
                if (removedPersistentGlamourSnapshot)
                    saveConfig();
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not clear the current character Glamour Dresser ownership snapshot.");
        }

        return removedPersistentGlamourSnapshot;
    }

    private static readonly GameInventoryType[] PhysicalContainers =
    [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
        GameInventoryType.EquippedItems,
        GameInventoryType.ArmoryMainHand,
        GameInventoryType.ArmoryOffHand,
        GameInventoryType.ArmoryHead,
        GameInventoryType.ArmoryBody,
        GameInventoryType.ArmoryHands,
        GameInventoryType.ArmoryWaist,
        GameInventoryType.ArmoryLegs,
        GameInventoryType.ArmoryFeets,
        GameInventoryType.ArmoryEar,
        GameInventoryType.ArmoryNeck,
        GameInventoryType.ArmoryWrist,
        GameInventoryType.ArmoryRings,
        GameInventoryType.ArmorySoulCrystal,
        GameInventoryType.SaddleBag1,
        GameInventoryType.SaddleBag2,
        GameInventoryType.PremiumSaddleBag1,
        GameInventoryType.PremiumSaddleBag2,
        // Retainer containers only return live slots when Dalamud/the client has
        // that inventory available; unavailable pages are safely ignored below.
        GameInventoryType.RetainerPage1,
        GameInventoryType.RetainerPage2,
        GameInventoryType.RetainerPage3,
        GameInventoryType.RetainerPage4,
        GameInventoryType.RetainerPage5,
        GameInventoryType.RetainerPage6,
        GameInventoryType.RetainerPage7,
        GameInventoryType.RetainerEquippedItems,
    ];

    public OwnershipIndex(
        IDataManager data,
        IGameInventory inventory,
        IPlayerState playerState,
        Configuration config,
        System.Action saveConfig,
        IPluginLog log)
    {
        this.data = data;
        this.inventory = inventory;
        this.playerState = playerState;
        this.config = config;
        this.saveConfig = saveConfig;
        this.log = log;
        BuildStaticMaps();
    }

    /// <summary>
    /// Returns the job-unlock state captured by the most recent command-triggered
    /// polling sample. This method never reads live client state.
    /// </summary>
    public bool IsJobUnlocked(uint classJobId) =>
        hasSnapshot && jobUnlockSnapshot.TryGetValue(classJobId, out var unlocked) && unlocked;

    /// <summary>
    /// Returns ownership from the most recent command-triggered one-shot scan.
    /// Rendering never performs live ownership queries.
    /// </summary>
    public OwnershipStatus GetItemStatus(uint itemId)
    {
        if (itemId == 0 || !hasSnapshot)
            return OwnershipStatus.Missing;

        if (ownershipSnapshot.TryGetValue(itemId, out var permanent) && permanent.IsOwned)
            return permanent;
        if (physicalItems.Contains(itemId))
            return new OwnershipStatus(true, "Inventory / Armoury / Saddlebag / Retainer");
        if (glamourItems.Contains(itemId))
            return new OwnershipStatus(true, "Glamour Dresser");
        if (armoireItems.Contains(itemId))
            return new OwnershipStatus(true, "Armoire");

        return OwnershipStatus.Missing;
    }

    /// <summary>
    /// Starts a fresh ownership polling pass without doing the expensive permanent-
    /// unlock scan synchronously. The caller can then advance that scan in small
    /// chunks from subsequent framework updates.
    /// </summary>
    public void BeginPolling(
        IEnumerable<uint> selectedItemIds,
        IEnumerable<uint> pinnedItemIds,
        IEnumerable<uint> allItemIds)
    {
        // Permanent collection unlocks belong to one character. If the user
        // switches characters without reloading the plugin, never reuse the prior
        // character's permanent ownership snapshot.
        var currentContentId = 0UL;
        try
        {
            currentContentId = playerState.IsLoaded ? playerState.ContentId : 0;
        }
        catch
        {
            currentContentId = 0;
        }

        if (snapshotContentId != 0 && currentContentId != 0 && snapshotContentId != currentContentId)
        {
            ownershipSnapshot.Clear();
            jobUnlockSnapshot.Clear();
            physicalItems.Clear();
            glamourItems.Clear();
            armoireItems.Clear();
            hasSnapshot = false;
        }
        if (currentContentId != 0)
            snapshotContentId = currentContentId;

        // Build a deterministic one-shot priority plan. Visible targets and pins
        // go first. Irreversible permanent unlocks already known owned do not need
        // to be re-queried. ItemAction rows previously proven to have no unlock
        // state are also excluded; that capability result is static for the item.
        var seen = new HashSet<uint>();
        var ordered = new List<uint>();

        bool ShouldQueue(uint id)
        {
            if (id == 0 || !permanentUnlockCandidateItems.Contains(id))
                return false;
            if (knownNonUnlockableItemActions.Contains(id))
                return false;
            if (ownershipSnapshot.TryGetValue(id, out var owned) && owned.IsOwned)
                return false;
            return true;
        }

        void AddGroup(IEnumerable<uint> ids)
        {
            foreach (var id in ids)
            {
                if (!seen.Add(id) || !ShouldQueue(id))
                    continue;
                ordered.Add(id);
            }
        }

        var all = allItemIds
            .Where(id => id != 0 && permanentUnlockCandidateItems.Contains(id))
            .Distinct()
            .ToArray();

        AddGroup(selectedItemIds);
        AddGroup(pinnedItemIds);
        AddGroup(all.Where(id => explicitPermanentUnlockCandidateItems.Contains(id)));
        AddGroup(all.Where(id => !explicitPermanentUnlockCandidateItems.Contains(id)));

        activePermanentCandidates = ordered.ToArray();
        permanentCandidateCursor = 0;
        hasSnapshot = true;
    }

    /// <summary>
    /// Moves still-unchecked permanent candidates to the front of the remaining
    /// one-shot scan. Already-checked candidates are never repeated.
    /// </summary>
    public int PromoteCandidates(IEnumerable<uint> itemIds, string reason)
    {
        if (PermanentPollingComplete || activePermanentCandidates.Length == 0)
            return 0;

        var requested = itemIds
            .Where(id => id != 0 && permanentUnlockCandidateItems.Contains(id))
            .ToHashSet();
        if (requested.Count == 0)
            return 0;

        var prefix = activePermanentCandidates.Take(permanentCandidateCursor);
        var remaining = activePermanentCandidates.Skip(permanentCandidateCursor).ToArray();
        var promoted = remaining.Where(requested.Contains).ToArray();
        if (promoted.Length == 0)
            return 0;

        var promotedSet = promoted.ToHashSet();
        activePermanentCandidates = prefix
            .Concat(promoted)
            .Concat(remaining.Where(id => !promotedSet.Contains(id)))
            .ToArray();

        _ = reason; // Reason is retained for call-site readability; no scan profiling is persisted.
        return promoted.Length;
    }

    public void FinishPolling()
    {
        // One-shot scan completion marker only; no profiling or output is recorded.
    }

    /// <summary>
    /// Refresh transient ownership once and process a bounded one-shot slice of
    /// permanent-unlock candidates. The caller invokes this only while an explicit
    /// /cqg scan is active; after the queue finishes no ownership work continues.
    /// </summary>
    public unsafe void PollSnapshotStep(IEnumerable<uint> classJobIds, int maxPermanentChecks, bool refreshTransientSources)
    {
        var stopwatch = Stopwatch.StartNew();
        // Keep ownership work comfortably below a frame. The high candidate cap
        // prevents an artificial throughput ceiling; the time budget is the real
        // guardrail.
        const double permanentSliceBudgetMs = 1.25;

        if (refreshTransientSources)
        {
            CapturePhysicalInventory();
            RefreshGlamourCache();
            CaptureArmoire();
            CaptureJobUnlocks(classJobIds);
        }

        if (!PermanentPollingComplete && maxPermanentChecks > 0)
        {
            var remaining = activePermanentCandidates.Length - permanentCandidateCursor;
            var checks = Math.Min(maxPermanentChecks, remaining);
            for (var i = 0; i < checks && permanentCandidateCursor < activePermanentCandidates.Length; i++)
            {
                // Keep each framework slice bounded even on machines where one of
                // the native unlock checks is unexpectedly slow. The overall pass
                // has no wall-clock timeout; it simply continues on later frames.
                if (i > 0 && stopwatch.Elapsed.TotalMilliseconds >=
                    (refreshTransientSources ? 3.0 : permanentSliceBudgetMs))
                    break;

                var itemId = activePermanentCandidates[permanentCandidateCursor++];
                try
                {
                    var result = GetPermanentUnlockStatus(itemId);
                    if (!result.HasUnlockStatus)
                    {
                        knownNonUnlockableItemActions.Add(itemId);
                        ownershipSnapshot.Remove(itemId);
                    }
                    else if (result.Status.IsOwned)
                    {
                        ownershipSnapshot[itemId] = result.Status;
                    }
                    else
                    {
                        ownershipSnapshot.Remove(itemId);
                    }
                }
                catch (Exception ex)
                {
                    log.Debug(ex, $"Ownership polling failed for item {itemId}.");
                }

            }

        }

        stopwatch.Stop();
    }

    private unsafe void CaptureJobUnlocks(IEnumerable<uint> classJobIds)
    {
        try
        {
            var player = PlayerState.Instance();
            foreach (var classJobId in classJobIds)
            {
                if (classJobId == 0)
                    continue;

                var unlocked = false;
                try
                {
                    unlocked = player != null && player->IsLoaded &&
                               player->GetClassJobLevel((int)classJobId, false) > 0;
                }
                catch
                {
                    // Keep the captured value false when client state is unavailable.
                }
                jobUnlockSnapshot[classJobId] = unlocked;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Job ownership polling failed.");
        }
    }

    private readonly record struct PermanentUnlockCheckResult(OwnershipStatus Status, bool HasUnlockStatus);

    private unsafe PermanentUnlockCheckResult GetPermanentUnlockStatus(uint itemId)
    {
        var player = PlayerState.Instance();
        var uiState = UIState.Instance();

        if (player != null)
        {
            if (mountByItem.TryGetValue(itemId, out var mountId) && mountId != 0 && player->IsMountUnlocked(mountId))
                return new PermanentUnlockCheckResult(new OwnershipStatus(true, "Mount Guide"), true);

            if (orchestrionByItem.TryGetValue(itemId, out var rollId) && rollId != 0 && player->IsOrchestrionRollUnlocked(rollId))
                return new PermanentUnlockCheckResult(new OwnershipStatus(true, "Orchestrion List"), true);

            if (ornamentByItem.TryGetValue(itemId, out var ornamentId) && ornamentId != 0 && player->IsOrnamentUnlocked(ornamentId))
                return new PermanentUnlockCheckResult(new OwnershipStatus(true, "Fashion Accessories"), true);
        }

        if (uiState != null)
        {
            if (minionByItem.TryGetValue(itemId, out var minionId) && minionId != 0 && uiState->IsCompanionUnlocked(minionId))
                return new PermanentUnlockCheckResult(new OwnershipStatus(true, "Minion Guide"), true);

            if (cardByItem.TryGetValue(itemId, out var cardId) && cardId > 0 && cardId <= ushort.MaxValue &&
                uiState->IsTripleTriadCardUnlocked((ushort)cardId))
                return new PermanentUnlockCheckResult(new OwnershipStatus(true, "Triple Triad Card List"), true);

            // FFXIV exposes one generic check for items whose use permanently
            // registers/unlocks something (hairstyles, emotes and other item-action
            // collectibles in addition to the explicitly named collections above).
            // Keep the named collection checks first so the UI can tell the user
            // exactly where the ownership state came from.
            var itemRow = ExdModule.GetItemRowById(itemId);
            if (itemRow != null)
            {
                var unlockState = uiState->IsItemActionUnlocked(itemRow);
                if (unlockState == 1)
                    return new PermanentUnlockCheckResult(new OwnershipStatus(true, "Registered / Collection Unlock"), true);
                if (unlockState == 2 || unlockState == 3)
                    return new PermanentUnlockCheckResult(OwnershipStatus.Missing, true);
                if (unlockState == 4)
                    return new PermanentUnlockCheckResult(OwnershipStatus.Missing, false);
            }
        }

        // If the relevant game state is temporarily unavailable, keep the item as
        // unlock-capable so it will be eligible on the next explicit /cqg scan.
        return new PermanentUnlockCheckResult(OwnershipStatus.Missing, true);
    }

    private void CapturePhysicalInventory()
    {
        physicalItems.Clear();
        foreach (var container in PhysicalContainers)
        {
            try
            {
                foreach (var item in inventory.GetInventoryItems(container))
                {
                    if (!item.IsEmpty && item.BaseItemId != 0 && item.Quantity > 0)
                        physicalItems.Add(item.BaseItemId);
                }
            }
            catch
            {
                // A container can be unavailable depending on character/account state.
            }
        }
    }

    private unsafe void RefreshGlamourCache()
    {
        glamourItems.Clear();

        // Start from the last authoritative per-character snapshot. FFXIV clears
        // the live Glamour Dresser cache on zone changes, so an unloaded cache
        // means "unknown right now", not "the dresser is empty".
        var contentId = playerState.IsLoaded ? playerState.ContentId : 0;
        var snapshotKey = contentId != 0 ? contentId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        if (snapshotKey.Length != 0 &&
            config.GlamourDresserSnapshots.TryGetValue(snapshotKey, out var snapshot))
        {
            foreach (var stored in snapshot)
            {
                if (stored != 0)
                    glamourItems.Add(stored);
            }
        }

        try
        {
            var liveItems = new HashSet<uint>();
            var liveLoaded = false;

            var finder = ItemFinderModule.Instance();
            if (finder != null && finder->IsGlamourDresserCached)
            {
                liveLoaded = true;
                foreach (var stored in finder->GlamourDresserItemIds)
                {
                    if (stored != 0)
                        liveItems.Add(stored);
                }
            }

            var mirage = MirageManager.Instance();
            if (mirage != null && mirage->PrismBoxLoaded)
            {
                liveLoaded = true;
                foreach (var stored in mirage->PrismBoxItemIds)
                {
                    if (stored != 0)
                        liveItems.Add(stored);
                }
            }

            if (!liveLoaded)
                return;

            // A loaded dresser is authoritative. Replace the old snapshot,
            // including removals, and immediately use it for ownership checks.
            glamourItems.Clear();
            foreach (var stored in liveItems)
                glamourItems.Add(stored);

            if (snapshotKey.Length != 0)
            {
                var ordered = liveItems.OrderBy(id => id).ToList();
                if (!config.GlamourDresserSnapshots.TryGetValue(snapshotKey, out var previous) ||
                    !previous.SequenceEqual(ordered))
                {
                    config.GlamourDresserSnapshots[snapshotKey] = ordered;
                    saveConfig();
                }
            }
        }
        catch
        {
            // Keep the last known snapshot when live dresser data is unavailable.
        }
    }

    private unsafe void CaptureArmoire()
    {
        try
        {
            var ui = UIState.Instance();
            if (ui == null)
                return;

            var cabinet = &ui->Cabinet;
            if (!cabinet->IsCabinetLoaded())
                return;

            // Only replace the known Armoire state when the client says that
            // cabinet data is actually loaded. "Not loaded" is unknown, not empty.
            armoireItems.Clear();
            foreach (var pair in cabinetByItem)
            {
                try
                {
                    if (cabinet->IsItemInCabinet(pair.Value))
                        armoireItems.Add(pair.Key);
                }
                catch
                {
                    // One malformed/unavailable cabinet row must not abort the snapshot.
                }
            }
        }
        catch
        {
            // An unloaded armoire remains absent from this command-time snapshot.
        }
    }

    private void BuildStaticMaps()
    {
        try
        {
            foreach (var row in data.GetExcelSheet<CabinetSheet>())
            {
                var itemId = ReflectionUtil.GetNestedRowId(row, "Item");
                if (itemId != 0)
                    cabinetByItem[itemId] = row.RowId;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build armoire item map.");
        }

        try
        {
            foreach (var item in data.GetExcelSheet<Item>())
            {
                if (item.RowId == 0 || !item.ItemAction.IsValid)
                    continue;

                permanentUnlockCandidateItems.Add(item.RowId);

                try
                {
                    var action = item.ItemAction.Value;
                    var actionId = ReflectionUtil.GetNestedRowId(action, "Action");
                    if (actionId == 0)
                    {
                        var raw = ReflectionUtil.GetNumericProperty(action, "Type", "ActionType");
                        if (raw.HasValue && raw.Value > 0 && raw.Value <= uint.MaxValue)
                            actionId = (uint)raw.Value;
                    }

                    var data0 = FirstUInt(ReflectionUtil.GetPropertyValue(action, "Data"));
                    var additionalData = ReadItemAdditionalData(item);

                    // These ItemAction IDs are known registration/collection
                    // actions. They are prioritized ahead of generic capability
                    // discovery because they are the high-value ownership checks.
                    if (actionId is 1322 or 853 or 3357 or 25183 or 20086 or 2633 or 1013)
                        explicitPermanentUnlockCandidateItems.Add(item.RowId);

                    switch (actionId)
                    {
                        case 1322 when data0 != 0:
                            mountByItem[item.RowId] = data0;
                            break;
                        case 853 when data0 != 0:
                            minionByItem[item.RowId] = data0;
                            break;
                        case 3357 when data0 != 0:
                            cardByItem[item.RowId] = data0;
                            break;
                        case 25183 when additionalData != 0:
                            orchestrionByItem[item.RowId] = additionalData;
                            break;
                        case 20086 when data0 != 0:
                            ornamentByItem[item.RowId] = data0;
                            break;
                    }
                }
                catch
                {
                    // One unusual action row must not break the ownership index.
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build unlock-item ownership maps.");
        }
    }

    private static uint ReadItemAdditionalData(Item item)
    {
        var nested = ReflectionUtil.GetNestedRowId(item, "AdditionalData");
        if (nested != 0)
            return nested;
        var numeric = ReflectionUtil.GetNumericProperty(item, "AdditionalData");
        return numeric.HasValue && numeric.Value > 0 && numeric.Value <= uint.MaxValue
            ? (uint)numeric.Value
            : 0;
    }

    private static uint FirstUInt(object? value)
    {
        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var entry in enumerable)
            {
                try
                {
                    var number = Convert.ToUInt32(entry);
                    if (number != 0)
                        return number;
                }
                catch
                {
                    var nested = ReflectionUtil.ExtractRowId(entry);
                    if (nested != 0)
                        return nested;
                }
            }
        }

        try
        {
            return value is null ? 0 : Convert.ToUInt32(value);
        }
        catch
        {
            return 0;
        }
    }
}
