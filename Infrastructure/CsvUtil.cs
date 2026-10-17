using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

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

public sealed record MobSpawnCoordinateAuditEntry(
    uint TerritoryTypeId,
    string TerritoryName,
    ushort? MapSizeFactor,
    float X,
    float Y,
    float? Z,
    int TerritoryRowCount,
    int TerritoryMonsterCount,
    int GlobalRowCount,
    int GlobalTerritoryCount,
    int GlobalMonsterCount,
    bool XEqualsY,
    string BundledClassification,
    string CandidateReason);

public sealed record MobSpawnCoordinateAuditReport(
    int RawRowCount,
    int ParsedPositionRowCount,
    int ParsedTerritoryPositionRowCount,
    int UniqueExactPositionTupleCount,
    string DataFingerprintSha256,
    IReadOnlyList<MobSpawnCoordinateAuditEntry> Entries);

public sealed record MobSpawnConfidenceTestCase(
    string Kind,
    uint MonsterBaseId,
    uint MonsterNameId,
    string MonsterName,
    uint TerritoryTypeId,
    string TerritoryName,
    float X,
    float Y,
    float? Z,
    uint ExampleItemId);


public sealed record FateMonsterContextCandidate(
    string Kind,
    uint MonsterBaseId,
    uint MonsterNameId,
    string MonsterName,
    uint ExampleItemId,
    bool HasVerifiedLocation,
    IReadOnlyList<string> FateNames);

public sealed class SupplementalCsvIndex
{
    public const string BundledMobSpawnBlacklistSourceVersion = MobSpawnCoordinateOverrides.SourcePackageVersion;

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

    public string SupplementalAssemblyVersion =>
        assembly?.GetName().Version?.ToString() ?? "unknown";

    public string SupplementalAssemblyInformationalVersion =>
        assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public string SupplementalAssemblyFileVersion =>
        assembly?.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "unknown";

    // BNpcName -> known spawn levels. MobDrop tells us which named enemy drops
    // an item; MobSpawn + BNpcLink provide the level information needed to
    // order those enemies usefully for players. Keep every known level because
    // a named enemy can legitimately appear at more than one level.
    private readonly Dictionary<uint, SortedSet<uint>> mobLevelsByNameId = [];

    // BNpcName -> lowest known level band of a territory in which the monster
    // spawns. FFXIV does not expose a single canonical "zone level" value, so
    // CQG derives a stable player-facing ordering key from MobSpawn itself: the
    // lowest monster level observed in that TerritoryType. This lets lower-level
    // zones sort ahead of later zones while preserving monster level as the next
    // ordering key.
    private readonly Dictionary<uint, uint> mobZoneLevelsByNameId = [];

    // BNpcName -> known overworld spawn points. These come from the same local
    // LuminaSupplemental MobSpawn dataset used for level ordering, so showing
    // and pinning them does not add any network traffic.
    private readonly Dictionary<uint, List<MonsterSpawnLocation>> mobSpawnLocationsByNameId = [];

    // MobSpawn is natively keyed by BNpcBase. Keep the base-keyed form too so
    // Garland's composite mob IDs can join directly to local spawn data without
    // depending on a BNpcLink/name bridge.
    private readonly Dictionary<uint, SortedSet<uint>> mobLevelsByBaseId = [];
    private readonly Dictionary<uint, uint> mobZoneLevelsByBaseId = [];
    private readonly Dictionary<uint, List<MonsterSpawnLocation>> mobSpawnLocationsByBaseId = [];

    // BNpcName is a display/name row, not a complete battle-NPC identity. One
    // BNpcName can be linked to multiple BNpcBase rows. Spawn ownership must
    // therefore use the same composite identity as Garland: BNpcBase + BNpcName.
    private readonly Dictionary<uint, HashSet<uint>> mobBaseIdsByNameId = [];
    private readonly Dictionary<(uint BaseId, uint NameId), SortedSet<uint>> mobLevelsByIdentity = [];
    private readonly Dictionary<(uint BaseId, uint NameId), uint> mobZoneLevelsByIdentity = [];
    private readonly Dictionary<(uint BaseId, uint NameId), List<MonsterSpawnLocation>> mobSpawnLocationsByIdentity = [];

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
            BuildMobMetadata();

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


    /// <summary>
    /// Returns the lowest known level for a named monster. Lowest level is the
    /// useful ordering key for a farming guide; all spawn-location detail stays
    /// available for the later per-monster location expansion work.
    /// </summary>
    public uint? GetLowestMobLevel(uint bnpcNameId)
    {
        return bnpcNameId != 0 &&
               mobLevelsByNameId.TryGetValue(bnpcNameId, out var levels) &&
               levels.Count > 0
            ? levels.Min
            : null;
    }


    /// <summary>
    /// Returns the lowest derived zone-level band for a named monster. The band
    /// is the lowest MobSpawn level observed in any TerritoryType containing that
    /// monster, which gives CQG a useful lower-zone-first ordering without
    /// inventing a game-sheet field that does not exist.
    /// </summary>
    public uint? GetLowestMobZoneLevel(uint bnpcNameId)
    {
        return bnpcNameId != 0 &&
               mobZoneLevelsByNameId.TryGetValue(bnpcNameId, out var level) &&
               level != 0
            ? level
            : null;
    }

    /// <summary>
    /// Returns every known overworld spawn coordinate for a named monster.
    /// Locations are ordered low-zone-first, then by zone and coordinate so the
    /// first entry is stable enough to use as the collapsed representative row.
    /// </summary>
    public IReadOnlyList<MonsterSpawnLocation> GetMobSpawnLocations(uint bnpcNameId)
    {
        if (bnpcNameId == 0 || !mobSpawnLocationsByNameId.TryGetValue(bnpcNameId, out var locations))
            return Array.Empty<MonsterSpawnLocation>();

        return locations
            .OrderBy(x => x.Confidence)
            .ThenBy(x => x.ZoneLevel ?? uint.MaxValue)
            .ThenBy(x => x.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.X)
            .ThenBy(x => x.Y)
            .ToArray();
    }

    public uint? GetLowestMobLevelByBase(uint bnpcBaseId)
    {
        return bnpcBaseId != 0 &&
               mobLevelsByBaseId.TryGetValue(bnpcBaseId, out var levels) &&
               levels.Count > 0
            ? levels.Min
            : null;
    }

    public uint? GetLowestMobZoneLevelByBase(uint bnpcBaseId)
    {
        return bnpcBaseId != 0 &&
               mobZoneLevelsByBaseId.TryGetValue(bnpcBaseId, out var level) &&
               level != 0
            ? level
            : null;
    }

