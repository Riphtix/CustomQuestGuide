using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Best-effort ENpc name -> in-world place lookup shared by all source types.
/// Quest rows already carry their issuer Level directly; shop/NPC source rows
/// usually do not, so this index joins ENpcResident IDs back through Level.Object.
/// It intentionally reports only data that can be established from game sheets.
/// </summary>
public sealed class NpcLocationIndex
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<string, List<string>> locationsByNpcName =
        new(StringComparer.OrdinalIgnoreCase);

    public NpcLocationIndex(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
        Build();
    }

    public string? GetBestLocation(string npcName)
    {
        npcName = (npcName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(npcName))
            return null;

        return locationsByNpcName.TryGetValue(npcName, out var locations)
            ? locations.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            : null;
    }

    private void Build()
    {
        try
        {
            var namesByNpcId = new Dictionary<uint, string>();
            foreach (var npc in data.GetExcelSheet<ENpcResident>())
            {
                if (npc.RowId == 0)
                    continue;

                var name = ReflectionUtil.GetPropertyValue(npc, "Singular", "Name")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    namesByNpcId[npc.RowId] = name;
            }

            foreach (var level in data.GetExcelSheet<Level>())
            {
                var npcId = ReadRowId(level, "Object", "ENpc", "ENpcResident");
                if (npcId == 0 || !namesByNpcId.TryGetValue(npcId, out var npcName))
                    continue;

                var location = ResolveLevelLocation(level);
                if (string.IsNullOrWhiteSpace(location))
                    continue;

                if (!locationsByNpcName.TryGetValue(npcName, out var list))
                {
                    list = [];
                    locationsByNpcName[npcName] = list;
                }

                if (!list.Contains(location, StringComparer.OrdinalIgnoreCase))
                    list.Add(location);
            }

            log.Debug($"CQG NPC location index: {locationsByNpcName.Count:N0} named NPCs with placements.");
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not build generic NPC location index.");
        }
    }

    private string? ResolveLevelLocation(Level level)
    {
        try
        {
            var placeNameId = ReadRowId(level, "PlaceName");
            var direct = RowText<PlaceName>(placeNameId, "Name");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            var territoryId = ReadRowId(level, "Territory", "TerritoryType");
            if (territoryId == 0)
                return null;

            var territory = data.GetExcelSheet<TerritoryType>().GetRow(territoryId);
            var territoryPlaceId = ReadRowId(territory, "PlaceName");
            return RowText<PlaceName>(territoryPlaceId, "Name");
        }
        catch
        {
            return null;
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
                var text = ReflectionUtil.GetPropertyValue(row, propertyName)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch
        {
            // Enrichment only.
        }

        return null;
    }

    private static uint ReadRowId(object row, params string[] propertyNames)
    {
        var nested = ReflectionUtil.GetNestedRowId(row, propertyNames);
        if (nested != 0)
            return nested;

        var numeric = ReflectionUtil.GetNumericProperty(row, propertyNames);
        return numeric is > 0 and <= uint.MaxValue ? (uint)numeric.Value : 0;
    }
}
