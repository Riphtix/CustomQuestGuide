using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace CustomQuestGuide;

/// <summary>Validated, ordered progression stages. Variant applicability is decided
/// using the character's original class, never quest names or guessed graph edges.</summary>
internal sealed class VerifiedJobQuestCatalog
{
    private sealed record Stage(uint[] Always, uint[] Starting, uint[] Later);
    private readonly Dictionary<uint, Stage[]> verified = new();

    internal VerifiedJobQuestCatalog(string json, IReadOnlySet<uint> knownQuestIds, IPluginLog log)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("schemaVersion").GetInt32() != 2)
                throw new InvalidOperationException("Unsupported job quest catalog schema.");

            foreach (var entry in doc.RootElement.GetProperty("jobs").EnumerateArray())
            {
                var job = entry.GetProperty("classJobId").GetUInt32();
                var stages = new List<Stage>();
                foreach (var stage in entry.GetProperty("stages").EnumerateArray())
                {
                    uint[] Read(string key) => stage.TryGetProperty(key, out var value)
                        ? value.EnumerateArray().Select(e => e.GetUInt32()).ToArray()
                        : Array.Empty<uint>();
                    stages.Add(new Stage(Read("always"), Read("startingClass"), Read("laterClass")));
                }
                var ids = stages.SelectMany(s => s.Always.Concat(s.Starting).Concat(s.Later)).ToArray();
                bool validStages = stages.Count > 0 && stages.All(s =>
                    (s.Always.Length == 1 && s.Starting.Length == 0 && s.Later.Length == 0) ||
                    (s.Always.Length == 0 && s.Starting.Length <= 1 && s.Later.Length <= 1 &&
                     s.Starting.Length + s.Later.Length >= 1));
                if (job == 0 || !entry.GetProperty("verifiedComplete").GetBoolean() ||
                    !validStages || ids.Any(id => id == 0 || !knownQuestIds.Contains(id)) ||
                    ids.Distinct().Count() != ids.Length || !verified.TryAdd(job, stages.ToArray()))
                    log.Warning("Rejecting incomplete or invalid quest catalog entry for job {Id}.", job);
            }
        }
        catch (Exception ex)
        {
            verified.Clear();
            log.Warning(ex, "Verified quest catalog unavailable.");
        }
    }

    internal bool TryGetAll(uint classJobId, out uint[] questIds)
    {
        if (verified.TryGetValue(classJobId, out var stages))
        {
            questIds = stages.SelectMany(s => s.Always.Concat(s.Starting).Concat(s.Later)).ToArray();
            return true;
        }
        questIds = Array.Empty<uint>();
        return false;
    }

    internal bool TryGetApplicable(uint classJobId, uint firstClassId, out uint[] questIds)
    {
        if (firstClassId != 0 && verified.TryGetValue(classJobId, out var stages))
        {
            questIds = stages.SelectMany(s => s.Always.Concat(
                firstClassId == classJobId ? s.Starting : s.Later)).ToArray();
            return questIds.Length > 0;
        }
        questIds = Array.Empty<uint>();
        return false;
    }
}