    public IReadOnlyList<MonsterSpawnLocation> GetMobSpawnLocationsByBase(uint bnpcBaseId)
    {
        if (bnpcBaseId == 0 || !mobSpawnLocationsByBaseId.TryGetValue(bnpcBaseId, out var locations))
            return Array.Empty<MonsterSpawnLocation>();

        return locations
            .OrderBy(x => x.Confidence)
            .ThenBy(x => x.ZoneLevel ?? uint.MaxValue)
            .ThenBy(x => x.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.X)
            .ThenBy(x => x.Y)
            .ToArray();
    }

    /// <summary>
    /// Returns the only BNpcBase linked to a BNpcName, or null when that name is
    /// shared by multiple battle-NPC bases. Ambiguity is intentionally preserved
    /// rather than guessed.
    /// </summary>
    public uint? GetUniqueMobBaseId(uint bnpcNameId)
    {
        if (bnpcNameId == 0 ||
            !mobBaseIdsByNameId.TryGetValue(bnpcNameId, out var baseIds) ||
            baseIds.Count != 1)
        {
            return null;
        }

        return baseIds.First();
    }

    public uint? GetLowestMobLevel(uint bnpcBaseId, uint bnpcNameId)
    {
        var key = (bnpcBaseId, bnpcNameId);
        return bnpcBaseId != 0 && bnpcNameId != 0 &&
               mobLevelsByIdentity.TryGetValue(key, out var levels) && levels.Count > 0
            ? levels.Min
            : null;
    }

    public uint? GetLowestMobZoneLevel(uint bnpcBaseId, uint bnpcNameId)
    {
        var key = (bnpcBaseId, bnpcNameId);
        return bnpcBaseId != 0 && bnpcNameId != 0 &&
               mobZoneLevelsByIdentity.TryGetValue(key, out var level) && level != 0
            ? level
            : null;
    }

    public IReadOnlyList<MonsterSpawnLocation> GetMobSpawnLocations(uint bnpcBaseId, uint bnpcNameId)
    {
        var key = (bnpcBaseId, bnpcNameId);
        if (bnpcBaseId == 0 || bnpcNameId == 0 ||
            !mobSpawnLocationsByIdentity.TryGetValue(key, out var locations))
        {
            return Array.Empty<MonsterSpawnLocation>();
        }

        return locations
            .OrderBy(x => x.Confidence)
            .ThenBy(x => x.ZoneLevel ?? uint.MaxValue)
            .ThenBy(x => x.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.X)
            .ThenBy(x => x.Y)
            .ToArray();
    }

    private void BuildMobMetadata()
    {
        try
        {
            // MobSpawn is primarily keyed by BNpcBase while MobDrop uses
            // BNpcName. BNpcLink is the supplemental bridge between them.
            var namesByBase = new Dictionary<uint, HashSet<uint>>();

            foreach (var row in ReadDataset("BNpcLink"))
            {
                // Identity joins must use exact column/property names. The generic
                // FirstIdContaining helper is intentionally permissive for broad
                // supplemental datasets, but using it here can mistake a nested or
                // similarly-named ID for the monster identity and fabricate spawns.
                var nameId = FirstIdNamed(row, "BNpcName", "BNpcNameId");
                var baseId = FirstIdNamed(row, "BNpcBase", "BNpcBaseId");
                if (nameId == 0 || baseId == 0)
                    continue;

                if (!namesByBase.TryGetValue(baseId, out var names))
                {
                    names = [];
                    namesByBase[baseId] = names;
                }

                names.Add(nameId);

                if (!mobBaseIdsByNameId.TryGetValue(nameId, out var bases))
                {
                    bases = [];
                    mobBaseIdsByNameId[nameId] = bases;
                }

                bases.Add(baseId);
            }

            // Read MobSpawn once into lightweight facts. Keep coordinate facts
            // even when a row does not expose a monster level; a useful map pin
            // should not disappear merely because level metadata is incomplete.
            var spawnFacts = new List<(
                uint NameId,
                uint BaseId,
                uint TerritoryId,
                uint Level,
                float? X,
                float? Y,
                uint? MapId,
                float? WorldX,
                float? WorldZ,
                bool IsFallback,
                MonsterCoordinateConfidence CoordinateConfidence)>();
            var territoryLowestLevel = new Dictionary<uint, uint>();

            foreach (var row in ReadDataset("MobSpawn"))
            {
                var level = ReadMobSpawnLevel(row);
                var territoryId = FirstIdNamed(
                    row,
                    "TerritoryType", "TerritoryTypeId",
                    "Territory", "TerritoryId");

                if (level != 0 && territoryId != 0 &&
                    (!territoryLowestLevel.TryGetValue(territoryId, out var current) || level < current))
                {
                    territoryLowestLevel[territoryId] = level;
                }

                TryReadMobSpawnCoordinates(
                    row,
                    territoryId,
                    out var x,
                    out var y,
                    out var mapId,
                    out var worldX,
                    out var worldZ,
                    out var coordinateConfidence);

                var exactNames = new HashSet<uint>();
                var inferredNames = new HashSet<uint>();

                // A BNpcBase can be shared by several differently named enemies.
                // Treat the base as a candidate lookup only; it is not sufficient
                // proof that a MobSpawn row belongs to every linked BNpcName.
                //
                // If MobSpawn exposes BNpcName directly, that relationship is exact.
                // If it exposes only BNpcBase, promote the row to a named monster
                // only when BNpcLink maps that base to exactly one name. Ambiguous
                // bases remain available in the base-level diagnostic index, but
                // are never copied into named-monster locations. Garland's exact
                // BNpcBase+BNpcName identity can fill those gaps on demand.
                var directNameId = FirstIdNamed(row, "BNpcName", "BNpcNameId");
                if (directNameId != 0)
                    exactNames.Add(directNameId);

                var baseId = FirstIdNamed(row, "BNpcBase", "BNpcBaseId");
                if (baseId != 0)
                {
                    if (level != 0)
                        AddMobLevelByBase(baseId, level);

                    // Keep the base aggregate for diagnostics/fallback ordering, but
                    // do not use it as an exact named-monster location relationship.
                    spawnFacts.Add((0, baseId, territoryId, level, x, y, mapId, worldX, worldZ, true, coordinateConfidence));

                    if (directNameId == 0 &&
                        namesByBase.TryGetValue(baseId, out var linkedNames) &&
                        linkedNames.Count == 1)
                    {
                        inferredNames.Add(linkedNames.First());
                    }
                }

                foreach (var nameId in exactNames)
                {
                    if (level != 0)
                        AddMobLevel(nameId, level);

                    spawnFacts.Add((nameId, baseId, territoryId, level, x, y, mapId, worldX, worldZ, false, coordinateConfidence));
                }

                // A unique BNpcBase -> BNpcName bridge is useful when the spawn
                // row omits BNpcName, but it is still an inferred relationship.
                // Keep it as fallback-only so directly named MobSpawn rows always
                // win and inferred coordinates never pollute an exact spawn list.
                foreach (var nameId in inferredNames)
                {
                    if (level != 0)
                        AddMobLevel(nameId, level);

                    spawnFacts.Add((nameId, baseId, territoryId, level, x, y, mapId, worldX, worldZ, true, coordinateConfidence));
                }
            }

            foreach (var fact in spawnFacts)
            {
                uint? zoneLevel = null;
                if (fact.TerritoryId != 0 &&
                    territoryLowestLevel.TryGetValue(fact.TerritoryId, out var resolvedZoneLevel))
                {
                    zoneLevel = resolvedZoneLevel;

                    if (fact.NameId != 0 &&
                        (!mobZoneLevelsByNameId.TryGetValue(fact.NameId, out var current) || resolvedZoneLevel < current))
                    {
                        mobZoneLevelsByNameId[fact.NameId] = resolvedZoneLevel;
                    }

                    if (fact.BaseId != 0 &&
                        (!mobZoneLevelsByBaseId.TryGetValue(fact.BaseId, out var currentBase) || resolvedZoneLevel < currentBase))
                    {
                        mobZoneLevelsByBaseId[fact.BaseId] = resolvedZoneLevel;
                    }
                }

                if (fact.NameId != 0 && fact.BaseId != 0)
                {
                    var identity = (fact.BaseId, fact.NameId);
                    if (fact.Level != 0)
                        AddMobLevelByIdentity(identity, fact.Level);

                    if (zoneLevel.HasValue &&
                        (!mobZoneLevelsByIdentity.TryGetValue(identity, out var currentIdentityZone) ||
                         zoneLevel.Value < currentIdentityZone))
                    {
                        mobZoneLevelsByIdentity[identity] = zoneLevel.Value;
                    }
                }

                if (fact.TerritoryId == 0 || !fact.X.HasValue || !fact.Y.HasValue)
                    continue;

                var zoneName = ResolveTerritoryName(fact.TerritoryId);
                if (string.IsNullOrWhiteSpace(zoneName))
                    continue;

                var location = new MonsterSpawnLocation(
                    fact.TerritoryId,
                    zoneName,
                    fact.X.Value,
                    fact.Y.Value,
                    zoneLevel,
                    fact.MapId,
                    fact.WorldX,
                    fact.WorldZ,
                    IsFallback: fact.IsFallback,
                    Confidence: fact.CoordinateConfidence);

                if (fact.NameId != 0)
                    AddMobSpawnLocation(fact.NameId, location);

                if (fact.BaseId != 0)
                    AddMobSpawnLocationByBase(fact.BaseId, location);

                if (fact.NameId != 0 && fact.BaseId != 0)
                    AddMobSpawnLocationByIdentity((fact.BaseId, fact.NameId), location);
            }

            log.Information(
                $"CQG supplemental mob metadata: namedMobsWithLevels={mobLevelsByNameId.Count:N0}, " +
                $"namedMobsWithZoneLevels={mobZoneLevelsByNameId.Count:N0}, " +
                $"namedMobsWithLocations={mobSpawnLocationsByNameId.Count:N0}, " +
                $"baseMobsWithLocations={mobSpawnLocationsByBaseId.Count:N0}, " +
                $"exactMobIdentitiesWithLocations={mobSpawnLocationsByIdentity.Count:N0}.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not initialize supplemental monster metadata.");
        }
    }

