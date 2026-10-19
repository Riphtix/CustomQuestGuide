using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CustomQuestGuide;

// ===== DutyUnlockGuidanceIndex.cs =====
public sealed record DutyUnlockAdvice(
    string DutyName,
    bool? IsUnlocked,
    string QuestName,
    string? Contact,
    string? Location,
    string? Note = null);

/// <summary>
/// Shared duty/content unlock guidance. The live ContentFinderCondition ->
/// InstanceContent relation provides unlock state for every supported duty.
/// Where the Quest sheet exposes a reverse content-unlock reference, it is
/// indexed generically; a small curated table remains only for historical rows
/// whose reverse relation is not dependable.
/// </summary>
public sealed class DutyUnlockGuidanceIndex
{
    private readonly QuestGuidanceIndex quests;
    private readonly IPluginLog log;
    private readonly CommunityDutyUnlockIndex communityDutyUnlocks;
    private readonly GarlandDutyUnlockIndex garlandDutyUnlocks;

    private readonly Dictionary<string, uint> instanceContentByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> dutyNameByInstanceContentId = [];
    private readonly Dictionary<uint, string> dutyNameByContentFinderId = [];
    private readonly Dictionary<string, uint> contentFinderIdByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> inferredQuestByDuty =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint[]> directUnlockQuestIdsByDuty =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool?> unlockSnapshot =
        new(StringComparer.OrdinalIgnoreCase);
    private string[] unlockPollingNames = Array.Empty<string>();
    private int unlockPollingCursor;
    public int PollingCandidateCount => unlockPollingNames.Length;
    public int PollingCursor => unlockPollingCursor;
    public bool PollingComplete => unlockPollingCursor >= unlockPollingNames.Length;

    private sealed record CuratedDutyUnlockRoute(
        string QuestName,
        string? Contact = null,
        string? Location = null,
        string? Note = null);

