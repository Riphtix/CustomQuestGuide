using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

public sealed record DutyTarget(
    uint ContentFinderConditionId,
    uint InstanceContentId,
    string Name,
    string Category,
    ushort RequiredLevel)
{
    public string TypeLabel => Category switch
    {
        "Dungeons" => "Dungeon",
        "Trials" => "Trial",
        "Raids" => "Raid",
        "Alliance Raids" => "Alliance Raid",
        "Treasure Dungeons" => "Treasure Dungeon",
        _ => "Duty",
    };

    public string DisplayLabel => RequiredLevel > 0
        ? $"{TypeLabel}: {Name} - Lv. {RequiredLevel}"
        : $"{TypeLabel}: {Name}";
}

/// <summary>
/// First-class duty target catalog built from ContentFinderCondition. Duties are
/// deliberately kept separate from item acquisition data so they can be searched,
/// selected and pinned directly while still sharing the same unlock guidance.
/// </summary>
public sealed class DutyCatalog
{
    private readonly List<DutyTarget> duties = [];
    private readonly Dictionary<uint, DutyTarget> byId = [];
    private readonly Dictionary<string, DutyTarget> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DutyTarget> byNormalizedName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DutyTarget> Duties => duties;
    public IReadOnlyList<string> Categories { get; }

    public DutyCatalog(IDataManager data, IPluginLog log)
    {
        try
        {
            foreach (var cfc in data.GetExcelSheet<ContentFinderCondition>())
            {
                if (cfc.RowId == 0)
                    continue;

                var name = cfc.Name.ExtractText().Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var instanceContentId = ReflectionUtil.GetNestedRowId(cfc, "Content", "InstanceContent");
                if (instanceContentId == 0)
                    continue;

                var category = ResolveCategory(data, cfc);
                var level = ReadLevel(cfc);
                var duty = new DutyTarget(cfc.RowId, instanceContentId, name, category, level);
                duties.Add(duty);
                byId.TryAdd(cfc.RowId, duty);
                byName.TryAdd(name, duty);
                byNormalizedName.TryAdd(NormalizeName(name), duty);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build first-class duty target catalog.");
        }

        duties.Sort((a, b) =>
        {
            var category = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
            return category != 0 ? category : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        Categories = duties
            .Select(x => x.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(CategorySortKey)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DutyTarget? Get(uint cfcId) => byId.TryGetValue(cfcId, out var duty) ? duty : null;

    public string GetDisplayLabel(string name) => GetByName(name)?.DisplayLabel ?? name;

    public DutyTarget? GetByName(string name)
    {
        var raw = (name ?? string.Empty).Trim();
        if (byName.TryGetValue(raw, out var exact))
            return exact;

        var normalized = NormalizeName(raw);
        if (byNormalizedName.TryGetValue(normalized, out var normalizedMatch))
            return normalizedMatch;

        // Source providers occasionally decorate the duty text slightly. Only
        // accept a contains-style fallback when it resolves to one unique duty.
        var candidates = duties
            .Where(x =>
            {
                var n = NormalizeName(x.Name);
                return n.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                       normalized.Contains(n, StringComparison.OrdinalIgnoreCase);
            })
            .Take(2)
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string NormalizeName(string value)
    {
        value = (value ?? string.Empty).Trim();
        var providerSeparator = value.IndexOf(" — ", StringComparison.Ordinal);
        if (providerSeparator > 0)
            value = value[..providerSeparator].Trim();

        return new string(value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    public IReadOnlyList<DutyTarget> GetByCategory(string category) => duties
        .Where(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private static int CategorySortKey(string category) => category switch
    {
        "Dungeons" => 0,
        "Trials" => 1,
        "Raids" => 2,
        "Alliance Raids" => 3,
        "Treasure Dungeons" => 4,
        _ => 10,
    };

    private static string ResolveCategory(IDataManager data, ContentFinderCondition cfc)
    {
        if (ReflectionUtil.GetBoolProperty(cfc, "AllianceRoulette") == true)
            return "Alliance Raids";
        if (ReflectionUtil.GetBoolProperty(cfc, "NormalRaidRoulette") == true)
            return "Raids";
        if (ReflectionUtil.GetBoolProperty(cfc, "TrialRoulette") == true)
            return "Trials";

        var contentTypeId = ReflectionUtil.GetNestedRowId(cfc, "ContentType");
        var contentTypeName = string.Empty;
        if (contentTypeId != 0)
        {
            try
            {
                var row = data.GetExcelSheet<ContentType>().GetRow(contentTypeId);
                contentTypeName = ReflectionUtil.GetPropertyValue(row, "Name")?.ToString()?.Trim() ?? string.Empty;
            }
            catch
            {
                // Optional classification enrichment only.
            }
        }

        if (contentTypeName.Contains("alliance", StringComparison.OrdinalIgnoreCase) &&
            contentTypeName.Contains("raid", StringComparison.OrdinalIgnoreCase))
            return "Alliance Raids";
        if (contentTypeName.Contains("raid", StringComparison.OrdinalIgnoreCase))
            return "Raids";
        if (contentTypeName.Contains("trial", StringComparison.OrdinalIgnoreCase))
            return "Trials";
        if (contentTypeName.Contains("treasure", StringComparison.OrdinalIgnoreCase))
            return "Treasure Dungeons";
        if (contentTypeName.Contains("dungeon", StringComparison.OrdinalIgnoreCase))
            return "Dungeons";

        return string.IsNullOrWhiteSpace(contentTypeName) ? "Other Duties" : contentTypeName;
    }

    private static ushort ReadLevel(ContentFinderCondition cfc)
    {
        // This is the character/class-job level required to enter the duty.
        // Do not fall back to ItemLevelRequired here: that is gear item level and
        // would produce misleading labels such as "Lv. 440" for level-80 duties.
        var value = ReflectionUtil.GetNumericProperty(cfc,
            "ClassJobLevelRequired",
            "RequiredClassJobLevel",
            "ClassJobLevel");
        if (!value.HasValue || value.Value <= 0 || value.Value > ushort.MaxValue)
            return 0;
        return (ushort)value.Value;
    }
}