    private void AddMobSpawnLocation(uint bnpcNameId, MonsterSpawnLocation location)
    {
        if (!mobSpawnLocationsByNameId.TryGetValue(bnpcNameId, out var locations))
        {
            locations = [];
            mobSpawnLocationsByNameId[bnpcNameId] = locations;
        }

        // Community spawn datasets can contain duplicate observations. Treat
        // coordinates within one tenth of a map unit as the same pin. If two
        // observations collapse to one displayed coordinate, keep the higher
        // confidence classification.
        var duplicateIndex = locations.FindIndex(existing =>
            existing.TerritoryTypeId == location.TerritoryTypeId &&
            Math.Abs(existing.X - location.X) < 0.1f &&
            Math.Abs(existing.Y - location.Y) < 0.1f);
        if (duplicateIndex >= 0)
        {
            if (location.Confidence < locations[duplicateIndex].Confidence)
                locations[duplicateIndex] = location;
            return;
        }

        locations.Add(location);
    }

    private string? ResolveTerritoryName(uint territoryId)
    {
        if (territoryId == 0)
            return null;

        try
        {
            var territory = data.GetExcelSheet<TerritoryType>().GetRow(territoryId);
            var placeNameId = ReflectionUtil.GetNestedRowId(territory, "PlaceName");
            if (placeNameId == 0)
                return null;

            var place = data.GetExcelSheet<PlaceName>().GetRow(placeNameId);
            var name = place.Name.ExtractText().Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an on-demand diagnostic view of raw LuminaSupplemental MobSpawn
    /// Position tuples. This is intentionally not built during plugin startup or
    /// normal rendering. Its purpose is to make upstream placeholder re-auditing
    /// straightforward whenever the bundled LuminaSupplemental version changes.
    ///
    /// The report does not mutate the runtime blacklist and does not guess that a
    /// row is invalid. It merely surfaces exact tuples that are unusually reused,
    /// plus every tuple currently matched by CQG's explicit bundled blacklist.
    /// </summary>
    public MobSpawnCoordinateAuditReport BuildMobSpawnCoordinateAudit()
    {
        var rows = ReadDataset("MobSpawn");
        var rawFacts = new List<(uint TerritoryId, uint BaseId, uint NameId, float X, float Y, float? Z)>();
        var parsedPositionRows = 0;

        foreach (var row in rows)
        {
            // Use the exact same position decoder as runtime MobSpawn indexing.
            // LuminaSupplemental 5.x commonly exposes Position as one serialized
            // vector value rather than three flattened Position.X/Y/Z columns.
            // The previous audit only checked flattened members, so it silently
            // scanned zero coordinates while normal monster resolution continued
            // to parse the serialized Position field successfully.
            if (!TryReadRawMobSpawnPosition(row, out var x, out var y, out var z))
                continue;

            parsedPositionRows++;

            var territoryId = FirstIdNamed(
                row,
                "TerritoryType", "TerritoryTypeId",
                "Territory", "TerritoryId");
            if (territoryId == 0)
                continue;

            var baseId = FirstIdNamed(row, "BNpcBase", "BNpcBaseId");
            var nameId = FirstIdNamed(row, "BNpcName", "BNpcNameId");
            rawFacts.Add((territoryId, baseId, nameId, x, y, z));
        }

        static string MonsterKey(uint baseId, uint nameId) => $"{baseId}:{nameId}";

        var globalStats = rawFacts
            .GroupBy(x => (x.X, x.Y, x.Z))
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    RowCount = g.Count(),
                    TerritoryCount = g.Select(x => x.TerritoryId).Distinct().Count(),
                    MonsterCount = g.Select(x => MonsterKey(x.BaseId, x.NameId)).Distinct().Count(),
                });

        var result = new List<MobSpawnCoordinateAuditEntry>();
        foreach (var group in rawFacts.GroupBy(x => (x.TerritoryId, x.X, x.Y, x.Z)))
        {
            var first = group.First();
            var global = globalStats[(first.X, first.Y, first.Z)];
            var territoryMonsterCount = group
                .Select(x => MonsterKey(x.BaseId, x.NameId))
                .Distinct()
                .Count();
            var xEqualsY = NearlyEqual(first.X, first.Y, 0.00001f);
            var disposition = MobSpawnCoordinateOverrides.Classify(
                first.TerritoryId,
                first.X,
                first.Y,
                first.Z);
            var bundledClassification = disposition.ToString();

            var reason = string.Empty;
            if (disposition == MobSpawnCoordinateDisposition.Suppress)
            {
                reason = "Bundled confirmed placeholder";
            }
            else if (disposition == MobSpawnCoordinateDisposition.Unverified)
            {
                reason = "Bundled unverified coordinate";
            }
            else if (xEqualsY && global.TerritoryCount >= 2 && global.RowCount >= 4)
            {
                reason = "Exact X=Y tuple reused across multiple territories";
            }
            else if (xEqualsY && group.Count() >= 4)
            {
                reason = "Exact X=Y tuple repeatedly reused within this territory";
            }
            else if (global.TerritoryCount >= 3 && global.RowCount >= 10)
            {
                reason = "Exact position tuple heavily reused across territories";
            }

            if (string.IsNullOrWhiteSpace(reason))
                continue;

            result.Add(new MobSpawnCoordinateAuditEntry(
                first.TerritoryId,
                ResolveTerritoryName(first.TerritoryId) ?? string.Empty,
                TryGetTerritoryMapSizeFactor(first.TerritoryId),
                first.X,
                first.Y,
                first.Z,
                group.Count(),
                territoryMonsterCount,
                global.RowCount,
                global.TerritoryCount,
                global.MonsterCount,
                xEqualsY,
                bundledClassification,
                reason));
        }

        var entries = result
            .OrderByDescending(x => x.BundledClassification == nameof(MobSpawnCoordinateDisposition.Suppress))
            .ThenByDescending(x => x.BundledClassification == nameof(MobSpawnCoordinateDisposition.Unverified))
            .ThenByDescending(x => x.GlobalTerritoryCount)
            .ThenByDescending(x => x.GlobalRowCount)
            .ThenBy(x => x.X)
            .ThenBy(x => x.Y)
            .ThenBy(x => x.Z ?? float.MaxValue)
            .ThenBy(x => x.TerritoryTypeId)
            .ToArray();

        // Fingerprint the normalized MobSpawn facts instead of comparing NuGet's
        // package version (5.x) to the assembly's intentionally stable CLR version
        // (1.0.0.0). This is the value to compare between audits after an upstream
        // package/data update.
        var fingerprintText = new StringBuilder(rawFacts.Count * 48);
        foreach (var fact in rawFacts
                     .OrderBy(x => x.TerritoryId)
                     .ThenBy(x => x.BaseId)
                     .ThenBy(x => x.NameId)
                     .ThenBy(x => x.X)
                     .ThenBy(x => x.Y)
                     .ThenBy(x => x.Z ?? float.MinValue))
        {
            fingerprintText
                .Append(fact.TerritoryId).Append('|')
                .Append(fact.BaseId).Append('|')
                .Append(fact.NameId).Append('|')
                .Append(fact.X.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(fact.Y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(fact.Z?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty)
                .Append('\n');
        }

        var fingerprintBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText.ToString()));
        var fingerprint = Convert.ToHexString(fingerprintBytes);

        return new MobSpawnCoordinateAuditReport(
            rows.Count,
            parsedPositionRows,
            rawFacts.Count,
            globalStats.Count,
            fingerprint,
            entries);
    }


