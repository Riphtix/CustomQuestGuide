using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace CustomQuestGuide;

/// <summary>
/// Persistent global cache for monster metadata.
///
/// Monster facts belong to the monster, not to any item that happens to drop
/// from it. Item/source discovery therefore stores only the monster/provider
/// identity; level/location enrichment is looked up here first and is shared by
/// every target that references the same monster.
/// </summary>
internal sealed class MonsterMetadataCache
{
    private const string FileName = "monster-cache.json";
    private const int SchemaVersion = 2;

    private readonly string path;
    private readonly IPluginLog log;
    private readonly object ioGate = new();
    private readonly ConcurrentDictionary<ulong, Entry> entries = new();

    internal sealed record Entry(
        uint? Level,
        MonsterSpawnLocation? Location,
        IReadOnlyList<string> FateNames,
        bool Checked,
        string? ValidatedGameDataFingerprint);

    public MonsterMetadataCache(string configDirectory, IPluginLog log)
    {
        path = Path.Combine(configDirectory, FileName);
        this.log = log;
        Load();
    }

    public int Count => entries.Count;
    public bool FilePresent => File.Exists(path);

    public bool TryGet(ulong monsterId, out Entry entry)
    {
        if (monsterId != 0 && entries.TryGetValue(monsterId, out var found))
        {
            entry = found;
            return true;
        }

        entry = default!;
        return false;
    }

    public bool TryGetLevel(ulong monsterId, out uint level)
    {
        level = 0;
        return TryGet(monsterId, out var entry) &&
               entry.Level is uint cached && cached != 0 &&
               (level = cached) != 0;
    }

    public bool TryGetLocation(ulong monsterId, out MonsterSpawnLocation location)
    {
        location = default!;
        if (!TryGet(monsterId, out var entry) || entry.Location is null)
            return false;

        location = entry.Location;
        return true;
    }

    // FateNames remains in the persisted schema only so older cache files can be
    // read without migration. Runtime FATE context is item-scoped and deliberately
    // never sourced from this global monster cache.
    public bool IsValidated(ulong monsterId, string currentGameDataFingerprint) =>
        TryGet(monsterId, out var entry) &&
        entry.Checked &&
        !string.IsNullOrWhiteSpace(entry.ValidatedGameDataFingerprint) &&
        string.Equals(
            entry.ValidatedGameDataFingerprint,
            currentGameDataFingerprint,
            StringComparison.Ordinal);

    public void PutLevelHint(ulong monsterId, uint level)
    {
        if (monsterId == 0 || level == 0)
            return;

        entries.AddOrUpdate(
            monsterId,
            _ => new Entry(level, null, Array.Empty<string>(), Checked: false, ValidatedGameDataFingerprint: null),
            (_, current) => current.Level is > 0
                ? current
                : current with { Level = level });
    }

