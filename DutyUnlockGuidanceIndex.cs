using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

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

    public bool? IsDutyUnlocked(string dutyName)
    {
        dutyName = NormalizeDutyName(dutyName);
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
