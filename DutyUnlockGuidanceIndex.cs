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

    private readonly Dictionary<string, uint> instanceContentByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> dutyNameByInstanceContentId = [];
    private readonly Dictionary<uint, string> dutyNameByContentFinderId = [];
    private readonly Dictionary<string, string> inferredQuestByDuty =
        new(StringComparer.OrdinalIgnoreCase);

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

    public DutyUnlockGuidanceIndex(IDataManager data, QuestGuidanceIndex quests, IPluginLog log)
    {
        this.quests = quests;
        this.log = log;
        BuildContentIndex(data);
        BuildQuestUnlockIndex(data);
    }

    public DutyUnlockAdvice? GetAdvice(string dutyName) =>
        GetAdvice(string.Empty, dutyName);

    public DutyUnlockAdvice? GetAdvice(string? setId, string dutyName)
    {
        dutyName = NormalizeDutyName(dutyName);
        if (string.IsNullOrWhiteSpace(dutyName))
            return null;

        var isUnlocked = IsDutyUnlocked(dutyName);
        var questName = ResolveUnlockQuest(setId ?? string.Empty, dutyName);
        var info = string.IsNullOrWhiteSpace(questName) ? null : quests.GetBestByName(questName);

        var contact = info?.QuestGiver;
        var location = info?.Location;
        string? note = null;

        // Hub minstrel quasi-quests are repeat contacts after the journal entry
        // itself has completed. These are curated contact fallbacks, not the
        // general duty-resolution mechanism.
        if ((setId ?? string.Empty).Equals("mount-extreme-dt", StringComparison.OrdinalIgnoreCase))
        {
            contact ??= "Wandering Minstrel";
            location = "Tuliyollal (X:11.2, Y:14.7)";
            note = "Return after completing the normal/story prerequisite for this trial.";
        }
        else if ((setId ?? string.Empty).Equals("mount-extreme-ew", StringComparison.OrdinalIgnoreCase))
        {
            contact ??= "Wandering Minstrel";
            location = "Old Sharlayan (X:12.7, Y:14.3)";
            note = "Return after completing the normal/story prerequisite for this trial.";
        }
        else if ((setId ?? string.Empty).Equals("mount-extreme-sb", StringComparison.OrdinalIgnoreCase))
        {
            contact ??= "Wandering Minstrel";
            location = "Kugane (X:11.6, Y:12.6)";
            note = "Return after completing the normal/story prerequisite for this trial.";
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
            note = "Return after completing the associated normal/story prerequisite.";
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

    public bool? IsDutyUnlocked(string dutyName)
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
                return NormalizeDutyName(value[prefix.Length..]);
        }
        return null;
    }

    private string ResolveUnlockQuest(string setId, string dutyName)
    {
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
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not build duty unlock-state index.");
        }
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
