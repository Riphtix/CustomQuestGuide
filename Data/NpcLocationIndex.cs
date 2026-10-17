using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

public sealed record NpcMapLocation(
    string PlaceName,
    uint TerritoryTypeId,
    uint MapId,
    Vector3 WorldPosition);

/// <summary>
/// Best-effort ENpc name -> in-world place lookup shared by all source types.
/// In v1.1 this also retains the actual Level placement so UI location names can
/// open the in-game map and place a flag without inventing coordinates.
/// </summary>
public sealed class NpcLocationIndex
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<string, List<string>> locationsByNpcName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<NpcMapLocation>> mapLocationsByNpcName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, List<NpcMapLocation>> mapLocationsByNpcId = [];
    private readonly Dictionary<uint, List<string>> locationsByNpcId = [];
    private readonly Dictionary<string, List<NpcMapLocation>> mapLocationsByPlaceName =
        new(StringComparer.OrdinalIgnoreCase);

    public NpcLocationIndex(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
        Build();
    }

    private static readonly IReadOnlyDictionary<string, string> CuratedLocationFallbacks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Some modern shop NPCs are not recoverable from the Level/ENpc
            // relationship exposed by the currently generated Lumina sheets.
            // Keep exact, verified fallbacks narrow; normal sheet-derived
            // placement always wins when it exists.
            ["Hhihwi"] = "Solution Nine (X:8.7, Y:13.4)",
            ["Eirene"] = "Solution Nine (X:8.1, Y:14.0)",
        };

    public string? GetBestLocation(string npcName)
    {
        npcName = (npcName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(npcName))
            return null;

        if (locationsByNpcName.TryGetValue(npcName, out var locations))
        {
            var live = locations.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(live))
                return live;
        }

        return CuratedLocationFallbacks.TryGetValue(npcName, out var fallback)
            ? fallback
            : null;
    }

    public NpcMapLocation? GetBestMapLocation(string npcName)
    {
        npcName = (npcName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(npcName))
            return null;

        return mapLocationsByNpcName.TryGetValue(npcName, out var locations)
            ? locations.FirstOrDefault(x => x.TerritoryTypeId != 0 && x.MapId != 0)
            : null;
    }

    public string? GetBestLocation(uint npcId)
    {
        if (npcId == 0 || !locationsByNpcId.TryGetValue(npcId, out var locations))
            return null;

        return locations.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    public NpcMapLocation? GetBestMapLocation(uint npcId)
    {
        if (npcId == 0 || !mapLocationsByNpcId.TryGetValue(npcId, out var locations))
            return null;

        return locations.FirstOrDefault(x => x.TerritoryTypeId != 0 && x.MapId != 0);
    }

    public NpcMapLocation? GetBestPlaceMapLocation(string placeName)
    {
        placeName = (placeName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(placeName))
            return null;

        return mapLocationsByPlaceName.TryGetValue(placeName, out var locations)
            ? locations.FirstOrDefault(x => x.TerritoryTypeId != 0 && x.MapId != 0)
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

            BuildGatheringPlaceMapLocations();

            foreach (var level in data.GetExcelSheet<Level>())
            {
                var location = ResolveLevelLocation(level);
                if (!string.IsNullOrWhiteSpace(location))
                {
                    var genericMapLocation = ResolveMapLocation(level, location);
                    if (genericMapLocation is not null)
                        AddPlaceMapLocation(location, genericMapLocation);
                }

                // GatheringPoint names such as "Everkeep Ward" are not Level.PlaceName
                // values. The Level row instead points at GatheringPoint through Object
                // when Type == 14. Resolve that linked row so gathering-source labels
                // can open the actual gathering-node area rather than only the broader
                // territory map.
                var levelType = ReflectionUtil.GetNumericProperty(level, "Type") ?? 0;
                if (levelType == 14)
                {
                    var gatheringPointId = ReadRowId(level, "Object", "GatheringPoint");
                    if (gatheringPointId != 0)
                    {
                        try
                        {
                            var gatheringPoint = data.GetExcelSheet<GatheringPoint>().GetRow(gatheringPointId);
                            var gatheringPlaceId = ReadRowId(gatheringPoint, "PlaceName");
                            var gatheringPlace = RowText<PlaceName>(gatheringPlaceId, "Name");
                            if (!string.IsNullOrWhiteSpace(gatheringPlace))
                            {
                                var gatheringMapLocation = ResolveMapLocation(level, gatheringPlace);
                                if (gatheringMapLocation is not null)
                                    AddPlaceMapLocation(gatheringPlace, gatheringMapLocation);
                            }
                        }
                        catch
                        {
                            // Best-effort map enrichment only.
                        }
                    }
                }

                var npcId = ReadRowId(level, "Object", "ENpc", "ENpcResident");
                if (npcId == 0 || !namesByNpcId.TryGetValue(npcId, out var npcName))
                    continue;

                if (string.IsNullOrWhiteSpace(location))
                    continue;

                if (!locationsByNpcName.TryGetValue(npcName, out var list))
                {
                    list = [];
                    locationsByNpcName[npcName] = list;
                }

                if (!list.Contains(location, StringComparer.OrdinalIgnoreCase))
                    list.Add(location);

                if (!locationsByNpcId.TryGetValue(npcId, out var idLocations))
                {
                    idLocations = [];
                    locationsByNpcId[npcId] = idLocations;
                }
                if (!idLocations.Contains(location, StringComparer.OrdinalIgnoreCase))
                    idLocations.Add(location);

                var mapLocation = ResolveMapLocation(level, location);
                if (mapLocation is not null)
                {
                    if (!mapLocationsByNpcName.TryGetValue(npcName, out var mapList))
                    {
                        mapList = [];
                        mapLocationsByNpcName[npcName] = mapList;
                    }

                    if (!mapList.Any(x =>
                            x.TerritoryTypeId == mapLocation.TerritoryTypeId &&
                            x.MapId == mapLocation.MapId &&
                            Vector3.DistanceSquared(x.WorldPosition, mapLocation.WorldPosition) < 0.01f))
                    {
                        mapList.Add(mapLocation);
                    }

                    if (!mapLocationsByNpcId.TryGetValue(npcId, out var idMapList))
                    {
                        idMapList = [];
                        mapLocationsByNpcId[npcId] = idMapList;
                    }
                    if (!idMapList.Any(x =>
                            x.TerritoryTypeId == mapLocation.TerritoryTypeId &&
                            x.MapId == mapLocation.MapId &&
                            Vector3.DistanceSquared(x.WorldPosition, mapLocation.WorldPosition) < 0.01f))
                    {
                        idMapList.Add(mapLocation);
                    }
                }
            }

            log.Debug($"CQG location index: {locationsByNpcName.Count:N0} named NPCs with placements; {mapLocationsByNpcName.Count:N0} have map-pinnable placements; {mapLocationsByPlaceName.Count:N0} named places have representative map placements.");
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not build generic NPC location index.");
        }
    }

    private void BuildGatheringPlaceMapLocations()
    {
        try
        {
            var exported = data.GetExcelSheet<ExportedGatheringPoint>();

            foreach (var point in data.GetExcelSheet<GatheringPoint>())
            {
                var baseId = ReadRowId(point, "GatheringPointBase", "Base");
                var placeNameId = ReadRowId(point, "PlaceName");
                var territoryId = ReadRowId(point, "TerritoryType", "Territory");
                if (baseId == 0 || placeNameId == 0 || territoryId == 0)
                    continue;

                var placeName = RowText<PlaceName>(placeNameId, "Name");
                if (string.IsNullOrWhiteSpace(placeName))
                    continue;

                uint mapId = 0;
                try
                {
                    var territory = data.GetExcelSheet<TerritoryType>().GetRow(territoryId);
                    mapId = ReadRowId(territory, "Map");
                }
                catch
                {
                    // Continue to the next point if the territory has no usable map.
                }

                if (mapId == 0)
                    continue;

                try
                {
                    // ExportedGatheringPoint is keyed by GatheringPointBase and
                    // contains world X/Y for gathering nodes. Dalamud's map-link
                    // API consumes world X/Z, so sheet Y maps to Vector3.Z here.
                    var exportedPoint = exported.GetRow(baseId);
                    var x = ReadFloat(exportedPoint, "X");
                    var y = ReadFloat(exportedPoint, "Y");
                    if (!x.HasValue || !y.HasValue)
                        continue;

                    AddPlaceMapLocation(
                        placeName,
                        new NpcMapLocation(
                            placeName,
                            territoryId,
                            mapId,
                            new Vector3(x.Value, 0f, y.Value)));
                }
                catch
                {
                    // Not every GatheringPointBase has an exported static
                    // coordinate. Those entries remain non-pinnable rather than
                    // receiving an invented position.
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not build ExportedGatheringPoint map index.");
        }
    }

    private void AddPlaceMapLocation(string placeName, NpcMapLocation mapLocation)
    {
        if (!mapLocationsByPlaceName.TryGetValue(placeName, out var list))
        {
            list = [];
            mapLocationsByPlaceName[placeName] = list;
        }

        if (!list.Any(x =>
                x.TerritoryTypeId == mapLocation.TerritoryTypeId &&
                x.MapId == mapLocation.MapId &&
                Vector3.DistanceSquared(x.WorldPosition, mapLocation.WorldPosition) < 0.01f))
        {
            list.Add(mapLocation);
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

    private NpcMapLocation? ResolveMapLocation(Level level, string placeName)
    {
        try
        {
            var territoryId = ReadRowId(level, "Territory", "TerritoryType");
            if (territoryId == 0)
                return null;

            var mapId = ReadRowId(level, "Map");
            if (mapId == 0)
            {
                var territory = data.GetExcelSheet<TerritoryType>().GetRow(territoryId);
                mapId = ReadRowId(territory, "Map");
            }
            if (mapId == 0)
                return null;

            var x = ReadFloat(level, "X");
            var y = ReadFloat(level, "Y");
            var z = ReadFloat(level, "Z");
            if (!x.HasValue || !z.HasValue)
                return null;

            return new NpcMapLocation(
                placeName,
                territoryId,
                mapId,
                new Vector3(x.Value, y ?? 0f, z.Value));
        }
        catch
        {
            return null;
        }
    }

    private static float? ReadFloat(object row, params string[] propertyNames)
    {
        var raw = ReflectionUtil.GetPropertyValue(row, propertyNames);
        if (raw is null)
            return null;
        try
        {
            return Convert.ToSingle(raw);
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