    /// <summary>
    /// Commits one successful monster lookup and persists it immediately. A
    /// successful provider response is cached even when no overworld coordinate
    /// exists, so duty-only monsters do not get queried forever.
    /// </summary>
    public void PutValidated(
        ulong monsterId,
        uint? level,
        MonsterSpawnLocation? location,
        IReadOnlyList<string>? fateNames,
        string currentGameDataFingerprint)
    {
        if (monsterId == 0)
            return;

        entries[monsterId] = new Entry(
            level is > 0 ? level : null,
            location,
            (fateNames ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Checked: true,
            ValidatedGameDataFingerprint: currentGameDataFingerprint);

        Save();
    }

    /// <summary>
    /// Imports legacy monster metadata from the old combined Garland cache.
    /// This is migration-only; the new runtime source of truth is monster-cache.json.
    /// </summary>
    public void ImportLegacy(
        ulong monsterId,
        uint? level,
        MonsterSpawnLocation? location,
        bool wasChecked,
        string? validatedFingerprint)
    {
        if (monsterId == 0 || entries.ContainsKey(monsterId))
            return;

        if (level is null && location is null && !wasChecked)
            return;

        entries[monsterId] = new Entry(
            level is > 0 ? level : null,
            location,
            Array.Empty<string>(),
            wasChecked,
            validatedFingerprint);
    }

    public void SaveImportedLegacyData() => Save();

    public void Clear()
    {
        lock (ioGate)
        {
            entries.Clear();
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not delete CQG monster metadata cache.");
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var parsedSchema) ||
                (parsedSchema != 1 && parsedSchema != SchemaVersion) ||
                !root.TryGetProperty("monsters", out var monsters) ||
                monsters.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in monsters.EnumerateObject())
            {
                if (!ulong.TryParse(property.Name, out var monsterId) || monsterId == 0 ||
                    property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var value = property.Value;
                uint? level = null;
                if (value.TryGetProperty("level", out var levelElement) &&
                    levelElement.ValueKind == JsonValueKind.Number &&
                    levelElement.TryGetUInt32(out var parsedLevel) && parsedLevel != 0)
                {
                    level = parsedLevel;
                }

                MonsterSpawnLocation? location = null;
                if (value.TryGetProperty("location", out var locationElement) &&
                    locationElement.ValueKind == JsonValueKind.Object)
                {
                    var zoneName = locationElement.TryGetProperty("zoneName", out var zoneElement) &&
                                   zoneElement.ValueKind == JsonValueKind.String
                        ? zoneElement.GetString()
                        : null;

                    if (!string.IsNullOrWhiteSpace(zoneName) &&
                        locationElement.TryGetProperty("territoryTypeId", out var territoryElement) &&
                        territoryElement.TryGetUInt32(out var territoryTypeId) &&
                        TryReadFloat(locationElement, "x", out var x) &&
                        TryReadFloat(locationElement, "y", out var y))
                    {
                        location = new MonsterSpawnLocation(
                            territoryTypeId,
                            zoneName!,
                            x,
                            y,
                            IsFallback: true);
                    }
                }

                var fateNames = value.TryGetProperty("fateNames", out var fateNamesElement) &&
                                fateNamesElement.ValueKind == JsonValueKind.Array
                    ? fateNamesElement.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();

                var wasChecked = value.TryGetProperty("checked", out var checkedElement) &&
                                 checkedElement.ValueKind == JsonValueKind.True;
                var fingerprint = value.TryGetProperty("validatedGameDataFingerprint", out var fpElement) &&
                                  fpElement.ValueKind == JsonValueKind.String
                    ? fpElement.GetString()
                    : null;

                // Schema 1 predates FATE relationship metadata. Preserve its
                // useful level/location hints, but force a one-time provider
                // revalidation when that monster is next needed so schema 2 can
                // learn static FATE associations without a blanket cache wipe.
                if (parsedSchema == 1)
                {
                    wasChecked = false;
                    fingerprint = null;
                }

                entries[monsterId] = new Entry(level, location, fateNames, wasChecked, fingerprint);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not load CQG monster metadata cache.");
        }
    }

    private void Save()
    {
        lock (ioGate)
        {
            try
            {
                var monsters = entries
                    .OrderBy(pair => pair.Key)
                    .ToDictionary(
                        pair => pair.Key.ToString(),
                        pair => new
                        {
                            level = pair.Value.Level,
                            location = pair.Value.Location is null
                                ? null
                                : new
                                {
                                    territoryTypeId = pair.Value.Location.TerritoryTypeId,
                                    zoneName = pair.Value.Location.ZoneName,
                                    x = pair.Value.Location.X,
                                    y = pair.Value.Location.Y,
                                },
                            fateNames = pair.Value.FateNames,
                            @checked = pair.Value.Checked,
                            validatedGameDataFingerprint = pair.Value.ValidatedGameDataFingerprint,
                        });

                var payload = new
                {
                    schemaVersion = SchemaVersion,
                    writtenUtc = DateTime.UtcNow.ToString("O"),
                    monsters,
                };

                var tempPath = path + ".tmp";
                File.WriteAllText(
                    tempPath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not save CQG monster metadata cache.");
            }
        }
    }

    private static bool TryReadFloat(JsonElement element, string property, out float value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var token))
            return false;

        if (token.ValueKind == JsonValueKind.Number)
            return token.TryGetSingle(out value);

        return token.ValueKind == JsonValueKind.String &&
               float.TryParse(
                   token.GetString(),
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value);
    }
}