    // Some duties are unlocked by quest script, feature state, or progression
    // gates rather than a dependable Quest-sheet -> ContentFinderCondition
    // reference. Keep these as small evidence-backed overrides while the live
    // sheet reverse index remains the default for ordinary duties.
    private static readonly Dictionary<string, CuratedDutyUnlockRoute> CuratedUnlockRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Aloalo Island"] = new(
                "Stranger from Paradise",
                "Shallow Moor",
                "Old Sharlayan (X:12.0, Y:13.3)"),
            ["Aglaia"] = new(
                "The Realm of the Gods",
                "G'raha Tia",
                "The Omphalos (X:6.2, Y:6.3)"),
            ["Advanced Mahjong (Full Ranked Match)"] = new(
                "Every Little Thing She Does Is Mahjong",
                "Lewena",
                "The Gold Saucer (X:4.8, Y:6.1)",
                "Doman Mahjong must be unlocked first. Advanced ranked matchmaking additionally requires 1st dan or higher."),
            ["Advanced Mahjong (Quick Ranked Match)"] = new(
                "Every Little Thing She Does Is Mahjong",
                "Lewena",
                "The Gold Saucer (X:4.8, Y:6.1)",
                "Doman Mahjong must be unlocked first. Advanced ranked matchmaking additionally requires 1st dan or higher."),
            ["A Relic Reborn: The Hydra"] = new(
                "A Relic Reborn",
                "Gerolt",
                "North Shroud (X:30.2, Y:20.2)",
                "The Hydra is unlocked as part of your job's A Relic Reborn weapon quest; progress that relic quest to the Hydra step."),
        };

    private static readonly Dictionary<string, string> QuestByDuty =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // A Realm Reborn
            ["The Howling Eye (Extreme)"] = "Gale-force Warning",
            ["The Navel (Extreme)"] = "Quake Me Up Before You O'Ghomoro",
            ["The Bowl of Embers (Extreme)"] = "Ifrit Ain't Broke",
            ["Thornmarch (Extreme)"] = "The King Lives",
            ["The Whorleater (Extreme)"] = "Whorl of a Time",
            ["The Akh Afah Amphitheatre (Extreme)"] = "Drop Dead Shiva",

            // Heavensward
            ["The Limitless Blue (Extreme)"] = "The Diabolical Bismarck",
            ["Thok ast Thok (Extreme)"] = "Thok Around the Clock",
            ["The Minstrel's Ballad: Thordan's Reign"] = "Thordan's Reign",
            ["The Minstrel's Ballad: Nidhogg's Rage"] = "Nidhogg's Rage",
            ["Containment Bay S1T7 (Extreme)"] = "A Fiendish Likeness",
            ["Containment Bay P1T6 (Extreme)"] = "A Deific Simulacrum",
            ["Containment Bay Z1T9 (Extreme)"] = "A Demonic Duplicate",

            // Shadowbringers Sorrow of Werlyt exceptions
            ["Cinder Drift (Extreme)"] = "Weapon of Choice",
            ["Castrum Marinum (Extreme)"] = "Weapon of Choice",
            ["The Cloud Deck (Extreme)"] = "Weapon of Choice",
        };

    public DutyUnlockGuidanceIndex(
        IDataManager data,
        QuestGuidanceIndex quests,
        CommunityDutyUnlockIndex communityDutyUnlocks,
        GarlandDutyUnlockIndex garlandDutyUnlocks,
        IPluginLog log)
    {
        this.quests = quests;
        this.communityDutyUnlocks = communityDutyUnlocks;
        this.garlandDutyUnlocks = garlandDutyUnlocks;
        this.log = log;
        BuildContentIndex(data);
        BuildQuestUnlockIndex(data);
    }

    public void BeginPolling()
    {
        unlockPollingNames = instanceContentByName.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        unlockPollingCursor = 0;
    }

    /// <summary>
    /// Moves still-unchecked duties to the front of the remaining one-shot scan.
    /// Duties already checked during this pass are never queried again.
    /// </summary>
    public int PromoteDuties(IEnumerable<string> dutyNames)
    {
        if (PollingComplete || unlockPollingNames.Length == 0)
            return 0;

        var requested = dutyNames
            .Select(NormalizeDutyName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
            return 0;

        var prefix = unlockPollingNames.Take(unlockPollingCursor);
        var remaining = unlockPollingNames.Skip(unlockPollingCursor).ToArray();
        var promoted = remaining.Where(requested.Contains).ToArray();
        if (promoted.Length == 0)
            return 0;

        var promotedSet = promoted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        unlockPollingNames = prefix
            .Concat(promoted)
            .Concat(remaining.Where(x => !promotedSet.Contains(x)))
            .ToArray();
        return promoted.Length;
    }

    public void PollSnapshotStep(int maxChecks)
    {
        if (maxChecks <= 0 || unlockPollingCursor >= unlockPollingNames.Length)
            return;

        var remaining = Math.Min(maxChecks, unlockPollingNames.Length - unlockPollingCursor);
        for (var i = 0; i < remaining; i++)
        {
            var dutyName = unlockPollingNames[unlockPollingCursor++];
            unlockSnapshot[dutyName] = ReadDutyUnlocked(dutyName);
        }
    }

    public DutyUnlockAdvice? GetAdvice(string dutyName) =>
        GetAdviceInternal(string.Empty, dutyName, allowNetworkRequests: true);

    public DutyUnlockAdvice? GetAdvice(string? setId, string dutyName) =>
        GetAdviceInternal(setId, dutyName, allowNetworkRequests: true);

    /// <summary>
    /// Resolves duty guidance using only live/local game data and already-cached
    /// community data. This method never starts an HTTP request. It is used by
    /// diagnostic exports so pressing an export button cannot alter resolver state
    /// or generate network traffic.
    /// </summary>
    public DutyUnlockAdvice? GetAdviceCachedOnly(string dutyName) =>
        GetAdviceInternal(string.Empty, dutyName, allowNetworkRequests: false);

    public DutyUnlockAdvice? GetAdviceCachedOnly(string? setId, string dutyName) =>
        GetAdviceInternal(setId, dutyName, allowNetworkRequests: false);

    private DutyUnlockAdvice? GetAdviceInternal(string? setId, string dutyName, bool allowNetworkRequests)
    {
        dutyName = NormalizeDutyName(dutyName);
        if (string.IsNullOrWhiteSpace(dutyName))
            return null;

        var isUnlocked = IsDutyUnlocked(dutyName);
        CuratedUnlockRoutes.TryGetValue(dutyName, out var curatedRoute);

        // ContentFinderCondition directly exposes up to two unlock criteria.
        // When UnlockType/UnlockType2 == 1 those rows are Quest references,
        // which is far more authoritative than trying to reverse-infer a quest
        // by scanning the Quest sheet. Prefer those direct links first.
        QuestGuidanceInfo? directInfo = null;
        if (directUnlockQuestIdsByDuty.TryGetValue(dutyName, out var directQuestIds))
        {
            directInfo = directQuestIds
                .Select(quests.GetById)
                .FirstOrDefault(x => x is not null);
        }

        var fallbackQuestName = ResolveUnlockQuest(setId ?? string.Empty, dutyName);
        var fallbackInfo = string.IsNullOrWhiteSpace(fallbackQuestName)
            ? null
            : quests.GetBestByName(fallbackQuestName);

        // Community fallback: FFXIV Todo ships a generated MIT-licensed content
        // index which includes the unlock quest ID for many optional duties whose
        // game-sheet relationship is script-only. Load it once, asynchronously,
        // and persist a reduced local cache rather than issuing a request per duty.
        QuestGuidanceInfo? communityInfo = null;
        string? communityNote = null;
        if (directInfo is null && fallbackInfo is null && curatedRoute is null)
        {
            var communityQuestId = communityDutyUnlocks.GetCachedQuestId(dutyName);
            if (communityQuestId is uint questId)
            {
                communityInfo = quests.GetById(questId);
                if (communityInfo is null)
                    communityNote = $"Community data resolved unlock quest #{questId}, but CQG could not find that quest row locally.";
            }
            else if (allowNetworkRequests)
            {
                communityDutyUnlocks.EnsureLoaded();
                if (communityDutyUnlocks.IsLoading)
                    communityNote = "Resolving unlock route from the community duty index...";
            }
            else
            {
                communityNote = "Missing network data (community duty index not cached for this duty).";
            }
        }

        // Final network fallback: Garland Tools parses quest-script unlock
        // instructions. Keep this after the one-shot community index because the
        // latter avoids one HTTP request per unresolved duty.
        QuestGuidanceInfo? garlandInfo = null;
        string? garlandNote = communityNote;
        if (directInfo is null && fallbackInfo is null && curatedRoute is null && communityInfo is null &&
            instanceContentByName.TryGetValue(dutyName, out var instanceContentId) &&
            instanceContentId != 0)
        {
            var garlandQuestId = garlandDutyUnlocks.GetCachedQuestId(instanceContentId);
            if (garlandQuestId is uint questId)
            {
                garlandInfo = quests.GetById(questId);
                if (garlandInfo is null)
                    garlandNote = $"Garland resolved unlock quest #{questId}, but CQG could not find that quest row locally.";
            }
            else if (allowNetworkRequests && !communityDutyUnlocks.IsLoading && !garlandDutyUnlocks.IsChecked(instanceContentId))
            {
                garlandDutyUnlocks.EnsureRequested(instanceContentId);
                garlandNote ??= "Resolving unlock route from Garland Tools...";
            }
            else if (!allowNetworkRequests && !garlandDutyUnlocks.IsChecked(instanceContentId))
            {
                garlandNote = "Missing network data (Garland duty lookup not cached for this duty).";
            }
        }

        var questName = directInfo?.Name ?? curatedRoute?.QuestName ?? fallbackInfo?.Name ?? communityInfo?.Name ?? garlandInfo?.Name ?? fallbackQuestName;
        var info = directInfo ?? fallbackInfo ?? communityInfo ?? garlandInfo ??
                   (string.IsNullOrWhiteSpace(questName) ? null : quests.GetBestByName(questName));

        var contact = info?.QuestGiver ?? curatedRoute?.Contact;
        var location = info?.Location ?? curatedRoute?.Location;
        string? note = curatedRoute?.Note ?? communityNote ?? garlandNote;

        // Hub minstrel quasi-quests are repeat contacts after the journal entry
        // itself has completed. These are curated contact fallbacks, not the
        // general duty-resolution mechanism.
        if ((setId ?? string.Empty).Equals("mount-extreme-dt", StringComparison.OrdinalIgnoreCase))
        {
            contact ??= "Wandering Minstrel";
            location = "Tuliyollal (X:11.2, Y:14.7)";
            note ??= "Return after completing the normal/story prerequisite for this trial.";
        }
        else if ((setId ?? string.Empty).Equals("mount-extreme-ew", StringComparison.OrdinalIgnoreCase))
        {
            contact ??= "Wandering Minstrel";
            location = "Old Sharlayan (X:12.7, Y:14.3)";
            note ??= "Return after completing the normal/story prerequisite for this trial.";
        }
        else if ((setId ?? string.Empty).Equals("mount-extreme-sb", StringComparison.OrdinalIgnoreCase))
        {
            contact ??= "Wandering Minstrel";
            location = "Kugane (X:11.6, Y:12.6)";
            note ??= "Return after completing the normal/story prerequisite for this trial.";
        }
        else if ((setId ?? string.Empty).Equals("mount-extreme-shb", StringComparison.OrdinalIgnoreCase))
        {
            if (questName.Equals("Weapon of Choice", StringComparison.OrdinalIgnoreCase))
            {
                contact ??= "Warmachina Fanatic";
                location = "The Lochs (X:11.6, Y:22.6)";
            }
            else
            {
                contact ??= "Minstreling Wanderer";
                location = "The Crystarium (X:7.5, Y:12.5)";
            }
            note ??= "Return after completing the associated normal/story prerequisite.";
        }

        // If we can at least identify the duty and its live state, return an
        // advice object even when the reverse unlock quest is unavailable. The
        // caller can then say Run vs Unlock rather than falsely claiming the
        // content is immediately actionable.
        if (!instanceContentByName.ContainsKey(dutyName) &&
            string.IsNullOrWhiteSpace(questName))
        {
            return null;
        }

        return new DutyUnlockAdvice(dutyName, isUnlocked, questName, contact, location, note);
    }


    public uint GetInstanceContentId(string dutyName)
    {
        dutyName = NormalizeDutyName(dutyName);
        if (instanceContentByName.TryGetValue(dutyName, out var exact))
            return exact;

        var normalized = NormalizeDutyLookupKey(dutyName);
        var matches = instanceContentByName
            .Where(x => NormalizeDutyLookupKey(x.Key).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0].Value : 0;
    }

    public uint GetContentFinderConditionId(string dutyName)
    {
        dutyName = NormalizeDutyName(dutyName);
        if (contentFinderIdByName.TryGetValue(dutyName, out var exact))
            return exact;

        var normalized = NormalizeDutyLookupKey(dutyName);
        var matches = contentFinderIdByName
            .Where(x => NormalizeDutyLookupKey(x.Key).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0].Value : 0;
    }

    private static string NormalizeDutyLookupKey(string value) =>
        new((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private readonly Dictionary<string, bool?> focusedUnlocks = new(StringComparer.OrdinalIgnoreCase);

    public void RefreshFocusedUnlocks(IEnumerable<string> dutyNames)
    {
        focusedUnlocks.Clear();
        foreach (var name in dutyNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
            focusedUnlocks[NormalizeDutyName(name)] = ReadDutyUnlocked(name);
    }

    public bool? IsDutyUnlocked(string dutyName)
    {
        dutyName = NormalizeDutyName(dutyName);
        if (focusedUnlocks.TryGetValue(dutyName, out var focused) && focused.HasValue)
            return focused;
        return unlockSnapshot.TryGetValue(dutyName, out var unlocked) ? unlocked : null;
    }

    private bool? ReadDutyUnlocked(string dutyName)
    {
        dutyName = NormalizeDutyName(dutyName);
        if (!instanceContentByName.TryGetValue(dutyName, out var instanceId) || instanceId == 0)
            return null;

        try
        {
            return UIState.IsInstanceContentUnlocked(instanceId);
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractDutyName(SourceEntry source)
    {
        var value = source.Name.Trim();
        var prefixes = new[] { "Trial:", "Duty:", "Raid:", "Alliance Raid:", "Dungeon:" };
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var extracted = value[prefix.Length..].Trim();
                var providerSeparator = extracted.IndexOf(" — ", StringComparison.Ordinal);
                if (providerSeparator > 0)
                    extracted = extracted[..providerSeparator].Trim();
                return NormalizeDutyName(extracted);
            }
        }

        // Exact live-game duty sources are emitted by SourceResolver as
        // Kind=Activity with the ContentFinderCondition name directly in Name
        // (for example "Containment Bay P1T6 (Extreme)").  Earlier versions
        // only recognized prefixed strings, so the UI knew an activity was
        // locked but could not promote it into a first-class pinnable duty.
        if (source.Kind.Equals("Activity", StringComparison.OrdinalIgnoreCase) ||
            source.Kind.Equals("Duty", StringComparison.OrdinalIgnoreCase))
        {
            var providerSeparator = value.IndexOf(" — ", StringComparison.Ordinal);
            if (providerSeparator > 0)
                value = value[..providerSeparator].Trim();

            // Reject broad/generic source labels; callers will validate named
            // candidates against the duty catalog / CFC index before pinning.
            if (value.Equals("Activity", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Duty", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Duty Chest", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Duty / Instance Drop", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Instance Content", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return NormalizeDutyName(value);
        }

        return null;
    }

    private string ResolveUnlockQuest(string setId, string dutyName)
    {
        if (CuratedUnlockRoutes.TryGetValue(dutyName, out var curated))
            return curated.QuestName;

        if (QuestByDuty.TryGetValue(dutyName, out var exact))
            return exact;

        if (inferredQuestByDuty.TryGetValue(dutyName, out var inferred))
            return inferred;

        return setId.ToLowerInvariant() switch
        {
            "mount-extreme-sb" => "Songs in the Key of Kugane",
            "mount-extreme-ew" => "I Wandered Sharlayan as a Minstrel",
            "mount-extreme-dt" => "How the West Was Sung",
            "mount-extreme-shb" => "Minstrel from Another Mother",
            _ => string.Empty,
        };
    }

    private static string NormalizeDutyName(string value) =>
        (value ?? string.Empty).Trim().Trim(' ', '—', '-');

    private void BuildContentIndex(IDataManager data)
    {
        try
        {
            foreach (var cfc in data.GetExcelSheet<ContentFinderCondition>())
            {
                var name = cfc.Name.ExtractText().Trim();
                if (cfc.RowId == 0 || string.IsNullOrWhiteSpace(name))
                    continue;

                var instanceId = ReflectionUtil.GetNestedRowId(cfc, "Content", "InstanceContent");
                if (instanceId == 0)
                    continue;

                instanceContentByName.TryAdd(name, instanceId);
                dutyNameByInstanceContentId.TryAdd(instanceId, name);
                dutyNameByContentFinderId.TryAdd(cfc.RowId, name);
                contentFinderIdByName.TryAdd(name, cfc.RowId);

                var directQuestIds = new List<uint>(2);
                var unlockType = ReflectionUtil.GetNumericProperty(cfc, "UnlockType") ?? 0;
                if (unlockType == 1)
                {
                    var questId = ReadRowIdOrNumeric(cfc, "UnlockCriteria");
                    if (questId != 0)
                        directQuestIds.Add(questId);
                }

                var unlockType2 = ReflectionUtil.GetNumericProperty(cfc, "UnlockType2") ?? 0;
                if (unlockType2 == 1)
                {
                    var questId = ReadRowIdOrNumeric(cfc, "UnlockCriteria2");
                    if (questId != 0 && !directQuestIds.Contains(questId))
                        directQuestIds.Add(questId);
                }

                if (directQuestIds.Count > 0)
                    directUnlockQuestIdsByDuty[name] = directQuestIds.ToArray();
            }

            log.Debug($"CQG duty index: {instanceContentByName.Count:N0} duties, {directUnlockQuestIdsByDuty.Count:N0} with direct ContentFinder unlock quest criteria.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build duty unlock-state index.");
        }
    }

    private static uint ReadRowIdOrNumeric(object row, params string[] propertyNames)
    {
        // ContentFinderCondition unlock criteria are scalar quest IDs on current
        // game sheets, while some schema revisions expose row references. Handle
        // both shapes instead of assuming a RowRef and silently resolving 0.
        var nested = ReflectionUtil.GetNestedRowId(row, propertyNames);
        if (nested != 0)
            return nested;

        var numeric = ReflectionUtil.GetNumericProperty(row, propertyNames);
        return numeric is > 0 and <= uint.MaxValue ? (uint)numeric.Value : 0;
    }

    private void BuildQuestUnlockIndex(IDataManager data)
    {
        try
        {
            foreach (var quest in data.GetExcelSheet<Quest>())
            {
                if (quest.RowId == 0)
                    continue;

                var questName = quest.Name.ExtractText().Trim();
                if (string.IsNullOrWhiteSpace(questName))
                    continue;

                foreach (var property in quest.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    // Restrict reflection to fields that semantically describe
                    // unlocked instanced content. This avoids accidentally
                    // treating unrelated content references as unlock relations.
                    if (!property.Name.Contains("Content", StringComparison.OrdinalIgnoreCase) &&
                        !property.Name.Contains("Instance", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!property.Name.Contains("Unlock", StringComparison.OrdinalIgnoreCase) &&
                        !property.Name.Contains("Finder", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    object? value;
                    try
                    {
                        value = property.GetValue(quest);
                    }
                    catch
                    {
                        continue;
                    }

                    var isFinderReference = property.Name.Contains("Finder", StringComparison.OrdinalIgnoreCase);
                    var isInstanceReference = property.Name.Contains("Instance", StringComparison.OrdinalIgnoreCase);

                    foreach (var id in EnumerateReferencedRowIds(value))
                    {
                        string? dutyName = null;
                        if (isFinderReference)
                            dutyNameByContentFinderId.TryGetValue(id, out dutyName);
                        if (string.IsNullOrWhiteSpace(dutyName) && isInstanceReference)
                            dutyNameByInstanceContentId.TryGetValue(id, out dutyName);

                        if (!string.IsNullOrWhiteSpace(dutyName))
                            inferredQuestByDuty.TryAdd(dutyName, questName);
                    }
                }
            }

            log.Debug($"CQG generic duty unlock quest index: {inferredQuestByDuty.Count:N0} duties.");
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not build reverse duty -> quest unlock index.");
        }
    }

    private static IEnumerable<uint> EnumerateReferencedRowIds(object? value)
    {
        if (value is null)
            yield break;

        var direct = ReflectionUtil.ExtractRowId(value);
        if (direct != 0)
            yield return direct;

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var entry in enumerable)
            {
                var id = ReflectionUtil.ExtractRowId(entry);
                if (id != 0)
                    yield return id;
            }
        }
    }
}

// ===== CommunityDutyUnlockIndex.cs =====
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

// ===== GarlandDutyUnlockIndex.cs =====
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
    private readonly ConcurrentDictionary<uint, DateTime> retryAfterUtc = new();
    private readonly CancellationTokenSource disposeCts = new();
    private readonly KeyedWorkScheduler<uint> dutyWork;
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
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CustomQuestGuide/1.3.4.20");
        dutyWork = new KeyedWorkScheduler<uint>(
            maxConcurrency: 1,
            minimumStartInterval: TimeSpan.FromSeconds(2),
            ResolveAsync,
            disposeCts.Token);
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
        instanceContentId != 0 && dutyWork.IsPending(instanceContentId);

    /// <summary>
    /// Queues one keyed duty lookup if it is not already cached/pending. Duty
    /// fallback work uses the same bounded scheduler primitive as item/monster
    /// enrichment, so concurrent UI actions queue deterministically instead of
    /// being silently dropped while another request owns the old pace gate.
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

        if (dutyWork.TryQueue(instanceContentId))
            networkRequests.CacheMiss("Garland duty");
    }

    private async Task ResolveAsync(uint instanceContentId, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://www.garlandtools.org/db/doc/instance/en/3/{instanceContentId}.json";
            networkRequests.Request("Garland duty");
            using var response = await http.GetAsync(url, cancellationToken);

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

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        if (dutyWork.PendingCount > 0)
            return false;

        questIdByInstanceContent.Clear();
        retryAfterUtc.Clear();

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
        dutyWork.Dispose();
        http.Dispose();
        disposeCts.Dispose();
    }
}
