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
/// On-demand community-data fallback for duty -> unlock quest relationships.
///
/// FFXIV Todo publishes an MIT-licensed generated content index which includes
/// quest IDs for optional duties whose unlock relationship is often hidden in
/// quest scripts instead of the static game sheets. CQG downloads that index at
/// most once when a locally-unresolved duty is encountered, reduces it to the
/// small name -> quest ID mapping it needs, and persists that reduced cache in
/// the plugin config directory. Subsequent lookups are local.
/// </summary>
public sealed class CommunityDutyUnlockIndex : IDisposable
{
    private const string SourceUrl =
        "https://raw.githubusercontent.com/datamachine/ffxiv-todo/main/FfxivTodo/Data/content.json";
    private const string CacheFileName = "community-duty-unlock-cache.json";
    private const int CacheSchemaVersion = 1;
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly IPluginLog log;
    private readonly NetworkRequestTracker networkRequests;
    private readonly string cachePath;
    private readonly ConcurrentDictionary<string, uint> questIdByDuty =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource disposeCts = new();
    private readonly object saveGate = new();

    private volatile bool loadAttempted;
    private volatile bool isLoading;
    private DateTime cacheGeneratedUtc = DateTime.MinValue;

    public CommunityDutyUnlockIndex(string configDirectory, IPluginLog log, NetworkRequestTracker networkRequests)
    {
        this.log = log;
        this.networkRequests = networkRequests;
        cachePath = Path.Combine(configDirectory, CacheFileName);
        LoadCache();
    }

    public uint? GetCachedQuestId(string dutyName)
    {
        dutyName = Normalize(dutyName);
        if (string.IsNullOrWhiteSpace(dutyName) ||
            !questIdByDuty.TryGetValue(dutyName, out var questId) ||
            questId == 0)
        {
            return null;
        }

        return questId;
    }

    public bool IsLoading => isLoading;
    public bool CacheFilePresent => File.Exists(cachePath);
    public int CachedEntryCount => questIdByDuty.Count;
    public DateTime CacheGeneratedUtc => cacheGeneratedUtc;
    public bool CacheIsFresh => questIdByDuty.Count > 0 &&
                                cacheGeneratedUtc != DateTime.MinValue &&
                                DateTime.UtcNow - cacheGeneratedUtc <= CacheMaxAge;

    /// <summary>
    /// Ensures the community index is available without blocking the UI. A
    /// fresh persisted cache is used without any HTTP request. A stale/missing
    /// cache triggers one background request for the whole index, not one
    /// request per duty.
    /// </summary>
    public void EnsureLoaded()
    {
        if (isLoading)
            return;

        var cacheFresh = questIdByDuty.Count > 0 &&
                         cacheGeneratedUtc != DateTime.MinValue &&
                         DateTime.UtcNow - cacheGeneratedUtc <= CacheMaxAge;
        if (cacheFresh || loadAttempted)
            return;

        loadAttempted = true;
        isLoading = true;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
            request.Headers.UserAgent.ParseAdd("CustomQuestGuide/1.1.13");
            networkRequests.Request("Community duty index");
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                disposeCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                networkRequests.Failure("Community duty index");
                log.Debug($"CQG community duty fallback HTTP {(int)response.StatusCode}.");
                return;
            }
            networkRequests.Success("Community duty index");

            await using var stream = await response.Content.ReadAsStreamAsync(disposeCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: disposeCts.Token);

            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var resolved = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (!TryReadString(item, "name", out var name) ||
                    !TryReadString(item, "category", out var category) ||
                    !IsDutyCategory(category) ||
                    !TryReadUInt(item, "questId", out var questId) ||
                    questId == 0)
                {
                    continue;
                }

                name = Normalize(name);
                if (!string.IsNullOrWhiteSpace(name))
                    resolved.TryAdd(name, questId);
            }

            if (resolved.Count == 0)
                return;

            questIdByDuty.Clear();
            foreach (var pair in resolved)
                questIdByDuty[pair.Key] = pair.Value;

            cacheGeneratedUtc = DateTime.UtcNow;
            SaveCache();
            log.Debug($"CQG community duty fallback loaded {questIdByDuty.Count:N0} duty unlock routes.");
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
            // Normal plugin unload.
        }
        catch (Exception ex)
        {
            // Keep any previously loaded stale cache usable. A failed network
            // refresh must never make duty guidance worse than it already was.
            log.Debug(ex, "CQG community duty fallback refresh failed.");
        }
        finally
        {
            isLoading = false;
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var parsedSchema) ||
                parsedSchema != CacheSchemaVersion)
            {
                return;
            }

            if (root.TryGetProperty("generatedUtc", out var generated) &&
                generated.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(generated.GetString(), out var parsedDate))
            {
                cacheGeneratedUtc = parsedDate.ToUniversalTime();
            }

            if (!root.TryGetProperty("duties", out var duties) || duties.ValueKind != JsonValueKind.Object)
                return;

            foreach (var property in duties.EnumerateObject())
            {
                if (ReadUInt(property.Value) is not uint questId || questId == 0)
                    continue;

                var name = Normalize(property.Name);
                if (!string.IsNullOrWhiteSpace(name))
                    questIdByDuty[name] = questId;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not load CQG community duty-unlock cache.");
        }
    }

    private void SaveCache()
    {
        try
        {
            lock (saveGate)
            {
                var duties = questIdByDuty
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

                var payload = new
                {
                    schemaVersion = CacheSchemaVersion,
                    generatedUtc = cacheGeneratedUtc,
                    source = "FFXIV Todo (MIT)",
                    duties,
                };

                File.WriteAllText(
                    cachePath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not save CQG community duty-unlock cache.");
        }
    }

    private static bool IsDutyCategory(string category)
    {
        // Keep this permissive enough for the source's category naming while
        // excluding ordinary quests/features that may coincidentally share a
        // duty name.
        return category.Contains("Dungeon", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("Trial", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("Raid", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("Criterion", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("Variant", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("Chaotic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var child) || child.ValueKind != JsonValueKind.String)
            return false;

        value = child.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryReadUInt(JsonElement element, string property, out uint value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var child))
            return false;

        var parsed = ReadUInt(child);
        if (parsed is not uint result)
            return false;

        value = result;
        return true;
    }

    private static uint? ReadUInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var numeric))
            return numeric;
        if (value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out numeric))
            return numeric;
        return null;
    }

    private static string Normalize(string value) =>
        (value ?? string.Empty).Trim().Trim(' ', '—', '-');

    public bool ClearCache()
    {
        if (isLoading)
            return false;

        questIdByDuty.Clear();
        loadAttempted = false;
        cacheGeneratedUtc = DateTime.MinValue;

        try
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not delete CQG community duty cache.");
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