    /// <summary>
    /// Finds a small set of real MobSpawn/item examples for visually testing CQG's
    /// coordinate-confidence presentation. This is Debug-only and scans the loaded
    /// supplemental datasets only when explicitly requested.
    /// </summary>
    public IReadOnlyList<MobSpawnConfidenceTestCase> BuildMobSpawnConfidenceTestCases()
    {
        var facts = new List<(uint TerritoryId, uint BaseId, uint NameId, float X, float Y, float? Z, MobSpawnCoordinateDisposition Disposition)>();
        foreach (var row in ReadDataset("MobSpawn"))
        {
            if (!TryReadRawMobSpawnPosition(row, out var x, out var y, out var z) ||
                !IsPlausibleMapPair(x, y))
                continue;

            var territoryId = FirstIdNamed(row, "TerritoryType", "TerritoryTypeId", "Territory", "TerritoryId");
            if (territoryId == 0)
                continue;

            var baseId = FirstIdNamed(row, "BNpcBase", "BNpcBaseId");
            var nameId = FirstIdNamed(row, "BNpcName", "BNpcNameId");
            if (baseId == 0 && nameId == 0)
                continue;

            facts.Add((
                territoryId, baseId, nameId, x, y, z,
                MobSpawnCoordinateOverrides.Classify(territoryId, x, y, z)));
        }

        bool SameMonster((uint TerritoryId, uint BaseId, uint NameId, float X, float Y, float? Z, MobSpawnCoordinateDisposition Disposition) a,
                         (uint TerritoryId, uint BaseId, uint NameId, float X, float Y, float? Z, MobSpawnCoordinateDisposition Disposition) b)
        {
            if (a.NameId != 0 && b.NameId != 0 && a.NameId != b.NameId)
                return false;
            if (a.BaseId != 0 && b.BaseId != 0 && a.BaseId != b.BaseId)
                return false;
            return (a.NameId != 0 && a.NameId == b.NameId) || (a.BaseId != 0 && a.BaseId == b.BaseId);
        }

        bool HasLocallyCompleteMonster(uint baseId, uint nameId)
        {
            uint? level = baseId != 0 && nameId != 0
                ? GetLowestMobLevel(baseId, nameId)
                : nameId != 0
                    ? GetLowestMobLevel(nameId)
                    : null;

            IReadOnlyList<MonsterSpawnLocation> locations = baseId != 0 && nameId != 0
                ? GetMobSpawnLocations(baseId, nameId)
                : nameId != 0
                    ? GetMobSpawnLocations(nameId)
                    : Array.Empty<MonsterSpawnLocation>();

            return level.HasValue && locations.Count > 0;
        }

        uint FindExampleItem(uint baseId, uint nameId)
        {
            foreach (var pair in ItemSources.OrderBy(x => x.Key))
            {
                foreach (var record in pair.Value)
                {
                    if (!record.Dataset.Equals("MobDrop", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dropNameId = FirstIdNamed(record.Row, "BNpcName", "BNpcNameId");
                    var dropBaseId = FirstIdNamed(record.Row, "BNpcBase", "BNpcBaseId");

                    if (nameId != 0 && dropNameId != 0 && nameId != dropNameId)
                        continue;
                    if (baseId != 0 && dropBaseId != 0 && baseId != dropBaseId)
                        continue;

                    if ((nameId != 0 && dropNameId == nameId) ||
                        (baseId != 0 && dropBaseId == baseId))
                        return pair.Key;
                }
            }

            return 0;
        }

        string ResolveMonsterName(uint nameId)
        {
            if (nameId == 0)
                return "Unknown monster";
            try
            {
                var row = data.GetExcelSheet<BNpcName>().GetRow(nameId);
                var name = row.Singular.ExtractText().Trim();
                return string.IsNullOrWhiteSpace(name) ? $"BNpcName {nameId}" : name;
            }
            catch
            {
                return $"BNpcName {nameId}";
            }
        }

        MobSpawnConfidenceTestCase? Pick(string kind, Func<(uint TerritoryId, uint BaseId, uint NameId, float X, float Y, float? Z, MobSpawnCoordinateDisposition Disposition), bool> predicate)
        {
            foreach (var fact in facts.Where(predicate)
                                      .OrderByDescending(x => HasLocallyCompleteMonster(x.BaseId, x.NameId))
                                      .ThenBy(x => x.NameId)
                                      .ThenBy(x => x.BaseId)
                                      .ThenBy(x => x.TerritoryId))
            {
                var itemId = FindExampleItem(fact.BaseId, fact.NameId);
                if (itemId == 0)
                    continue;

                return new MobSpawnConfidenceTestCase(
                    kind,
                    fact.BaseId,
                    fact.NameId,
                    ResolveMonsterName(fact.NameId),
                    fact.TerritoryId,
                    ResolveTerritoryName(fact.TerritoryId) ?? $"Territory {fact.TerritoryId}",
                    fact.X,
                    fact.Y,
                    fact.Z,
                    itemId);
            }

            return null;
        }

        var results = new List<MobSpawnConfidenceTestCase>();

        var unverifiedWithVerified = Pick(
            "Unverified + verified",
            fact => fact.Disposition == MobSpawnCoordinateDisposition.Unverified &&
                    facts.Any(other => SameMonster(fact, other) &&
                                       other.Disposition == MobSpawnCoordinateDisposition.Normal));
        if (unverifiedWithVerified is not null)
            results.Add(unverifiedWithVerified);

        var unverifiedOnly = Pick(
            "Unverified only",
            fact => fact.Disposition == MobSpawnCoordinateDisposition.Unverified &&
                    !facts.Any(other => SameMonster(fact, other) &&
                                        other.Disposition == MobSpawnCoordinateDisposition.Normal));
        if (unverifiedOnly is not null)
            results.Add(unverifiedOnly);

        var suppressed = Pick(
            "Suppressed placeholder",
            fact => fact.Disposition == MobSpawnCoordinateDisposition.Suppress);
        if (suppressed is not null)
            results.Add(suppressed);

        return results;
    }

    /// <summary>
    /// Returns the named FATE sources explicitly attached to an item by
    /// LuminaSupplemental FateItem. This is item-scoped evidence; it is deliberately
    /// not promoted into the global monster cache because the dataset does not claim
    /// a universal monster -> FATE relationship.
    /// </summary>
    public IReadOnlyList<string> GetFateNamesForItem(uint itemId)
    {
        if (itemId == 0 || !ItemSources.TryGetValue(itemId, out var records))
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!record.Dataset.Equals("FateItem", StringComparison.OrdinalIgnoreCase))
                continue;

            var fateId = FirstIdContaining(record.Row, "Fate");
            if (fateId == 0)
                continue;

            try
            {
                var fate = data.GetExcelSheet<Fate>().GetRow(fateId);
                var name = fate.Name.ExtractText().Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
            catch
            {
                // A supplemental relation without a resolvable Fate row is not
                // actionable enough to surface as monster context.
            }
        }

        return names
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public uint FindExampleMobDropItem(uint baseId, uint nameId)
    {
        foreach (var pair in ItemSources.OrderBy(x => x.Key))
        {
            foreach (var record in pair.Value)
            {
                if (!record.Dataset.Equals("MobDrop", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dropNameId = FirstIdNamed(record.Row, "BNpcName", "BNpcNameId");
                var dropBaseId = FirstIdNamed(record.Row, "BNpcBase", "BNpcBaseId");

                if (nameId != 0 && dropNameId != 0 && nameId != dropNameId)
                    continue;
                if (baseId != 0 && dropBaseId != 0 && baseId != dropBaseId)
                    continue;

                if ((nameId != 0 && dropNameId == nameId) ||
                    (baseId != 0 && dropBaseId == baseId))
                    return pair.Key;
            }
        }

        return 0;
    }

    /// <summary>
    /// Local, zero-network debug finder for the conservative item-scoped FATE
    /// relationship policy. A FATE is attached to a monster only when the item has
    /// explicit FateItem evidence and exactly one concrete MobDrop identity. This
    /// avoids guessing among several monsters that happen to drop the same item.
    /// </summary>
    public IReadOnlyList<FateMonsterContextCandidate> BuildFateMonsterContextCandidates()
    {
        var candidates = new List<FateMonsterContextCandidate>();

        foreach (var pair in ItemSources.OrderBy(x => x.Key))
        {
            var fateNames = GetFateNamesForItem(pair.Key);
            if (fateNames.Count == 0)
                continue;

            var monsters = new Dictionary<(uint BaseId, uint NameId), (string Name, bool HasVerified)>();
            foreach (var record in pair.Value)
            {
                if (!record.Dataset.Equals("MobDrop", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nameId = FirstIdNamed(record.Row, "BNpcName", "BNpcNameId");
                var baseId = FirstIdNamed(record.Row, "BNpcBase", "BNpcBaseId");
                if (baseId == 0 && nameId != 0)
                    baseId = GetUniqueMobBaseId(nameId) ?? 0;
                if (baseId == 0 || nameId == 0)
                    continue;

                string monsterName;
                try
                {
                    monsterName = data.GetExcelSheet<BNpcName>().GetRow(nameId).Singular.ExtractText().Trim();
                }
                catch
                {
                    monsterName = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(monsterName))
                    monsterName = $"BNpcName {nameId}";

                var hasVerified = GetMobSpawnLocations(baseId, nameId)
                    .Any(x => x.Confidence == MonsterCoordinateConfidence.Verified);

                monsters[(baseId, nameId)] = (monsterName, hasVerified);
            }

            // The item-level datasets prove only that this item has both a FATE
            // source and monster source(s). With multiple monsters there is no
            // authoritative local join telling us which one belongs to the FATE.
            if (monsters.Count != 1)
                continue;

            var monster = monsters.Single();
            var kind = monster.Value.HasVerified
                ? "FATE + verified location"
                : "FATE context only";

            candidates.Add(new FateMonsterContextCandidate(
                kind,
                monster.Key.BaseId,
                monster.Key.NameId,
                monster.Value.Name,
                pair.Key,
                monster.Value.HasVerified,
                fateNames));
        }

        var results = new List<FateMonsterContextCandidate>();
        var withVerified = candidates.FirstOrDefault(x => x.HasVerifiedLocation);
        var contextOnly = candidates.FirstOrDefault(x => !x.HasVerifiedLocation);
        var multiple = candidates.FirstOrDefault(x => x.FateNames.Count > 1);

        if (withVerified is not null)
            results.Add(withVerified);
        if (contextOnly is not null && !results.Any(x => x.ExampleItemId == contextOnly.ExampleItemId))
            results.Add(contextOnly);
        if (multiple is not null && !results.Any(x => x.ExampleItemId == multiple.ExampleItemId))
            results.Add(multiple with { Kind = "Multiple FATE relationships" });

        return results;
    }

    private bool TryReadMobSpawnCoordinates(
        IReadOnlyDictionary<string, string> row,
        uint territoryTypeId,
        out float? mapX,
        out float? mapY,
        out uint? mapId,
        out float? worldX,
        out float? worldZ,
        out MonsterCoordinateConfidence confidence)
    {
        mapX = null;
        mapY = null;
        mapId = null;
        worldX = null;
        worldZ = null;
        confidence = MonsterCoordinateConfidence.Verified;

        // Prefer fields that explicitly identify themselves as player-facing map
        // coordinates. These need no world-space conversion.
        var explicitMapX = ReadFloat(row, "MapX", "MapCoordinateX", "MapPositionX");
        var explicitMapY = ReadFloat(row, "MapY", "MapCoordinateY", "MapPositionY");
        if (IsPlausibleMapPair(explicitMapX, explicitMapY))
        {
            mapX = explicitMapX;
            mapY = explicitMapY;
            return true;
        }

        // LuminaSupplemental MobSpawn.Position stores player-facing map X/Y.
        // Decode it through one shared helper so runtime resolution and the Debug
        // blacklist audit see precisely the same raw tuple, including the third
        // component used to identify verified placeholder rows.
        if (TryReadRawMobSpawnPosition(row, out var positionX, out var positionY, out var positionZ))
        {
            if (!IsPlausibleMapPair(positionX, positionY))
                return false;

            var disposition = MobSpawnCoordinateOverrides.Classify(
                territoryTypeId,
                positionX,
                positionY,
                positionZ);

            if (disposition == MobSpawnCoordinateDisposition.Suppress)
                return false;

            if (disposition == MobSpawnCoordinateDisposition.Unverified)
                confidence = MonsterCoordinateConfidence.Unverified;

            mapX = positionX;
            mapY = positionY;
            return true;
        }

        // Fields that explicitly identify themselves as world coordinates follow
        // the normal world -> map normalization path.
        var rawWorldX = ReadFloat(row, "WorldX");
        var rawWorldZ = ReadFloat(row, "WorldZ");
        if (rawWorldX.HasValue && rawWorldZ.HasValue &&
            MapCoordinateConverter.TryWorldToMap(
                data,
                territoryTypeId,
                rawWorldX.Value,
                rawWorldZ.Value,
                out var resolvedMapId,
                out var resolvedMapX,
                out var resolvedMapY))
        {
            mapX = resolvedMapX;
            mapY = resolvedMapY;
            mapId = resolvedMapId;
            worldX = rawWorldX;
            worldZ = rawWorldZ;
            return true;
        }

        // Older embedded CSV revisions can expose a bare coordinate pair. Treat it
        // as map-space only when it is already within the normal FFXIV map range.
        if (TryReadCoordinatePair(row, out var pairX, out var pairY) &&
            IsPlausibleMapPair(pairX, pairY))
        {
            mapX = pairX;
            mapY = pairY;
            return true;
        }

        var legacyX = ReadExactFloat(row, "X");
        var legacyY = ReadExactFloat(row, "Y");
        if (IsPlausibleMapPair(legacyX, legacyY))
        {
            mapX = legacyX;
            mapY = legacyY;
            return true;
        }

        return false;
    }

    private ushort? TryGetTerritoryMapSizeFactor(uint territoryTypeId)
    {
        try
        {
            var territory = data.GetExcelSheet<TerritoryType>().GetRow(territoryTypeId);
            var mapId = ReflectionUtil.GetNestedRowId(territory, "Map");
            if (mapId == 0)
                return null;

            var map = data.GetExcelSheet<Map>().GetRow(mapId);
            return map.SizeFactor;
        }
        catch
        {
            return null;
        }
    }

    private static bool NearlyEqual(float left, float right, float epsilon = 0.0005f)
        => MathF.Abs(left - right) <= epsilon;

    private static bool IsPlausibleMapPair(float? x, float? y) =>
        x.HasValue && y.HasValue &&
        x.Value >= 0f && x.Value <= 60f &&
        y.Value >= 0f && y.Value <= 60f;

    private static float? ReadExactFloat(
        IReadOnlyDictionary<string, string> row,
        string name)
    {
        return row.TryGetValue(name, out var raw) && TryParseFiniteFloat(raw, out var value)
            ? value
            : null;
    }

    private static float? ReadFloat(
        IReadOnlyDictionary<string, string> row,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetValue(name, out var raw) && TryParseFiniteFloat(raw, out var direct))
                return direct;

            var normalized = Normalize(name);
            foreach (var (key, rawValue) in row)
            {
                var normalizedKey = Normalize(key);
                if ((normalizedKey == normalized || normalizedKey.EndsWith(normalized, StringComparison.Ordinal)) &&
                    TryParseFiniteFloat(rawValue, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static bool TryReadRawMobSpawnPosition(
        IReadOnlyDictionary<string, string> row,
        out float x,
        out float y,
        out float? z)
    {
        x = 0f;
        y = 0f;
        z = null;

        // Newer typed models may flatten the vector into Position.X/Y/Z.
        var structuredX = ReadFloat(row, "Position.X", "PositionX", "PosX", "LocationX");
        var structuredY = ReadFloat(row, "Position.Y", "PositionY", "PosY", "LocationY");
        var structuredZ = ReadFloat(row, "Position.Z", "PositionZ", "PosZ", "LocationZ");
        if (structuredX.HasValue && structuredY.HasValue)
        {
            x = structuredX.Value;
            y = structuredY.Value;
            z = structuredZ;
            return true;
        }

        // LuminaSupplemental 5.x can expose Position as one serialized Vector3
        // value such as "21.48;21.48;-1". Parse all three components here.
        foreach (var (key, raw) in row)
        {
            var normalizedKey = Normalize(key);
            if (!normalizedKey.Contains("position", StringComparison.Ordinal) &&
                !normalizedKey.Contains("coordinate", StringComparison.Ordinal) &&
                !normalizedKey.Contains("coords", StringComparison.Ordinal) &&
                !normalizedKey.Contains("location", StringComparison.Ordinal))
            {
                continue;
            }

            var numbers = ExtractFiniteFloats(raw);
            if (numbers.Count < 2)
                continue;

            x = numbers[0];
            y = numbers[1];
            z = numbers.Count >= 3 ? numbers[2] : null;
            return true;
        }

        return false;
    }

    private static bool TryReadCoordinatePair(
        IReadOnlyDictionary<string, string> row,
        out float? x,
        out float? y)
    {
        x = null;
        y = null;

        foreach (var (key, raw) in row)
        {
            var normalizedKey = Normalize(key);
            if (!normalizedKey.Contains("position", StringComparison.Ordinal) &&
                !normalizedKey.Contains("coordinate", StringComparison.Ordinal) &&
                !normalizedKey.Contains("coords", StringComparison.Ordinal) &&
                !normalizedKey.Contains("location", StringComparison.Ordinal))
            {
                continue;
            }

            var numbers = ExtractFiniteFloats(raw);
            if (numbers.Count < 2)
                continue;

            if (IsMapCoordinate(numbers[0]) && IsMapCoordinate(numbers[1]))
            {
                x = numbers[0];
                y = numbers[1];
                return true;
            }
        }

        return false;
    }

    private static List<float> ExtractFiniteFloats(string raw)
    {
        var values = new List<float>();
        if (string.IsNullOrWhiteSpace(raw))
            return values;

        var token = new System.Text.StringBuilder();
        void Flush()
        {
            if (token.Length == 0)
                return;

            if (TryParseFiniteFloat(token.ToString(), out var value))
                values.Add(value);
            token.Clear();
        }

        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c is '-' or '+' or '.' or 'e' or 'E')
                token.Append(c);
            else
                Flush();
        }

        Flush();
        return values;
    }

    private static bool TryParseFiniteFloat(string raw, out float value)
    {
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsMapCoordinate(float value) => value >= 0f && value <= 60f;

    private static uint ReadMobSpawnLevel(IReadOnlyDictionary<string, string> row)
    {
        // LuminaSupplemental has changed model/property naming across releases,
        // and some loaders expose a level range instead of one scalar. Prefer
        // explicit monster-level fields first, then accept other fields whose
        // normalized name clearly describes a level. This intentionally avoids
        // generic numeric columns such as RowId, TerritoryType, X, or Y.
        var preferredKeys = new[]
        {
            "Level", "MobLevel", "BNpcLevel", "EnemyLevel",
            "MinLevel", "LevelMin", "MinimumLevel", "Levels", "LevelRange",
        };

        foreach (var key in preferredKeys)
        {
            if (row.TryGetValue(key, out var raw) && TryReadLowestLevel(raw, out var level))
                return level;
        }

        foreach (var (key, raw) in row)
        {
            var normalized = Normalize(key);
            if (!normalized.Contains("level", StringComparison.Ordinal) ||
                normalized.Contains("territorylevel", StringComparison.Ordinal) ||
                normalized.Contains("itemlevel", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryReadLowestLevel(raw, out var level))
                return level;
        }

        return 0;
    }

    private static bool TryReadLowestLevel(string? raw, out uint level)
    {
        level = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // Handles scalar values ("29") as well as common ranges/serialized
        // forms such as "25-31", "25,31", or "[25,31]". Monster levels are
        // small positive integers; choosing the lowest value matches CQG's
        // lowest-level-first farming presentation.
        var current = 0u;
        var inNumber = false;
        var values = new List<uint>();

        foreach (var c in raw)
        {
            if (char.IsDigit(c))
            {
                inNumber = true;
                current = current <= 9999 ? current * 10 + (uint)(c - '0') : current;
                continue;
            }

            if (!inNumber)
                continue;

            values.Add(current);
            current = 0;
            inNumber = false;
        }

        if (inNumber)
            values.Add(current);

        var candidates = values
            .Where(value => value > 0 && value <= 999)
            .ToList();

        if (candidates.Count == 0)
            return false;

        level = candidates.Min();
        return true;
    }

    private void AddMobSpawnLocationByBase(uint bnpcBaseId, MonsterSpawnLocation location)
    {
        if (!mobSpawnLocationsByBaseId.TryGetValue(bnpcBaseId, out var locations))
        {
            locations = [];
            mobSpawnLocationsByBaseId[bnpcBaseId] = locations;
        }

        var duplicateIndex = locations.FindIndex(existing =>
            existing.TerritoryTypeId == location.TerritoryTypeId &&
            Math.Abs(existing.X - location.X) < 0.1f &&
            Math.Abs(existing.Y - location.Y) < 0.1f);
        if (duplicateIndex >= 0)
        {
            if (location.Confidence < locations[duplicateIndex].Confidence)
                locations[duplicateIndex] = location;
            return;
        }

        locations.Add(location);
    }

    private void AddMobSpawnLocationByIdentity(
        (uint BaseId, uint NameId) identity,
        MonsterSpawnLocation location)
    {
        if (identity.BaseId == 0 || identity.NameId == 0)
            return;

        if (!mobSpawnLocationsByIdentity.TryGetValue(identity, out var locations))
        {
            locations = [];
            mobSpawnLocationsByIdentity[identity] = locations;
        }

        var duplicateIndex = locations.FindIndex(existing =>
            existing.TerritoryTypeId == location.TerritoryTypeId &&
            Math.Abs(existing.X - location.X) < 0.1f &&
            Math.Abs(existing.Y - location.Y) < 0.1f);
        if (duplicateIndex >= 0)
        {
            if (location.Confidence < locations[duplicateIndex].Confidence)
                locations[duplicateIndex] = location;
            return;
        }

        locations.Add(location);
    }

    private void AddMobLevelByIdentity((uint BaseId, uint NameId) identity, uint level)
    {
        if (identity.BaseId == 0 || identity.NameId == 0 || level == 0)
            return;

        if (!mobLevelsByIdentity.TryGetValue(identity, out var levels))
        {
            levels = [];
            mobLevelsByIdentity[identity] = levels;
        }

        levels.Add(level);
    }

    private void AddMobLevelByBase(uint bnpcBaseId, uint level)
    {
        if (bnpcBaseId == 0 || level == 0)
            return;

        if (!mobLevelsByBaseId.TryGetValue(bnpcBaseId, out var levels))
        {
            levels = [];
            mobLevelsByBaseId[bnpcBaseId] = levels;
        }

        levels.Add(level);
    }

    private void AddMobLevel(uint bnpcNameId, uint level)
    {
        if (bnpcNameId == 0 || level == 0)
            return;

        if (!mobLevelsByNameId.TryGetValue(bnpcNameId, out var levels))
        {
            levels = [];
            mobLevelsByNameId[bnpcNameId] = levels;
        }

        levels.Add(level);
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

        // Prefer LuminaSupplemental's documented typed CsvLoader. Besides being
        // the supported package API, the typed model preserves structured fields
        // (for example MobSpawn position data) that can be lost or misinterpreted
        // when an embedded CSV is treated as an arbitrary flat table.
        var viaLoader = ReadViaPackageCsvLoader(datasetName);
        if (viaLoader.Count > 0)
        {
            datasetCache[datasetName] = viaLoader;
            return viaLoader;
        }

        // Keep direct resource parsing as a compatibility fallback for package
        // revisions where CsvLoader cannot be invoked reflectively.
        var direct = ReadEmbeddedCsv(datasetName);
        datasetCache[datasetName] = direct;
        return direct;
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
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        FlattenObject(result, obj, prefix: string.Empty, depth: 0, visited);
        return result;
    }

    /// <summary>
    /// Flattens a supplemental model into searchable scalar facts while retaining
    /// the full member path (for example Position.X). Supplemental package models
    /// have evolved from flat CSV-shaped records to richer typed/nested values;
    /// limiting reflection to top-level ToString() values silently hid useful
    /// fields such as MobSpawn coordinates.
    /// </summary>
    private static void FlattenObject(
        IDictionary<string, string> output,
        object value,
        string prefix,
        int depth,
        ISet<object> visited)
    {
        const int MaxDepth = 4;
        if (depth > MaxDepth)
            return;

        var type = value.GetType();
        if (IsScalarValue(type, value))
        {
            if (!string.IsNullOrWhiteSpace(prefix))
                output[prefix] = FlattenValue(value);
            return;
        }

        // Lumina row-reference wrappers should remain IDs. Recursing into them
        // can force sheet resolution and also produces a large, irrelevant graph.
        var rowId = ReflectionUtil.ExtractRowId(value);
        if (rowId != 0 && !string.IsNullOrWhiteSpace(prefix))
        {
            output[prefix] = rowId.ToString(CultureInfo.InvariantCulture);
            return;
        }

        // Guard only reference types; boxed value types can legitimately repeat.
        if (!type.IsValueType && !visited.Add(value))
            return;

        if (value is IEnumerable enumerable && value is not string)
        {
            var index = 0;
            foreach (var entry in enumerable)
            {
                if (entry is null || index >= 16)
                    break;

                var childPrefix = string.IsNullOrWhiteSpace(prefix)
                    ? $"Item{index}"
                    : $"{prefix}.Item{index}";
                FlattenObject(output, entry, childPrefix, depth + 1, visited);
                index++;
            }

            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (child is null)
                continue;

            var childPrefix = string.IsNullOrWhiteSpace(prefix)
                ? property.Name
                : $"{prefix}.{property.Name}";
            FlattenObject(output, child, childPrefix, depth + 1, visited);
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            object? child;
            try
            {
                child = field.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (child is null)
                continue;

            var childPrefix = string.IsNullOrWhiteSpace(prefix)
                ? field.Name
                : $"{prefix}.{field.Name}";
            FlattenObject(output, child, childPrefix, depth + 1, visited);
        }

        // Some lightweight framework structs expose no public children but have
        // a useful invariant ToString representation. Preserve it as a fallback.
        if (!string.IsNullOrWhiteSpace(prefix) && !output.ContainsKey(prefix))
        {
            var flattened = FlattenValue(value);
            if (!string.IsNullOrWhiteSpace(flattened))
                output[prefix] = flattened;
        }
    }

    private static bool IsScalarValue(Type type, object value) =>
        value is string ||
        type.IsPrimitive ||
        type.IsEnum ||
        value is decimal ||
        value is DateTime ||
        value is DateTimeOffset ||
        value is Guid;

    private static string FlattenValue(object value)
    {
        if (value is string text)
            return text;

        if (value.GetType().IsPrimitive || value is decimal || value is Enum)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        var rowId = ReflectionUtil.ExtractRowId(value);
        if (rowId != 0)
            return rowId.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>
    /// Reads a critical relationship ID by exact field/property identity.
    ///
    /// Supplemental typed models are flattened using dotted member paths. For
    /// identity joins (BNpcName, BNpcBase, TerritoryType, etc.) a substring
    /// search is unsafe because unrelated nested members can contain the same
    /// text. Prefer an exact key, then an exact leaf member name.
    /// </summary>
    public static uint FirstIdNamed(
        IReadOnlyDictionary<string, string> row,
        params string[] names)
    {
        if (row.Count == 0 || names.Length == 0)
            return 0;

        var normalizedNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

        // Exact key/property match always wins. This is the normal shape for
        // both the embedded CSV and CsvLoader row-reference properties.
        foreach (var (key, raw) in row)
        {
            if (!normalizedNames.Contains(Normalize(key)))
                continue;

            if (uint.TryParse(raw, out var id) && id != 0)
                return id;
        }

        // Typed-model flattening can expose a scalar as Parent.BNpcName. Match
        // only the final member name; never an arbitrary substring of the path.
        foreach (var (key, raw) in row)
        {
            var separator = key.LastIndexOf('.');
            var leaf = separator >= 0 ? key[(separator + 1)..] : key;
            if (!normalizedNames.Contains(Normalize(leaf)))
                continue;

            if (uint.TryParse(raw, out var id) && id != 0)
                return id;
        }

        return 0;
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
