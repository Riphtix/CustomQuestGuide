using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Builds class/job unlock and formal quest-progression targets from live game
/// data. The progression graph intentionally follows only quests attributable
/// to one specific class/job and reachable from that class/job's unlock quest;
/// it does not attempt to become a leveling, rotation, gearing, role-quest, or
/// general blue-quest guide.
/// </summary>
public sealed class JobProgressionCatalog
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, JobProgressionTarget> byId = [];
    private readonly Dictionary<uint, QuestNode> quests = [];
    private readonly Dictionary<uint, uint> singleJobCategoryCache = [];
    private IReadOnlyList<JobIdentity> jobIdentities = Array.Empty<JobIdentity>();

    public JobProgressionCatalog(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
        Build();
    }

    public IReadOnlyList<JobProgressionTarget> Jobs => byId.Values
        .OrderBy(x => CategorySort(x.Category))
        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<string> Categories => Jobs
        .Select(x => x.Category)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(CategorySort)
        .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public JobProgressionTarget? Get(uint classJobId) =>
        byId.TryGetValue(classJobId, out var value) ? value : null;

    public IReadOnlyList<JobProgressionTarget> GetByCategory(string category) =>
        Jobs.Where(x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase)).ToArray();

    private void Build()
    {
        try
        {
            jobIdentities = data.GetExcelSheet<ClassJob>()
                .Where(row => row.RowId != 0)
                .Select(row =>
                {
                    var name = row.Name.ExtractText().Trim();
                    var abbreviation = row.Abbreviation.ExtractText().Trim();
                    var parentId = ReadRowId(
                        row,
                        "ClassJobParent",
                        "ParentClassJob");
                    var unlockQuestId = ReadRowId(row, "UnlockQuest");
                    var prerequisiteId = ReadRowId(row, "ClassJobPrerequisite", "Prerequisite");

                    return new JobIdentity(
                        row.RowId,
                        name,
                        abbreviation,
                        parentId,
                        unlockQuestId,
                        prerequisiteId,
                        Classify(abbreviation));
                })
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.Name) &&
                    !string.IsNullOrWhiteSpace(x.Abbreviation) &&
                    !x.Abbreviation.Equals("ADV", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            BuildQuestIndex();

            foreach (var job in jobIdentities)
            {
                var reverseUnlockRoots = quests.Values
                    .Where(q => q.UnlockClassJobId == job.ClassJobId)
                    .OrderBy(q => q.Level)
                    .ThenBy(q => q.RowId)
                    .ToArray();

                var unlock = job.UnlockQuestId != 0 && quests.TryGetValue(job.UnlockQuestId, out var directUnlock)
                    ? directUnlock
                    : reverseUnlockRoots.FirstOrDefault();

                var unlockRoots = unlock is not null
                    ? new QuestNode[] { unlock }
                    : reverseUnlockRoots;
                var progression = BuildProgression(job.ClassJobId, unlockRoots);

                // Keep every real class/job visible. Some base classes or special
                // jobs can have unusual unlock metadata; an empty progression is
                // still a useful diagnostic rather than a reason to hide the job.
                byId[job.ClassJobId] = new JobProgressionTarget(
                    job.ClassJobId,
                    job.Name,
                    job.Abbreviation,
                    job.Category,
                    job.ParentClassJobId,
                    job.UnlockQuestId != 0 && unlock is not null
                        ? "ClassJob.UnlockQuest"
                        : unlock is not null
                            ? "Quest.ClassJobUnlock reverse relation"
                            : "Unresolved",
                    job.UnlockQuestId,
                    unlock is null ? null : ToStep(unlock),
                    progression.Select(ToStep).ToArray(),
                    unlock is null
                        ? Array.Empty<JobQuestRequirement>()
                        : unlock.PreviousQuestIds
                            .Select(id => quests.TryGetValue(id, out var q)
                                ? new JobQuestRequirement(id, q.Name)
                                : new JobQuestRequirement(id, $"Quest #{id}"))
                            .ToArray());
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build class/job progression catalog.");
        }
    }

    private void BuildQuestIndex()
    {
        foreach (var quest in data.GetExcelSheet<Quest>())
        {
            if (quest.RowId == 0)
                continue;

            var name = quest.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var unlockJobId = ReadRowId(quest, "ClassJobUnlock");
            var requiredJobId = ReadRowId(quest, "ClassJobRequired");

            // Some class/job quests use a single-job ClassJobCategory instead of
            // ClassJobRequired. Resolve only categories that map to exactly one
            // class/job; role-wide and multi-job categories are deliberately not
            // treated as formal job progression.
            if (requiredJobId == 0)
            {
                var categoryId = ReadRowId(
                    quest,
                    "ClassJobCategory0",
                    "ClassJobCategory");
                requiredJobId = ResolveSingleJobCategory(categoryId);
            }

            var previousIds = EnumerateRowIds(
                ReflectionUtil.GetPropertyValue(quest, "PreviousQuest"));

            var level = ReadFirstLevel(
                ReflectionUtil.GetPropertyValue(quest, "ClassJobLevel"));

            var issuerId = ReadRowId(quest, "IssuerStart");
            var issuerName = RowText<ENpcResident>(issuerId, "Singular", "Name");
            var issuerLocationId = ReadRowId(quest, "IssuerLocation", "IssuerStartLocation");
            var issuerLocation = ResolveLevelLocation(issuerLocationId);

            quests[quest.RowId] = new QuestNode(
                quest.RowId,
                name,
                level,
                unlockJobId,
                requiredJobId,
                previousIds,
                issuerName,
                issuerLocation);
        }
    }

    private IReadOnlyList<QuestNode> BuildProgression(
        uint classJobId,
        IReadOnlyList<QuestNode> unlockRoots)
    {
        var candidates = quests.Values
            .Where(q => q.RequiredClassJobId == classJobId || q.UnlockClassJobId == classJobId)
            .ToDictionary(q => q.RowId);

        if (candidates.Count == 0)
            return Array.Empty<QuestNode>();

        var included = new HashSet<uint>();
        var queue = new Queue<uint>();

        foreach (var root in unlockRoots)
        {
            if (!candidates.ContainsKey(root.RowId) || !included.Add(root.RowId))
                continue;
            queue.Enqueue(root.RowId);
        }

        // If the unlock relation is absent, keep only the candidate set and sort
        // it. This is a schema-resilience fallback; the Debug view will make a
        // missing unlock quest obvious without hiding the class/job entirely.
        if (queue.Count == 0)
        {
            return candidates.Values
                .OrderBy(q => q.Level)
                .ThenBy(q => q.RowId)
                .ToArray();
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var candidate in candidates.Values)
            {
                if (included.Contains(candidate.RowId))
                    continue;

                if (!candidate.PreviousQuestIds.Contains(current))
                    continue;

                included.Add(candidate.RowId);
                queue.Enqueue(candidate.RowId);
            }
        }

        return included
            .Select(id => candidates[id])
            .OrderBy(q => q.Level)
            .ThenBy(q => q.RowId)
            .ToArray();
    }

    private uint ResolveSingleJobCategory(uint categoryId)
    {
        if (categoryId == 0)
            return 0;

        if (singleJobCategoryCache.TryGetValue(categoryId, out var cached))
            return cached;

        try
        {
            var row = data.GetExcelSheet<ClassJobCategory>().GetRow(categoryId);
            var matching = new List<uint>();

            foreach (var job in jobIdentities)
            {
                var raw = ReflectionUtil.GetPropertyValue(row, job.Abbreviation);
                if (AsBool(raw))
                    matching.Add(job.ClassJobId);
            }

            var result = matching.Count == 1 ? matching[0] : 0;
            singleJobCategoryCache[categoryId] = result;
            return result;
        }
        catch
        {
            singleJobCategoryCache[categoryId] = 0;
            return 0;
        }
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
            // Optional display metadata only.
        }

        return null;
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
            // Optional display metadata only.
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
            if (id == 0 && entry is not null)
            {
                try
                {
                    id = Convert.ToUInt32(entry);
                }
                catch
                {
                    id = 0;
                }
            }

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
                // Try the next entry.
            }
        }

        return 0;
    }

    private static bool AsBool(object? value)
    {
        if (value is bool b)
            return b;

        if (value is null)
            return false;

        try
        {
            return Convert.ToInt64(value) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static JobQuestStep ToStep(QuestNode quest) =>
        new(
            quest.RowId,
            quest.Name,
            quest.Level,
            quest.IssuerName,
            quest.IssuerLocation);

    private static string Classify(string abbreviation)
    {
        if (CraftingJobs.Contains(abbreviation))
            return "Crafting Classes";
        if (GatheringJobs.Contains(abbreviation))
            return "Gathering Classes";
        if (LimitedJobs.Contains(abbreviation))
            return "Limited Jobs";
        return "Combat Classes & Jobs";
    }

    private static int CategorySort(string category) => category switch
    {
        "Combat Classes & Jobs" => 0,
        "Crafting Classes" => 10,
        "Gathering Classes" => 20,
        "Limited Jobs" => 30,
        _ => 100,
    };

    private static readonly HashSet<string> CraftingJobs = new(StringComparer.OrdinalIgnoreCase)
    {
        "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL",
    };

    private static readonly HashSet<string> GatheringJobs = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIN", "BTN", "FSH",
    };

    private static readonly HashSet<string> LimitedJobs = new(StringComparer.OrdinalIgnoreCase)
    {
        "BLU", "BST",
    };

    private sealed record JobIdentity(
        uint ClassJobId,
        string Name,
        string Abbreviation,
        uint ParentClassJobId,
        uint UnlockQuestId,
        uint PrerequisiteClassJobId,
        string Category);

    private sealed record QuestNode(
        uint RowId,
        string Name,
        ushort Level,
        uint UnlockClassJobId,
        uint RequiredClassJobId,
        IReadOnlyList<uint> PreviousQuestIds,
        string? IssuerName,
        string? IssuerLocation);
}
