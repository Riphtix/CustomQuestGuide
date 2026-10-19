using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Discovers classes/jobs and unlock guidance from game data. Formal progression
/// membership comes ONLY from a validated, bundled quest-ID catalog; the game
/// quest prerequisite graph is not a trustworthy full progression index.
/// </summary>
public sealed class JobProgressionCatalog
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, JobProgressionTarget> byId = [];
    private readonly Dictionary<uint, QuestNode> quests = [];
    private IReadOnlyList<JobIdentity> jobIdentities = Array.Empty<JobIdentity>();
    private VerifiedJobQuestCatalog? verifiedCatalog;
    private readonly Dictionary<uint, uint[]> journalGenreJobs = new();
    private readonly Dictionary<uint, uint[]> journalCandidateQuests = new();
    private readonly Dictionary<uint, JournalProgressionAnalyzer.Result> journalPathCache = new();
    private readonly Dictionary<uint, IReadOnlyList<JournalQuestStage>> journalStagesCache = new();

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
            BuildJournalCandidates();
            // Manifest is embedded: no runtime file access or network calls.
            using (var stream = typeof(JobProgressionCatalog).Assembly.GetManifestResourceStream(
                "CustomQuestGuide.Data.Verified.job-progression.json"))
            {
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    verifiedCatalog = new VerifiedJobQuestCatalog(reader.ReadToEnd(),
                        new HashSet<uint>(quests.Keys), log);
                }
                else
                    log.Warning("Verified job-progression manifest resource missing.");
            }

            foreach (var job in jobIdentities)
            {
                var reverseUnlockRoots = quests.Values
                    .Where(q => q.UnlockClassJobId == job.ClassJobId)
                    .OrderBy(q => q.Level)
                    .ThenBy(q => q.RowId)
                    .ToArray();

                // ARR base classes (including crafting/gathering) can have missing or
                // misleading unlock links. Use their named guild-introduction quests.
                // ARR base classes can have multiple reverse ClassJobUnlock links,
                // including the city-specific starting MSQ ("Close to Home"). That
                // MSQ does not unlock the class for a character who started elsewhere.
                // Never select the first reverse link by quest level/row ID.
                var directUnlock = job.UnlockQuestId != 0 && quests.TryGetValue(job.UnlockQuestId, out var direct)
                    ? direct : null;
                // A base class's ClassJob.UnlockQuest can refer to the starting-city
                // MSQ rather than the guild quest for a later-unlocked class.
                // Validate ALL base-class candidates by guild quest identity;
                // do not let the direct relation bypass that validation.
                var unlock = IsArrBaseClass(job.Abbreviation)
                    ? SelectBaseClassGuildUnlockQuest(job, directUnlock, reverseUnlockRoots)
                    : directUnlock ?? SelectReverseUnlockQuest(job, reverseUnlockRoots);

                // Formal progression is exclusively supplied by a reviewed offline
                // quest-ID manifest. Never guess membership from class restrictions
                // or partial prerequisite edges. Missing data means UNKNOWN.
                var progression = verifiedCatalog is not null &&
                    verifiedCatalog.TryGetAll(job.ClassJobId, out var ids)
                    ? ids.Select(id => quests[id]).ToArray()
                    : Array.Empty<QuestNode>();

                // Keep every real class/job visible. Some base classes or special
                // jobs can have unusual unlock metadata; an empty progression is
                // still a useful diagnostic rather than a reason to hide the job.
                byId[job.ClassJobId] = new JobProgressionTarget(
                    job.ClassJobId,
                    job.Name,
                    job.Abbreviation,
                    job.Category,
                    job.ParentClassJobId,
                    job.UnlockQuestId != 0 && unlock is not null && unlock.RowId == job.UnlockQuestId
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

    // Standalone jobs (for example DNC/GNB) also have ParentClassJobId == 0;
    // never apply the ARR starting-class guild heuristic to those jobs.
    private static bool IsArrBaseClass(string abbreviation) =>
        abbreviation is "GLD" or "PGL" or "MRD" or "LNC" or "ARC" or
            "ROG" or "CNJ" or "THM" or "ACN" ||
        CraftingJobs.Contains(abbreviation) || GatheringJobs.Contains(abbreviation);

    private QuestNode? SelectBaseClassGuildUnlockQuest(
        JobIdentity job, QuestNode? directUnlock, IReadOnlyList<QuestNode> reverseCandidates)
    {
        // Prefer the guild's introduction, even when the ClassJob row points
        // directly to an MSQ. Some data revisions omit its reverse relation;
        // look up the exact guild title in the local quest index in that case.
        // Restrict fallback by its exact class-specific title, never by level.
        var candidates = reverseCandidates
            .Concat(directUnlock is null ? Array.Empty<QuestNode>() : new[] { directUnlock })
            .Concat(quests.Values.Where(q => IsBaseClassGuildQuestName(q.Name, job.Name)))
            .DistinctBy(q => q.RowId)
            .ToArray();
        return candidates.FirstOrDefault(q => IsBaseClassGuildIntroduction(q.Name, job.Name))
            ?? candidates.FirstOrDefault(q => IsBaseClassWayQuest(q.Name, job.Name));
    }

    private static bool IsBaseClassGuildIntroduction(string title, string className) =>
        title.Equals($"So You Want to Be an {className}", StringComparison.OrdinalIgnoreCase) ||
        title.Equals($"So You Want to Be a {className}", StringComparison.OrdinalIgnoreCase);

    private static bool IsBaseClassWayQuest(string title, string className) =>
        title.Equals($"Way of the {className}", StringComparison.OrdinalIgnoreCase);

    private static bool IsBaseClassGuildQuestName(string title, string className) =>
        IsBaseClassGuildIntroduction(title, className) || IsBaseClassWayQuest(title, className);

    private static QuestNode? SelectReverseUnlockQuest(JobIdentity job, IReadOnlyList<QuestNode> candidates) =>
        candidates.FirstOrDefault();

    // Journal genre is reliable membership evidence, not proof of mandatory progression.
    // This index is built once from local game data and does not contact any service.
    public IReadOnlyList<uint> GetJournalCandidates(uint classJobId) =>
        journalCandidateQuests.TryGetValue(classJobId, out var ids) ? ids : Array.Empty<uint>();

    public IReadOnlyList<uint> GetJournalJobsForGenre(uint genreId) =>
        journalGenreJobs.TryGetValue(genreId, out var ids) ? ids : Array.Empty<uint>();

    private void BuildJournalCandidates()
    {
        var genreNames = new Dictionary<uint, string>();
        var categorySections = new Dictionary<uint, uint>();
        var sectionNames = new Dictionary<uint, string>();
        foreach (var section in data.GetExcelSheet<JournalSection>())
            sectionNames[section.RowId] = ReflectionUtil.GetPropertyValue(section, "Name")?.ToString()?.Trim() ?? string.Empty;
        foreach (var category in data.GetExcelSheet<JournalCategory>())
            categorySections[category.RowId] = ReadRowId(category, "JournalSection", "Section");
        foreach (var genre in data.GetExcelSheet<JournalGenre>())
        {
            var categoryId = ReadRowId(genre, "JournalCategory", "Category");
            if (!categorySections.TryGetValue(categoryId, out var sectionId) ||
                !sectionNames.TryGetValue(sectionId, out var sectionName) ||
                !string.Equals(sectionName, "Class & Job Quests", StringComparison.OrdinalIgnoreCase))
                continue;
            genreNames[genre.RowId] = ReflectionUtil.GetPropertyValue(genre, "Name")?.ToString()?.Trim() ?? string.Empty;
        }
        foreach (var (genreId, name) in genreNames)
        {
            // A localized display-name association is deliberately *not* trusted
            // for quest completion. Require a unique exact match, no fuzzy search.
            var matching = jobIdentities.Where(j =>
                string.Equals(name, j.Name + " Quests", StringComparison.OrdinalIgnoreCase))
                .Select(j => j.ClassJobId).ToArray();
            if (matching.Length == 1) journalGenreJobs[genreId] = matching;
        }
        var byJob = new Dictionary<uint, List<uint>>();
        foreach (var quest in data.GetExcelSheet<Quest>())
        {
            var genreId = ReadRowId(quest, "JournalGenre", "JournalGenre0");
            if (!journalGenreJobs.TryGetValue(genreId, out var jobs)) continue;
            foreach (var jobId in jobs)
            {
                if (!byJob.TryGetValue(jobId, out var list))
                    byJob[jobId] = list = new List<uint>();
                if (quests.ContainsKey(quest.RowId)) list.Add(quest.RowId);
            }
        }
        foreach (var (jobId, ids) in byJob)
            journalCandidateQuests[jobId] = ids.Distinct().OrderBy(id => id).ToArray();
    }

    // Preview-only graph analysis. Deliberately separate from the verified catalog
    // so candidate paths cannot affect completion, auto-unpin or handoffs.
    internal JournalProgressionAnalyzer.Result AnalyzeJournalPath(uint classJobId)
    {
        if (journalPathCache.TryGetValue(classJobId, out var cached)) return cached;
        var nodes = GetJournalCandidates(classJobId)
            .Where(quests.ContainsKey)
            .Select(id => quests[id])
            .Select(q => new JournalProgressionAnalyzer.Node(q.RowId, q.Level, q.PreviousQuestIds))
            .ToArray();
        var result = JournalProgressionAnalyzer.Analyze(nodes);
        journalPathCache[classJobId] = result;
        return result;
    }

    // Read-only stage preview, deliberately separate from the verified completion pipeline.
    // Group by journal level and displayed quest title to expose quest ID variants
    // together instead of pretending every prerequisite graph route is a full chain.
    // Source membership and completion do not establish mandatory eligibility.
    internal sealed record JournalQuestVariant(uint QuestId, string Name, ushort Level,
        IReadOnlyList<uint> PreviousQuestIds, JobQuestStep Step);
    internal sealed record JournalQuestStage(ushort Level, string Name,
        IReadOnlyList<JournalQuestVariant> Variants, bool OnTerminalRoute);

    internal IReadOnlyList<JournalQuestStage> GetJournalStages(uint classJobId)
    {
        if (journalStagesCache.TryGetValue(classJobId, out var cached)) return cached;
        var analysis = AnalyzeJournalPath(classJobId);
        var routeIds = analysis.Routes?.SelectMany(route => route).ToHashSet()
            ?? new HashSet<uint>();
        var stages = GetJournalCandidates(classJobId)
            .Where(quests.ContainsKey)
            .Select(id => quests[id])
            .GroupBy(q => (q.Level, q.Name), new JournalStageKeyComparer())
            .Select(group => new JournalQuestStage(group.Key.Level, group.Key.Name,
                group.OrderBy(q => q.RowId)
                    .Select(q => new JournalQuestVariant(q.RowId, q.Name, q.Level,
                        q.PreviousQuestIds, ToStep(q))).ToArray(),
                group.Any(q => routeIds.Contains(q.RowId))))
            .OrderBy(stage => stage.Level)
            .ThenBy(stage => stage.Variants.Min(v => v.QuestId))
            .ToArray();
        journalStagesCache[classJobId] = stages;
        return stages;
    }

    private sealed class JournalStageKeyComparer : IEqualityComparer<(ushort Level, string Name)>
    {
        public bool Equals((ushort Level, string Name) x, (ushort Level, string Name) y) =>
            x.Level == y.Level && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((ushort Level, string Name) value) =>
            HashCode.Combine(value.Level, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }

    internal string? GetJournalQuestName(uint questId) =>
        quests.TryGetValue(questId, out var q) ? q.Name : null;

    /// <summary>Resolve only validated quest stages applicable to this character.</summary>
    public IReadOnlyList<uint>? GetVerifiedApplicableQuestIds(uint classJobId, uint firstClassId) =>
        verifiedCatalog is not null && verifiedCatalog.TryGetApplicable(classJobId, firstClassId, out var ids)
            ? ids : null;

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
            var categoryId = ReadRowId(quest, "ClassJobCategory0", "ClassJobCategory");
            var secondaryCategoryId = ReadRowId(quest, "ClassJobCategory1");

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
                categoryId,
                secondaryCategoryId,
                previousIds,
                issuerName,
                issuerLocation);
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
        uint ClassJobCategoryId,
        uint SecondaryClassJobCategoryId,
        IReadOnlyList<uint> PreviousQuestIds,
        string? IssuerName,
        string? IssuerLocation);
}
