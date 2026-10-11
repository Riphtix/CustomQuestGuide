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

    private readonly HashSet<uint> physicalItems = [];
    private readonly HashSet<uint> glamourItems = [];
    private readonly HashSet<uint> armoireItems = [];
    private readonly Dictionary<uint, OwnershipStatus> ownershipSnapshot = [];
    private readonly Dictionary<uint, bool> jobUnlockSnapshot = [];
    private bool hasSnapshot;
    private uint[] activePermanentCandidates = Array.Empty<uint>();
    private int permanentCandidateCursor;
    public double LastPollDurationMs { get; private set; }
    public int PermanentCandidateCount => activePermanentCandidates.Length;
    public int PermanentCandidateCursor => permanentCandidateCursor;

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
    /// Returns ownership from the most recent command-triggered polling window.
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
    public void BeginPolling(IEnumerable<uint> itemIds)
    {
        // Keep the last completed/partial snapshot visible while a new polling
        // window warms up. Clearing here caused every green/pink/blue state to
        // briefly disappear each time /cqg was used. Individual sources replace
        // their own captured state as fresh samples arrive.
        activePermanentCandidates = itemIds
            .Where(id => id != 0 && permanentUnlockCandidateItems.Contains(id))
            .Distinct()
            .ToArray();
        permanentCandidateCursor = 0;
        hasSnapshot = true;
    }

    /// <summary>
    /// Refresh the inexpensive ownership sources and process only a bounded number
    /// of permanent-unlock candidates. This keeps /cqg responsive while allowing
    /// ownership to settle over the command-triggered polling window.
    /// </summary>
    public unsafe void PollSnapshotStep(IEnumerable<uint> classJobIds, int maxPermanentChecks, bool refreshTransientSources)
    {
        var stopwatch = Stopwatch.StartNew();

        if (refreshTransientSources)
        {
            CapturePhysicalInventory();
            RefreshGlamourCache();
            CaptureArmoire();
            CaptureJobUnlocks(classJobIds);
        }

        if (activePermanentCandidates.Length > 0 && maxPermanentChecks > 0)
        {
            var checks = Math.Min(maxPermanentChecks, activePermanentCandidates.Length);
            for (var i = 0; i < checks; i++)
            {
                // Keep each framework slice bounded even on machines where one of
                // the native unlock checks is unexpectedly slow. Transient-source
                // refreshes are allowed a slightly larger slice because they run
                // only once every two seconds.
                if (i > 0 && stopwatch.Elapsed.TotalMilliseconds >= (refreshTransientSources ? 4.0 : 2.0))
                    break;

                if (permanentCandidateCursor >= activePermanentCandidates.Length)
                    permanentCandidateCursor = 0;

                var itemId = activePermanentCandidates[permanentCandidateCursor++];
                try
                {
                    var status = GetPermanentUnlockStatus(itemId);
                    if (status.IsOwned)
                        ownershipSnapshot[itemId] = status;
                    else
                        ownershipSnapshot.Remove(itemId);
                }
                catch (Exception ex)
                {
                    log.Debug(ex, $"Ownership polling failed for item {itemId}.");
                }
            }
        }

        stopwatch.Stop();
        LastPollDurationMs = stopwatch.Elapsed.TotalMilliseconds;
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

    private unsafe OwnershipStatus GetPermanentUnlockStatus(uint itemId)
    {
        var player = PlayerState.Instance();
        var uiState = UIState.Instance();

        if (player != null)
        {
            if (mountByItem.TryGetValue(itemId, out var mountId) && mountId != 0 && player->IsMountUnlocked(mountId))
                return new OwnershipStatus(true, "Mount Guide");

            if (orchestrionByItem.TryGetValue(itemId, out var rollId) && rollId != 0 && player->IsOrchestrionRollUnlocked(rollId))
                return new OwnershipStatus(true, "Orchestrion List");

            if (ornamentByItem.TryGetValue(itemId, out var ornamentId) && ornamentId != 0 && player->IsOrnamentUnlocked(ornamentId))
                return new OwnershipStatus(true, "Fashion Accessories");
        }

        if (uiState != null)
        {
            if (minionByItem.TryGetValue(itemId, out var minionId) && minionId != 0 && uiState->IsCompanionUnlocked(minionId))
                return new OwnershipStatus(true, "Minion Guide");

            if (cardByItem.TryGetValue(itemId, out var cardId) && cardId > 0 && cardId <= ushort.MaxValue &&
                uiState->IsTripleTriadCardUnlocked((ushort)cardId))
                return new OwnershipStatus(true, "Triple Triad Card List");

            // FFXIV exposes one generic check for items whose use permanently
            // registers/unlocks something (hairstyles, emotes and other item-action
            // collectibles in addition to the explicitly named collections above).
            // Keep the named collection checks first so the UI can tell the user
            // exactly where the ownership state came from.
            var itemRow = ExdModule.GetItemRowById(itemId);
            if (itemRow != null && uiState->IsItemActionUnlocked(itemRow) == 1)
                return new OwnershipStatus(true, "Registered / Collection Unlock");
        }

        return OwnershipStatus.Missing;
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
