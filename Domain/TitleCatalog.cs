using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Achievement-backed title targets. Titles are not inventory items, so they are
/// kept separate from TargetCatalog while sharing the Items-tab browsing model.
/// Ownership is snapshotted during the same explicit /cqg one-shot refresh used
/// by the rest of CQG; rendering never polls the client continuously.
/// </summary>
public sealed class TitleCatalog
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly List<TitleTarget> titles = [];
    private readonly Dictionary<ushort, TitleTarget> byId = [];
    private readonly HashSet<ushort> unlockedSnapshot = [];

    private bool polling;
    private bool requested;
    private int waitFrames;

    public IReadOnlyList<TitleTarget> Titles => titles;
    public bool PollingComplete => !polling;
    public bool HasOwnershipSnapshot { get; private set; }

    public TitleCatalog(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
        Build();
    }

    public TitleTarget? Get(ushort titleId) =>
        titleId != 0 && byId.TryGetValue(titleId, out var title) ? title : null;

    public void BeginPolling()
    {
        polling = titles.Count > 0;
        requested = false;
        waitFrames = 0;
    }

    public unsafe void PollSnapshotStep()
    {
        if (!polling)
            return;

        var ui = UIState.Instance();
        if (ui == null)
        {
            FinishUnavailable();
            return;
        }

        var list = &ui->TitleList;
        if (!list->DataReceived)
        {
            if (!requested)
            {
                requested = true;
                try { list->RequestTitleList(); }
                catch (Exception ex) { log.Debug(ex, "Could not request the title ownership list."); }
            }

            // Never let title-state availability keep the /cqg one-shot scan alive
            // indefinitely. A later /cqg can retry after the client has loaded it.
            if (++waitFrames >= 300)
                FinishUnavailable();
            return;
        }

        unlockedSnapshot.Clear();
        foreach (var title in titles)
        {
            try
            {
                if (list->IsTitleUnlocked(title.TitleId))
                    unlockedSnapshot.Add(title.TitleId);
            }
            catch
            {
                // One malformed/unavailable row must not abort the snapshot.
            }
        }

        HasOwnershipSnapshot = true;
        polling = false;
    }

    private readonly Dictionary<ushort, bool> focusedUnlocks = [];

    public unsafe void RefreshFocusedUnlocks(IEnumerable<ushort> titleIds)
    {
        focusedUnlocks.Clear();
        var ui = UIState.Instance();
        if (ui == null) return;
        var list = &ui->TitleList;
        // Do not request the whole title list repeatedly from the HUD.
        if (!list->DataReceived) return;
        foreach (var id in titleIds.Where(id => id != 0).Distinct())
        {
            try { focusedUnlocks[id] = list->IsTitleUnlocked(id); }
            catch { /* Unavailable game state: retain unknown. */ }
        }
    }

    public bool? IsUnlocked(ushort titleId)
    {
        if (focusedUnlocks.TryGetValue(titleId, out var focused))
            return focused;
        if (!HasOwnershipSnapshot || titleId == 0)
            return null;
        return unlockedSnapshot.Contains(titleId);
    }

    private void FinishUnavailable()
    {
        HasOwnershipSnapshot = false;
        polling = false;
    }

    private void Build()
    {
        var names = new Dictionary<uint, string>();
        try
        {
            foreach (var row in data.GetExcelSheet<Title>())
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue)
                    continue;

                var masculine = Text(row, "Masculine");
                var feminine = Text(row, "Feminine");
                var name = !string.IsNullOrWhiteSpace(masculine) ? masculine : feminine;
                if (!string.IsNullOrWhiteSpace(name))
                    names[row.RowId] = name;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build title names.");
        }

        try
        {
            foreach (var achievement in data.GetExcelSheet<Lumina.Excel.Sheets.Achievement>())
            {
                var titleId = ReflectionUtil.GetNestedRowId(achievement, "Title");
                if (titleId == 0)
                    titleId = (uint?)ReflectionUtil.GetNumericProperty(achievement, "Title") ?? 0;
                if (titleId == 0 || titleId > ushort.MaxValue || !names.TryGetValue(titleId, out var titleName))
                    continue;

                var entry = new TitleTarget(
                    (ushort)titleId,
                    titleName,
                    achievement.RowId,
                    Text(achievement, "Name"),
                    Text(achievement, "Description"));

                if (byId.TryAdd(entry.TitleId, entry))
                    titles.Add(entry);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build achievement-backed title targets.");
        }

        titles.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    private static string Text(object row, params string[] names)
    {
        var value = ReflectionUtil.GetPropertyValue(row, names);
        if (value is null)
            return string.Empty;

        try
        {
            var extractText = value.GetType().GetMethod("ExtractText", Type.EmptyTypes);
            if (extractText?.Invoke(value, null) is string extracted)
                return extracted.Trim();
        }
        catch
        {
            // Fall through to ToString for schema/string-wrapper compatibility.
        }

        return value.ToString()?.Trim() ?? string.Empty;
    }
}
