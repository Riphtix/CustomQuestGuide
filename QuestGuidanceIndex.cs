using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Shared quest metadata/state helper used by item acquisition, relic sets,
/// curated prerequisite guides, pinned targets, and class/job progression.
/// It intentionally answers the two questions CQG should answer whenever a
/// quest is the actionable route: what quest is current/next, and who starts it.
/// </summary>
public sealed class QuestGuidanceIndex
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, QuestGuidanceInfo> byId = [];
    private readonly Dictionary<string, List<QuestGuidanceInfo>> byName =
        new(StringComparer.OrdinalIgnoreCase);

    public QuestGuidanceIndex(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
        Build();
    }

    public QuestGuidanceInfo? GetById(uint rowId) =>
        byId.TryGetValue(rowId, out var value) ? value : null;

    public IReadOnlyList<QuestGuidanceInfo> GetByName(string name)
    {
        var normalized = NormalizeQuestName(name);
        return byName.TryGetValue(normalized, out var values)
            ? values
            : Array.Empty<QuestGuidanceInfo>();
    }

    public QuestGuidanceInfo? GetBestByName(string name) =>
        GetByName(name).FirstOrDefault();


    public QuestSequenceStatus ResolveQuestIds(
        IEnumerable<uint> questIds,
        Func<uint, QuestProgressState> stateProvider)
    {
        var ids = questIds.Where(x => x != 0).ToArray();
        if (ids.Length == 0)
            return QuestSequenceStatus.Empty;

        var entries = ids.Select(id =>
        {
            var info = GetById(id);
            var raw = stateProvider(id);
            return new QuestSequenceEntry(
                info?.Name ?? $"Quest #{id}",
                info,
                raw,
                raw);
        }).ToArray();

        return InferSequence(entries);
    }

    public QuestSequenceStatus ResolveSequence(
        IEnumerable<string> questNames,
        Func<uint, QuestProgressState> stateProvider)
    {
        var orderedNames = questNames
            .Select(NormalizeQuestName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (orderedNames.Length == 0)
            return QuestSequenceStatus.Empty;

        var entries = orderedNames.Select(name =>
        {
            var candidates = GetByName(name);
            var bestState = QuestProgressState.NotStarted;
            QuestGuidanceInfo? bestInfo = candidates.FirstOrDefault();

            foreach (var candidate in candidates)
            {
                var state = stateProvider(candidate.QuestId);
                if (state == QuestProgressState.Completed)
                {
                    bestState = state;
                    bestInfo = candidate;
                    break;
                }

                if (state == QuestProgressState.InProgress)
                {
                    bestState = state;
                    bestInfo = candidate;
                }
            }

            return new QuestSequenceEntry(name, bestInfo, bestState, bestState);
        }).ToArray();

        return InferSequence(entries);
    }

    private static QuestSequenceStatus InferSequence(QuestSequenceEntry[] entries)
    {
        // Tracker-style inference: observing any later quest as active/completed
        // proves earlier ordered prerequisites have been passed, even if a
        // historical completion bit or alternate branch cannot be read directly.
        var furthestObserved = -1;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].RawState is QuestProgressState.InProgress or QuestProgressState.Completed)
                furthestObserved = i;
        }

        if (furthestObserved >= 0)
        {
            for (var i = 0; i < furthestObserved; i++)
                entries[i] = entries[i] with { EffectiveState = QuestProgressState.Completed };
        }

        var activeIndex = Array.FindIndex(entries, x => x.RawState == QuestProgressState.InProgress);
        var nextIndex = activeIndex >= 0
            ? activeIndex
            : Array.FindIndex(entries, x => x.EffectiveState == QuestProgressState.NotStarted);
        var afterActiveIndex = activeIndex >= 0 && activeIndex + 1 < entries.Length
            ? activeIndex + 1
            : -1;

        return new QuestSequenceStatus(
            entries,
            activeIndex,
            nextIndex,
            afterActiveIndex,
            entries.Length > 0 && entries.All(x => x.EffectiveState == QuestProgressState.Completed));
    }

    public static IReadOnlyList<string> ExtractQuestNames(SourceEntry source)
    {
        var result = new List<string>();

        if (source.Kind.Equals("Quest", StringComparison.OrdinalIgnoreCase))
            AddQuestExpression(result, source.Name);
        else if (source.Kind.Contains("Quest", StringComparison.OrdinalIgnoreCase))
            AddQuestExpression(result, source.Name);
        else if (source.Name.StartsWith("Quest:", StringComparison.OrdinalIgnoreCase))
            AddQuestExpression(result, source.Name[6..]);

        if (!string.IsNullOrWhiteSpace(source.Detail))
        {
            const string marker = "Requires quest:";
            var index = source.Detail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var requirement = source.Detail[(index + marker.Length)..];
                var pipe = requirement.IndexOf('|');
                if (pipe >= 0)
                    requirement = requirement[..pipe];
                AddQuestExpression(result, requirement);
            }
        }

        return result
            .Select(NormalizeQuestName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeQuestName(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.StartsWith("Quest:", StringComparison.OrdinalIgnoreCase))
            value = value[6..].Trim();

        // Live quest reward sources may append a level suffix.
        var levelMarker = value.LastIndexOf(" (Lv. ", StringComparison.OrdinalIgnoreCase);
        if (levelMarker > 0 && value.EndsWith(")", StringComparison.Ordinal))
            value = value[..levelMarker].Trim();

        return value.Trim(' ', '—', '-', '>');
    }

    private static void AddQuestExpression(List<string> target, string expression)
    {
        expression = expression.Replace("->", "→", StringComparison.Ordinal);
        foreach (var part in expression.Split('→', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeQuestName(part);
            if (!string.IsNullOrWhiteSpace(normalized))
                target.Add(normalized);
        }
    }

    private void Build()
    {
        try
        {
            foreach (var quest in data.GetExcelSheet<Quest>())
            {
                if (quest.RowId == 0)
                    continue;

                var name = quest.Name.ExtractText().Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var issuerId = ReadRowId(quest, "IssuerStart");
                var issuerName = RowText<ENpcResident>(issuerId, "Singular", "Name");
                var issuerLocationId = ReadRowId(quest, "IssuerLocation", "IssuerStartLocation");
                var location = ResolveLevelLocation(issuerLocationId);
                var previous = EnumerateRowIds(ReflectionUtil.GetPropertyValue(quest, "PreviousQuest"));
                var level = ReadFirstLevel(ReflectionUtil.GetPropertyValue(quest, "ClassJobLevel"));

                var info = new QuestGuidanceInfo(
                    quest.RowId,
                    name,
                    level,
                    issuerId,
                    issuerName,
                    location,
                    previous);

                byId[quest.RowId] = info;
                if (!byName.TryGetValue(name, out var list))
                {
                    list = [];
                    byName[name] = list;
                }
                list.Add(info);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build shared quest guidance index.");
        }
    }

    private string? ResolveLevelLocation(uint levelId)
    {
        if (levelId == 0)
            return null;

        try
        {
            var level = data.GetExcelSheet<Level>().GetRow(levelId);
            var placeNameId = ReadRowId(level, "PlaceName");
            if (placeNameId != 0)
            {
                var direct = RowText<PlaceName>(placeNameId, "Name");
                if (!string.IsNullOrWhiteSpace(direct))
                    return direct;
            }

            var territoryId = ReadRowId(level, "Territory", "TerritoryType");
            if (territoryId != 0)
            {
                var territory = data.GetExcelSheet<TerritoryType>().GetRow(territoryId);
                var territoryPlaceId = ReadRowId(territory, "PlaceName");
                var territoryName = RowText<PlaceName>(territoryPlaceId, "Name");
                if (!string.IsNullOrWhiteSpace(territoryName))
                    return territoryName;
            }
        }
        catch
        {
            // Display enrichment only.
        }

        return null;
    }

    private string? RowText<T>(uint rowId, params string[] propertyNames)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        if (rowId == 0)
            return null;

        try
        {
            var row = data.GetExcelSheet<T>().GetRow(rowId);
            foreach (var propertyName in propertyNames)
            {
                var value = ReflectionUtil.GetPropertyValue(row, propertyName);
                var text = value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch
        {
            // Optional metadata only.
        }

        return null;
    }

    private static uint ReadRowId(object row, params string[] propertyNames)
    {
        var nested = ReflectionUtil.GetNestedRowId(row, propertyNames);
        if (nested != 0)
            return nested;

        var numeric = ReflectionUtil.GetNumericProperty(row, propertyNames);
        if (numeric.HasValue && numeric.Value > 0 && numeric.Value <= uint.MaxValue)
            return (uint)numeric.Value;
        return 0;
    }

    private static IReadOnlyList<uint> EnumerateRowIds(object? value)
    {
        var result = new List<uint>();
        if (value is not IEnumerable enumerable || value is string)
            return result;

        foreach (var entry in enumerable)
        {
            var id = ReflectionUtil.ExtractRowId(entry);
            if (id != 0)
                result.Add(id);
        }
        return result.Distinct().ToArray();
    }

    private static ushort ReadFirstLevel(object? value)
    {
        if (value is not IEnumerable enumerable || value is string)
            return 0;

        foreach (var entry in enumerable)
        {
            try
            {
                var level = Convert.ToUInt16(entry);
                if (level > 0)
                    return level;
            }
            catch
            {
                // Continue.
            }
        }
        return 0;
    }
}
