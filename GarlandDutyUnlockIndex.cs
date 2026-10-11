using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace CustomQuestGuide;

/// <summary>
/// Sparse, on-demand fallback for duty -> unlock quest relationships that are
/// hidden in quest scripts rather than exposed by the static game sheets.
/// Garland Tools parses those script instructions and publishes the resulting
/// instance.unlockedByQuest relationship. CQG only queries this fallback for a
/// duty whose local/static resolver could not already identify an unlock quest.
/// Results and confirmed misses are cached locally to avoid repeated requests.
/// </summary>
public sealed class GarlandDutyUnlockIndex : IDisposable
{
    private const string CacheFileName = "garland-duty-unlock-cache.json";
    private const int CacheSchemaVersion = 1;

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly IPluginLog log;
    private readonly NetworkRequestTracker networkRequests;
    private readonly string cachePath;
    private readonly ConcurrentDictionary<uint, uint> questIdByInstanceContent = new();
    private readonly ConcurrentDictionary<uint, byte> inFlight = new();
    private readonly ConcurrentDictionary<uint, DateTime> retryAfterUtc = new();
    private readonly object requestPaceGate = new();
    private DateTime nextRequestUtc = DateTime.MinValue;
    private readonly CancellationTokenSource disposeCts = new();
    private readonly object saveGate = new();

    public bool CacheFilePresent => File.Exists(cachePath);
    public int CachedEntryCount => questIdByInstanceContent.Count;
    public int CachedResolvedCount => questIdByInstanceContent.Count(pair => pair.Value != 0);
    public int CachedMissCount => questIdByInstanceContent.Count(pair => pair.Value == 0);
    public DateTime CacheFileWrittenUtc => File.Exists(cachePath) ? File.GetLastWriteTimeUtc(cachePath) : DateTime.MinValue;

    public GarlandDutyUnlockIndex(string configDirectory, IPluginLog log, NetworkRequestTracker networkRequests)
    {
        this.log = log;
        this.networkRequests = networkRequests;
        cachePath = Path.Combine(configDirectory, CacheFileName);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CustomQuestGuide/1.1.13");
        LoadCache();
    }

    /// <summary>
    /// Returns a positive cached quest ID. A cached value of 0 represents a
    /// confirmed miss and intentionally returns null.
    /// </summary>
    public uint? GetCachedQuestId(uint instanceContentId)
    {
        if (instanceContentId == 0 ||
            !questIdByInstanceContent.TryGetValue(instanceContentId, out var questId) ||
            questId == 0)
        {
            return null;
        }

        return questId;
    }

    public bool IsChecked(uint instanceContentId) =>
        instanceContentId != 0 && questIdByInstanceContent.ContainsKey(instanceContentId);

    public bool IsResolving(uint instanceContentId) =>
        instanceContentId != 0 && inFlight.ContainsKey(instanceContentId);

    /// <summary>
    /// Starts a single background request if this instance has not already been
    /// cached or requested. The caller never waits on network I/O.
    /// </summary>
    public void EnsureRequested(uint instanceContentId)
    {
        if (instanceContentId == 0 || questIdByInstanceContent.ContainsKey(instanceContentId))
            return;

        if (retryAfterUtc.TryGetValue(instanceContentId, out var retryAfter) && DateTime.UtcNow < retryAfter)
        {
            networkRequests.Suppressed("Garland duty");
            return;
        }

        lock (requestPaceGate)
        {
            if (inFlight.Count > 0 || DateTime.UtcNow < nextRequestUtc)
            {
                networkRequests.Suppressed("Garland duty");
                return;
            }

            if (!inFlight.TryAdd(instanceContentId, 0))
                return;

            nextRequestUtc = DateTime.UtcNow.AddSeconds(2);
        }

        _ = ResolveAsync(instanceContentId);
    }

    private async Task ResolveAsync(uint instanceContentId)
    {
        try
        {
            var url = $"https://www.garlandtools.org/db/doc/instance/en/3/{instanceContentId}.json";
            networkRequests.Request("Garland duty");
            using var response = await http.GetAsync(url, disposeCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                networkRequests.Failure("Garland duty");
                // 404 is a stable miss for this Garland document ID. Other
                // failures may be temporary and should remain retryable.
                if ((int)response.StatusCode == 404)
                {
                    questIdByInstanceContent[instanceContentId] = 0;
                    SaveCache();
                }
                else
                {
                    retryAfterUtc[instanceContentId] = DateTime.UtcNow.AddMinutes(
                        (int)response.StatusCode == 429 ? 30 : 5);
                }

                return;
            }
            networkRequests.Success("Garland duty");
            retryAfterUtc.TryRemove(instanceContentId, out _);

            await using var stream = await response.Content.ReadAsStreamAsync(disposeCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: disposeCts.Token);

            uint questId = 0;
            if (document.RootElement.TryGetProperty("instance", out var instance) &&
                instance.ValueKind == JsonValueKind.Object &&
                instance.TryGetProperty("unlockedByQuest", out var unlockedByQuest))
            {
                questId = ReadUInt(unlockedByQuest);
            }

            // A successful instance document without unlockedByQuest is a
            // confirmed miss. Cache 0 so normal UI redraws do not re-request it.
            questIdByInstanceContent[instanceContentId] = questId;
            SaveCache();
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            // Normal plugin unload.
        }
        catch (Exception ex)
        {
            // Transient failures are deliberately not cached, but use a short
            // backoff so repeated UI selections cannot hammer the endpoint.
            retryAfterUtc[instanceContentId] = DateTime.UtcNow.AddMinutes(5);
            networkRequests.Failure("Garland duty");
            log.Debug(ex, $"Garland duty unlock lookup failed for InstanceContent {instanceContentId}.");
        }
        finally
        {
            inFlight.TryRemove(instanceContentId, out _);
        }
    }

    private static uint ReadUInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var numeric))
            return numeric;

        if (value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out numeric))
            return numeric;

        return 0;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var parsedSchema) ||
                parsedSchema != CacheSchemaVersion)
            {
                return;
            }

            if (!document.RootElement.TryGetProperty("instances", out var instances) ||
                instances.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in instances.EnumerateObject())
            {
                if (!uint.TryParse(property.Name, out var instanceId) || instanceId == 0)
                    continue;

                var questId = ReadUInt(property.Value);
                questIdByInstanceContent[instanceId] = questId;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not load CQG Garland duty-unlock cache.");
        }
    }

    private void SaveCache()
    {
        try
        {
            lock (saveGate)
            {
                var instances = questIdByInstanceContent
                    .OrderBy(pair => pair.Key)
                    .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);

                var payload = new
                {
                    schemaVersion = CacheSchemaVersion,
                    instances,
                };

                File.WriteAllText(
                    cachePath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not save CQG Garland duty-unlock cache.");
        }
    }

    public bool ClearCache()
    {
        if (inFlight.Count > 0)
            return false;

        questIdByInstanceContent.Clear();
        retryAfterUtc.Clear();
        nextRequestUtc = DateTime.MinValue;

        try
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not delete CQG Garland duty-unlock cache.");
        }

        return true;
    }

    public void Dispose()
    {
        disposeCts.Cancel();
        http.Dispose();
        disposeCts.Dispose();
    }
}
