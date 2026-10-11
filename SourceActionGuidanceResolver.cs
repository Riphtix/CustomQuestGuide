using System;
using System.Linq;

namespace CustomQuestGuide;

/// <summary>
/// Shared next-action resolver for acquisition results. UI surfaces should ask
/// this class what the player can do next rather than re-implementing quest,
/// duty, or NPC routing rules independently.
/// </summary>
public sealed class SourceActionGuidanceResolver
{
    private readonly QuestGuidanceIndex questGuidance;
    private readonly DutyUnlockGuidanceIndex dutyUnlocks;
    private readonly NpcLocationIndex npcLocations;
    private readonly Func<uint, QuestProgressState> getQuestStateByRowId;
    private readonly Func<uint, TargetSet?> findContainingSet;

    public SourceActionGuidanceResolver(
        QuestGuidanceIndex questGuidance,
        DutyUnlockGuidanceIndex dutyUnlocks,
        NpcLocationIndex npcLocations,
        Func<uint, QuestProgressState> getQuestStateByRowId,
        Func<uint, TargetSet?> findContainingSet)
    {
        this.questGuidance = questGuidance;
        this.dutyUnlocks = dutyUnlocks;
        this.npcLocations = npcLocations;
        this.getQuestStateByRowId = getQuestStateByRowId;
        this.findContainingSet = findContainingSet;
    }

    public SourceActionGuidance Resolve(AcquisitionResult result)
    {
        if (result.Sources.Count == 0)
            return SourceActionGuidance.Empty;

        SourceActionGuidance FromQuestSource(SourceEntry source)
        {
            var names = QuestGuidanceIndex.ExtractQuestNames(source);
            if (names.Count == 0)
                return SourceActionGuidance.Empty;

            var status = questGuidance.ResolveSequence(names, getQuestStateByRowId);
            if (status.IsComplete || status.CurrentOrNext is not { } step)
                return SourceActionGuidance.Empty;

            var label = status.Active is not null ? "Current quest" : "Next quest";
            var who = FormatWhoWhere(step.Info);
            return new SourceActionGuidance(
                label,
                step.Name,
                status.Active is null ? who : string.Empty,
                status.Active is not null ? who : string.Empty,
                source);
        }

        var primary = result.Sources[0];
        var primaryQuest = FromQuestSource(primary);
        if (!primaryQuest.IsEmpty)
            return primaryQuest;

        foreach (var relicSource in result.Sources.Where(x =>
                     x.Kind.Equals("Relic", StringComparison.OrdinalIgnoreCase)))
        {
            var relicQuest = FromQuestSource(relicSource);
            if (!relicQuest.IsEmpty)
                return relicQuest;
        }

        var containingSet = findContainingSet(result.Target.ItemId);
        foreach (var source in result.Sources)
        {
            var dutyName = DutyUnlockGuidanceIndex.ExtractDutyName(source);
            if (string.IsNullOrWhiteSpace(dutyName))
                continue;

            var advice = containingSet is null
                ? dutyUnlocks.GetAdvice(dutyName)
                : dutyUnlocks.GetAdvice(containingSet.Id, dutyName);
            if (advice is null)
                continue;

            if (advice.IsUnlocked == true)
                return new SourceActionGuidance("Run", advice.DutyName, string.Empty, string.Empty, source);

            var who = string.IsNullOrWhiteSpace(advice.Contact)
                ? string.Empty
                : string.IsNullOrWhiteSpace(advice.Location)
                    ? advice.Contact!
                    : $"{advice.Contact} — {advice.Location}";
            var route = string.IsNullOrWhiteSpace(advice.QuestName)
                ? advice.DutyName
                : $"{advice.DutyName} via {advice.QuestName}";
            return new SourceActionGuidance("Unlock content", route, who, string.Empty, source);
        }

        var npc = ExtractNamedNpc(primary);
        if (!string.IsNullOrWhiteSpace(npc))
            return BuildNpcGuidance(primary, npc!);

        if (primary.Kind.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
            primary.Kind.Contains("Vendor", StringComparison.OrdinalIgnoreCase) ||
            primary.Name.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
            primary.Name.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var source in result.Sources.Skip(1))
            {
                npc = ExtractNamedNpc(source);
                if (!string.IsNullOrWhiteSpace(npc))
                    return BuildNpcGuidance(source, npc!);
            }
        }

        return SourceActionGuidance.Empty;
    }

    private SourceActionGuidance BuildNpcGuidance(SourceEntry source, string npc)
    {
        var location = npcLocations.GetBestLocation(npc);
        var who = string.IsNullOrWhiteSpace(location) ? npc : $"{npc} — {location}";
        return new SourceActionGuidance("Go to", who, string.Empty, string.Empty, source);
    }

    private static string FormatWhoWhere(QuestGuidanceInfo? info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.QuestGiver))
            return string.Empty;

        return string.IsNullOrWhiteSpace(info.Location)
            ? info.QuestGiver!
            : $"{info.QuestGiver} — {info.Location}";
    }

    private static string? ExtractNamedNpc(SourceEntry source)
    {
        var name = source.Name.Trim();
        var directPrefixes = new[] { "Vendor: ", "Triple Triad NPC: ", "NPC: " };

        foreach (var prefix in directPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..].Trim();
        }

        if (name.StartsWith("Special / Currency Shop:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Gil Vendor:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Grand Company Quartermaster:", StringComparison.OrdinalIgnoreCase))
        {
            var dash = name.LastIndexOf(" — ", StringComparison.Ordinal);
            if (dash >= 0 && dash + 3 < name.Length)
                return name[(dash + 3)..].Trim();
        }

        return null;
    }
}
