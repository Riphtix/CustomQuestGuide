using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// First-class emote targets. The player targets the actual emote/command, not
/// the Ballroom Etiquette item that may teach it. Static sheet relationships
/// map an emote to its teaching item or quest where available; ownership is
/// captured once per explicit /cqg refresh from the game's unlock state.
/// </summary>
public sealed class EmoteCatalog
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly List<EmoteTarget> emotes = [];
    private readonly Dictionary<ushort, EmoteTarget> byId = [];
    private readonly HashSet<ushort> unlockedSnapshot = [];

    public IReadOnlyList<EmoteTarget> Emotes => emotes;
    public bool HasOwnershipSnapshot { get; private set; }
    public bool PollingComplete { get; private set; } = true;

    public EmoteCatalog(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
        Build();
    }

    public EmoteTarget? Get(ushort emoteId) =>
        emoteId != 0 && byId.TryGetValue(emoteId, out var emote) ? emote : null;

    public void BeginPolling() => PollingComplete = false;

    public unsafe void PollSnapshotStep()
    {
        if (PollingComplete)
            return;

        unlockedSnapshot.Clear();
        var ui = UIState.Instance();
        if (ui == null)
        {
            HasOwnershipSnapshot = false;
            PollingComplete = true;
            return;
        }

        foreach (var emote in emotes)
        {
            try
            {
                if (ui->IsEmoteUnlocked(emote.EmoteId))
                    unlockedSnapshot.Add(emote.EmoteId);
            }
            catch
            {
                // Keep the one-shot scan resilient to one bad/unsupported row.
            }
        }

        HasOwnershipSnapshot = true;
        PollingComplete = true;
    }

    private readonly Dictionary<ushort, bool> focusedUnlocks = [];

    public unsafe void RefreshFocusedUnlocks(IEnumerable<ushort> emoteIds)
    {
        focusedUnlocks.Clear();
        var ui = UIState.Instance();
        if (ui == null) return;
        foreach (var id in emoteIds.Where(id => id != 0).Distinct())
        {
            try { focusedUnlocks[id] = ui->IsEmoteUnlocked(id); }
            catch { /* Unavailable game state: retain unknown. */ }
        }
    }

    public bool? IsUnlocked(ushort emoteId)
    {
        if (focusedUnlocks.TryGetValue(emoteId, out var focused))
            return focused;
        if (!HasOwnershipSnapshot || emoteId == 0)
            return null;
        return unlockedSnapshot.Contains(emoteId);
    }

    private void Build()
    {
        var itemByUnlockLink = BuildTeachingItemIndex();
        var questByEmote = BuildQuestRewardIndex();

        try
        {
            foreach (var row in data.GetExcelSheet<Emote>())
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue)
                    continue;

                var name = Text(row, "Name");
                var command = ExtractCommand(row);
                var unlockLink = (uint?)ReflectionUtil.GetNumericProperty(row, "UnlockLink") ?? 0;

                // Skip internal/nameless emote rows. A visible text command is a
                // useful fallback name for rows whose display name is blank.
                if (string.IsNullOrWhiteSpace(name))
                    name = command.TrimStart('/');
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                itemByUnlockLink.TryGetValue(unlockLink, out var itemId);
                questByEmote.TryGetValue(row.RowId, out var questId);

                var entry = new EmoteTarget(
                    (ushort)row.RowId,
                    name,
                    command,
                    unlockLink,
                    itemId,
                    questId);

                if (byId.TryAdd(entry.EmoteId, entry))
                    emotes.Add(entry);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build emote targets.");
        }

        emotes.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }

    private Dictionary<uint, uint> BuildTeachingItemIndex()
    {
        var result = new Dictionary<uint, uint>();
        try
        {
            foreach (var item in data.GetExcelSheet<Item>())
            {
                if (item.RowId == 0 || !item.ItemAction.IsValid)
                    continue;

                var action = item.ItemAction.Value;
                var linkedActionId = ReflectionUtil.GetNestedRowId(action, "Action");
                if (linkedActionId == 0)
                    linkedActionId = (uint?)ReflectionUtil.GetNumericProperty(action, "Type", "ActionType") ?? 0;

                // 2633 is the generic unlock-link item action used by Ballroom
                // Etiquette manuals (and some other permanent unlock items).
                if (linkedActionId != 2633)
                    continue;

                var dataValues = ReflectionUtil.GetPropertyValue(action, "Data");
                var unlockLink = FirstPositiveUInt(dataValues);
                if (unlockLink != 0)
                    result.TryAdd(unlockLink, item.RowId);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not build emote teaching-item index.");
        }
        return result;
    }

    private Dictionary<uint, uint> BuildQuestRewardIndex()
    {
        var result = new Dictionary<uint, uint>();
        try
        {
            foreach (var quest in data.GetExcelSheet<Quest>())
            {
                if (quest.RowId == 0)
                    continue;

                var emoteId = ReflectionUtil.GetNestedRowId(quest, "EmoteReward");
                if (emoteId == 0)
                    emoteId = (uint?)ReflectionUtil.GetNumericProperty(quest, "EmoteReward") ?? 0;
                if (emoteId != 0)
                    result.TryAdd(emoteId, quest.RowId);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not build quest-emote reward index.");
        }
        return result;
    }

    private static string ExtractCommand(object emote)
    {
        try
        {
            var textCommandRef = ReflectionUtil.GetPropertyValue(emote, "TextCommand");
            if (textCommandRef is null)
                return string.Empty;

            var value = ReflectionUtil.GetPropertyValue(textCommandRef, "Value") ?? textCommandRef;
            var command = Text(value, "Command", "Text");
            if (string.IsNullOrWhiteSpace(command))
                return string.Empty;
            return command.StartsWith('/') ? command : $"/{command}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static uint FirstPositiveUInt(object? value)
    {
        if (value is null)
            return 0;

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var element in enumerable)
            {
                if (element is null)
                    continue;

                try
                {
                    var number = Convert.ToUInt32(element);
                    if (number != 0)
                        return number;
                }
                catch
                {
                    var nested = ReflectionUtil.GetNumericProperty(element, "Value");
                    if (nested is > 0 and <= uint.MaxValue)
                        return (uint)nested.Value;
                }
            }
        }

        try
        {
            var number = Convert.ToUInt32(value);
            return number;
        }
        catch
        {
            return 0;
        }
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
