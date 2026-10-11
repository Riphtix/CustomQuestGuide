using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace CustomQuestGuide;

/// <summary>
/// Loads LuminaSupplemental data.
///
/// v0.8 has two loading paths:
///
/// 1. Read embedded CSV resources directly when they are exposed by the package.
/// 2. If that produces no rows, call LuminaSupplemental's own CsvLoader via
///    reflection. This is intentionally reflection-based so the plugin does not
///    have to bind to every supplemental model namespace/type at compile time.
///
/// The second path is important: the package's documented/supported loading
/// method is CsvLoader.LoadResource&lt;T&gt;(...). Earlier CQG versions assumed all
/// CSVs could always be consumed as ordinary manifest resources, which can leave
/// the entire duty-drop provider empty even though the NuGet package is present.
/// </summary>
public sealed class SupplementalCsvIndex
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Assembly? assembly;
    private readonly string[] resources = [];

    private readonly Dictionary<string, IReadOnlyList<Dictionary<string, string>>> datasetCache =
        new(StringComparer.OrdinalIgnoreCase);

    public HashSet<uint> StoreItems { get; } = [];
    public HashSet<uint> UnobtainableItems { get; } = [];

    public int StoreItemRows { get; private set; }
    public int UnobtainableItemRows { get; private set; }

    public Dictionary<uint, List<SupplementalSourceRecord>> ItemSources { get; } = [];

    // Supplemental reverse shop metadata. The retail shop sheets tell us what
    // an item costs, but not always which NPC staffs the shop or what a blank
    // shop row is called. LuminaSupplemental ships those missing joins.
    public Dictionary<uint, List<uint>> ShopNpcIds { get; } = [];
    public Dictionary<uint, string> ShopNames { get; } = [];

    /// <summary>
    /// Item ID -> duty association, after joining Dungeon* supplemental tables.
    /// </summary>
    public Dictionary<uint, List<DutyReference>> DutyReferencesByItem { get; } = [];

    public SupplementalCsvIndex(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;

        try
        {
            assembly =
                AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a =>
                        string.Equals(
                            a.GetName().Name,
                            "LuminaSupplemental.Excel",
                            StringComparison.OrdinalIgnoreCase))
                ?? Assembly.Load("LuminaSupplemental.Excel");

            resources = assembly.GetManifestResourceNames();

            LoadItemIdSet("StoreItem", StoreItems);
            LoadItemIdSet("UnobtainableItem", UnobtainableItems);

            BuildDutyIndex();
            BuildShopMetadata();

            IndexDataset("MobDrop", "Monster");
            IndexDataset("FateItem", "FATE");
            IndexDataset("FieldOpCoffer", "Field Operation Coffer");
            IndexDataset("RetainerVentureItem", "Retainer Venture");
            IndexDataset("AirshipDrop", "Airship Voyage");
            IndexDataset("SubmarineDrop", "Submarine Voyage");
            IndexDataset("GardeningCrossbreed", "Gardening");
            IndexDataset("ItemSupplement", "Item Conversion");

            log.Information(
                $"CQG supplemental index: resources={resources.Length}, " +
                $"store={StoreItems.Count}, unobtainable={UnobtainableItems.Count}, " +
                $"genericItems={ItemSources.Count}, dutyItems={DutyReferencesByItem.Count}.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not initialize LuminaSupplemental data.");
        }
    }


    private void BuildShopMetadata()
    {
        try
        {
            foreach (var row in ReadDataset("ENpcShop"))
            {
                var shopId = FirstIdContaining(row, "Shop");
                var npcId = FirstIdContaining(row, "ENpc", "Npc");

                if (shopId == 0 || npcId == 0)
                    continue;

                if (!ShopNpcIds.TryGetValue(shopId, out var npcs))
                {
                    npcs = [];
                    ShopNpcIds[shopId] = npcs;
                }

                if (!npcs.Contains(npcId))
                    npcs.Add(npcId);
            }

            foreach (var row in ReadDataset("ShopName"))
            {
                var shopId = FirstIdContaining(row, "Shop");
                var name = GetText(row, "Name", "ShopName", "DisplayName", "Label");

                if (shopId == 0 || string.IsNullOrWhiteSpace(name))
                    continue;

                ShopNames[shopId] = name.Trim();
            }

            log.Debug(
                $"CQG supplemental shop metadata: namedShops={ShopNames.Count}, " +
                $"shopNpcLinks={ShopNpcIds.Sum(x => x.Value.Count)}.");
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not build supplemental shop metadata.");
        }
    }

    public IReadOnlyList<uint> GetShopNpcIds(uint shopId) =>
        ShopNpcIds.TryGetValue(shopId, out var ids) ? ids : [];

    public string? GetShopName(uint shopId) =>
        ShopNames.TryGetValue(shopId, out var name) ? name : null;

    public IReadOnlyList<Dictionary<string, string>> ReadDataset(string datasetName)
    {
        if (datasetCache.TryGetValue(datasetName, out var cached))
            return cached;

        var direct = ReadEmbeddedCsv(datasetName);

        if (direct.Count > 0)
        {
            datasetCache[datasetName] = direct;
            return direct;
        }

        var viaLoader = ReadViaPackageCsvLoader(datasetName);
        datasetCache[datasetName] = viaLoader;
        return viaLoader;
    }

    private IReadOnlyList<Dictionary<string, string>> ReadEmbeddedCsv(string datasetName)
    {
        if (assembly is null)
            return [];

        try
        {
            var resource = resources.FirstOrDefault(r =>
                r.EndsWith($".{datasetName}.csv", StringComparison.OrdinalIgnoreCase));

            resource ??= resources.FirstOrDefault(r =>
                r.Contains(datasetName, StringComparison.OrdinalIgnoreCase) &&
                r.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

            if (resource is null)
                return [];

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
                return [];

            using var reader = new StreamReader(stream);

            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                return [];

            var headers = ParseCsvLine(headerLine);
            var result = new List<Dictionary<string, string>>();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var values = ParseCsvLine(line);
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < headers.Count && i < values.Count; i++)
                    row[headers[i]] = values[i];

                result.Add(row);
            }

            if (result.Count > 0)
                log.Debug($"CQG loaded {result.Count} {datasetName} rows directly.");

            return result;
        }
        catch (Exception ex)
        {
            log.Debug(ex, $"Direct supplemental resource read failed for {datasetName}.");
            return [];
        }
    }

    /// <summary>
    /// Calls LuminaSupplemental.Excel's documented CsvLoader.LoadResource&lt;T&gt;
    /// without compile-time coupling to the model namespace.
    /// </summary>
    private IReadOnlyList<Dictionary<string, string>> ReadViaPackageCsvLoader(
        string datasetName)
    {
        if (assembly is null)
            return [];

        try
        {
            var allTypes = SafeGetTypes(assembly);

            var modelType = allTypes.FirstOrDefault(t =>
                string.Equals(t.Name, datasetName, StringComparison.OrdinalIgnoreCase));

            var loaderType = allTypes.FirstOrDefault(t =>
                string.Equals(t.Name, "CsvLoader", StringComparison.OrdinalIgnoreCase));

            if (modelType is null || loaderType is null)
                return [];

            var resourceName = GetStaticStringMember(
                loaderType,
                $"{datasetName}ResourceName");

            if (string.IsNullOrWhiteSpace(resourceName))
            {
                // Some versions may use a different casing convention. Search
                // any public static string whose name contains both the dataset
                // and "ResourceName".
                resourceName = loaderType
                    .GetMembers(BindingFlags.Public | BindingFlags.Static)
                    .Where(m =>
                        m.Name.Contains(datasetName, StringComparison.OrdinalIgnoreCase) &&
                        m.Name.Contains("ResourceName", StringComparison.OrdinalIgnoreCase))
                    .Select(GetStaticStringMember)
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            }

            if (string.IsNullOrWhiteSpace(resourceName))
                return [];

            var gameData = ReflectionUtil.GetPropertyValue(data, "GameData");
            if (gameData is null)
                return [];

            var options = ReflectionUtil.GetPropertyValue(gameData, "Options");
            var language = options is null
                ? null
                : ReflectionUtil.GetPropertyValue(options, "DefaultExcelLanguage");

            var candidates = loaderType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m =>
                    string.Equals(m.Name, "LoadResource", StringComparison.Ordinal) &&
                    m.IsGenericMethodDefinition)
                .ToArray();

            foreach (var candidate in candidates)
            {
                MethodInfo method;

                try
                {
                    method = candidate.MakeGenericMethod(modelType);
                }
                catch
                {
                    continue;
                }

                var parameters = method.GetParameters();
                var args = new object?[parameters.Length];
                var usable = true;

                for (var i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    var effectiveType = p.ParameterType.IsByRef
                        ? p.ParameterType.GetElementType()
                        : p.ParameterType;

                    if (effectiveType == typeof(string))
                    {
                        args[i] = resourceName;
                    }
                    else if (p.IsOut || p.ParameterType.IsByRef)
                    {
                        args[i] = null;
                    }
                    else if (effectiveType is not null &&
                             effectiveType.IsInstanceOfType(gameData))
                    {
                        args[i] = gameData;
                    }
                    else if (language is not null &&
                             effectiveType is not null &&
                             effectiveType.IsInstanceOfType(language))
                    {
                        args[i] = language;
                    }
                    else if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else
                    {
                        usable = false;
                        break;
                    }
                }

                if (!usable)
                    continue;

                object? rawResult;

                try
                {
                    rawResult = method.Invoke(null, args);
                }
                catch
                {
                    continue;
                }

                if (rawResult is not IEnumerable enumerable)
                    continue;

                var rows = new List<Dictionary<string, string>>();

                foreach (var entry in enumerable)
                {
                    if (entry is null)
                        continue;

                    rows.Add(ObjectToDictionary(entry));
                }

                if (rows.Count > 0)
                {
                    log.Information(
                        $"CQG loaded {rows.Count} {datasetName} rows through LuminaSupplemental CsvLoader.");
                    return rows;
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, $"CsvLoader fallback failed for {datasetName}.");
        }

        return [];
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
    }

    private static string? GetStaticStringMember(Type type, string memberName)
    {
        var member = type.GetMember(
                memberName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)
            .FirstOrDefault();

        return member is null ? null : GetStaticStringMember(member);
    }

    private static string? GetStaticStringMember(MemberInfo member)
    {
        try
        {
            return member switch
            {
                FieldInfo f => f.GetValue(null)?.ToString(),
                PropertyInfo p => p.GetValue(null)?.ToString(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ObjectToDictionary(object obj)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in obj.GetType().GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            object? value;

            try
            {
                value = property.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (value is null)
                continue;

            result[property.Name] = FlattenValue(value);
        }

        foreach (var field in obj.GetType().GetFields(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            object? value;

            try
            {
                value = field.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (value is null)
                continue;

            result[field.Name] = FlattenValue(value);
        }

        return result;
    }

    private static string FlattenValue(object value)
    {
        if (value is string text)
            return text;

        if (value.GetType().IsPrimitive ||
            value is decimal ||
            value is Enum)
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                   ?? string.Empty;

        var rowId = ReflectionUtil.ExtractRowId(value);
        if (rowId != 0)
            return rowId.ToString();

        return value.ToString() ?? string.Empty;
    }

    private void LoadItemIdSet(string datasetName, HashSet<uint> output)
    {
        var rows = ReadDataset(datasetName);

        if (datasetName.Equals("StoreItem", StringComparison.OrdinalIgnoreCase))
            StoreItemRows = rows.Count;

        if (datasetName.Equals("UnobtainableItem", StringComparison.OrdinalIgnoreCase))
            UnobtainableItemRows = rows.Count;

        foreach (var row in rows)
        {
            if (TryFindItemId(row, out var itemId))
            {
                output.Add(itemId);
                continue;
            }

            // StoreItem / UnobtainableItem are one-item-per-row datasets.
            // Package/model revisions have used slightly different property
            // names over time, so accept the common generic ID names here.
            if (datasetName.Equals("StoreItem", StringComparison.OrdinalIgnoreCase) ||
                datasetName.Equals("UnobtainableItem", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetUInt(
                        row,
                        out itemId,
                        "ItemId",
                        "ItemID",
                        "Item",
                        "Id",
                        "ID",
                        "RowId",
                        "RowID") &&
                    itemId != 0)
                {
                    output.Add(itemId);
                    continue;
                }

                // Final schema-tolerant fallback: these datasets describe a
                // single item per row. If exactly one non-zero numeric value is
                // present, it is the only defensible item identifier.
                var numericValues = row.Values
                    .Select(raw => uint.TryParse(raw, out var value) ? value : 0)
                    .Where(value => value != 0)
                    .Distinct()
                    .ToArray();

                if (numericValues.Length == 1)
                    output.Add(numericValues[0]);
            }
        }

        log.Information(
            $"CQG dataset {datasetName}: rows={rows.Count:N0}, itemIds={output.Count:N0}");
    }

    private void BuildDutyIndex()
    {
        var bosses = ReadDataset("DungeonBoss");
        var chests = ReadDataset("DungeonChest");

        var bossToDuty = BuildParentDutyMap(
            bosses,
            ["DungeonBossId", "DungeonBossID", "BossId", "BossID", "RowId", "RowID", "Id", "ID"]);

        var chestToDuty = BuildParentDutyMap(
            chests,
            ["DungeonChestId", "DungeonChestID", "ChestId", "ChestID", "RowId", "RowID", "Id", "ID"]);

        AddDirectDutyDrops(ReadDataset("DungeonDrop"));

        AddJoinedDutyDrops(
            ReadDataset("DungeonBossDrop"),
            bossToDuty,
            ["DungeonBossId", "DungeonBossID", "BossId", "BossID"]);

        var bossChestRows = ReadDataset("DungeonBossChest");
        AddDirectDutyDrops(bossChestRows);
        AddJoinedDutyDrops(
            bossChestRows,
            bossToDuty,
            ["DungeonBossId", "DungeonBossID", "BossId", "BossID"]);

        AddJoinedDutyDrops(
            ReadDataset("DungeonChestItem"),
            chestToDuty,
            ["DungeonChestId", "DungeonChestID", "ChestId", "ChestID"]);

        IndexDataset("DungeonDrop", "Duty");
        IndexDataset("DungeonBossDrop", "Duty Boss");
        IndexDataset("DungeonBossChest", "Duty Boss Chest");
        IndexDataset("DungeonChestItem", "Duty Chest");
    }

    private Dictionary<uint, DutyReference> BuildParentDutyMap(
        IReadOnlyList<Dictionary<string, string>> rows,
        string[] parentIdColumns)
    {
        var result = new Dictionary<uint, DutyReference>();

        foreach (var row in rows)
        {
            if (!TryGetUInt(row, out var parentId, parentIdColumns))
                continue;

            var duty = ExtractDutyReference(row);
            if (duty is not null)
                result[parentId] = duty;
        }

        return result;
    }

    private void AddDirectDutyDrops(
        IReadOnlyList<Dictionary<string, string>> rows)
    {
        foreach (var row in rows)
        {
            if (!TryFindItemId(row, out var itemId))
                continue;

            var duty = ExtractDutyReference(row);
            if (duty is not null)
                AddDutyReference(itemId, duty);
        }
    }

    private void AddJoinedDutyDrops(
        IReadOnlyList<Dictionary<string, string>> rows,
        IReadOnlyDictionary<uint, DutyReference> parentMap,
        string[] parentColumns)
    {
        foreach (var row in rows)
        {
            if (!TryFindItemId(row, out var itemId))
                continue;

            if (!TryGetUInt(row, out var parentId, parentColumns))
                continue;

            if (parentMap.TryGetValue(parentId, out var duty))
                AddDutyReference(itemId, duty);
        }
    }

    private void AddDutyReference(uint itemId, DutyReference duty)
    {
        if (!DutyReferencesByItem.TryGetValue(itemId, out var list))
        {
            list = [];
            DutyReferencesByItem[itemId] = list;
        }

        if (!list.Contains(duty))
            list.Add(duty);
    }

    private static DutyReference? ExtractDutyReference(
        IReadOnlyDictionary<string, string> row)
    {
        if (TryGetUInt(row, out var cfc,
                "ContentFinderConditionId",
                "ContentFinderConditionID",
                "ContentFinderCondition",
                "CfcId",
                "CFCId"))
        {
            return new DutyReference(DutyReferenceKind.ContentFinderCondition, cfc);
        }

        if (TryGetUInt(row, out var territory,
                "TerritoryTypeId",
                "TerritoryTypeID",
                "TerritoryType",
                "TerritoryId",
                "TerritoryID",
                "Territory"))
        {
            return new DutyReference(DutyReferenceKind.TerritoryType, territory);
        }

        var cfcLoose = FirstIdContaining(row, "ContentFinderCondition", "Cfc");
        if (cfcLoose != 0)
            return new DutyReference(DutyReferenceKind.ContentFinderCondition, cfcLoose);

        var territoryLoose = FirstIdContaining(row, "TerritoryType", "Territory");
        if (territoryLoose != 0)
            return new DutyReference(DutyReferenceKind.TerritoryType, territoryLoose);

        return null;
    }

    private void IndexDataset(string datasetName, string kind)
    {
        foreach (var row in ReadDataset(datasetName))
        {
            if (!TryFindItemId(row, out var itemId))
                continue;

            if (!ItemSources.TryGetValue(itemId, out var list))
            {
                list = [];
                ItemSources[itemId] = list;
            }

            list.Add(new SupplementalSourceRecord(datasetName, kind, row));
        }
    }

    private static bool TryFindItemId(
        IReadOnlyDictionary<string, string> row,
        out uint itemId)
    {
        if (TryGetUInt(row, out itemId,
                "ItemId", "ItemID", "Item", "ItemRowId", "ItemRowID"))
            return itemId != 0;

        foreach (var (key, raw) in row)
        {
            var k = Normalize(key);

            if (!k.Contains("item"))
                continue;

            if (k.Contains("result") ||
                k.Contains("reward") ||
                k.Contains("drop") ||
                k.EndsWith("itemid") ||
                k == "item")
            {
                if (uint.TryParse(raw, out itemId) && itemId != 0)
                    return true;
            }
        }

        itemId = 0;
        return false;
    }

    public static bool TryGetUInt(
        IReadOnlyDictionary<string, string> row,
        out uint value,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetValue(name, out var raw) &&
                uint.TryParse(raw, out value))
                return true;

            var normalized = Normalize(name);

            foreach (var (key, text) in row)
            {
                if (Normalize(key) == normalized &&
                    uint.TryParse(text, out value))
                    return true;
            }
        }

        value = 0;
        return false;
    }

    public static string? GetText(
        IReadOnlyDictionary<string, string> row,
        params string[] preferredNames)
    {
        foreach (var name in preferredNames)
        {
            if (row.TryGetValue(name, out var raw) &&
                !string.IsNullOrWhiteSpace(raw))
                return raw;
        }

        return null;
    }

    public static uint FirstIdContaining(
        IReadOnlyDictionary<string, string> row,
        params string[] fragments)
    {
        foreach (var (key, raw) in row)
        {
            var k = Normalize(key);

            if (fragments.Any(f => k.Contains(Normalize(f))) &&
                uint.TryParse(raw, out var id) &&
                id != 0)
                return id;
        }

        return 0;
    }

    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (c == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        values.Add(current.ToString());
        return values;
    }
}

public sealed record SupplementalSourceRecord(
    string Dataset,
    string Kind,
    IReadOnlyDictionary<string, string> Row);

public enum DutyReferenceKind
{
    ContentFinderCondition,
    TerritoryType,
}

public sealed record DutyReference(DutyReferenceKind Kind, uint Id);
