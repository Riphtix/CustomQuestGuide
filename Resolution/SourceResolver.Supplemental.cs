using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

public sealed partial class SourceResolver
{
    // ---------------------------------------------------------------------
    // Duty / supplemental sources
    // ---------------------------------------------------------------------

    private IEnumerable<ContentFinderInfo> ResolveDutyReference(DutyReference reference)
    {
        if (reference.Kind == DutyReferenceKind.ContentFinderCondition)
        {
            if (cfcById.TryGetValue(reference.Id, out var cfc))
                yield return cfc;

            yield break;
        }

        if (reference.Kind == DutyReferenceKind.TerritoryType &&
            cfcByTerritory.TryGetValue(reference.Id, out var duties))
        {
            // TerritoryType is not a unique duty key. Multiple duties can share
            // a territory, and returning all/first matches can fabricate sources
            // (the Dawntrail Wing -> Alexander bug was one example).
            //
            // Only trust a territory-only association when it resolves to one
            // unique Content Finder entry.
            var unique = duties
                .GroupBy(d => d.CfcId)
                .Select(g => g.First())
                .ToArray();

            if (unique.Length == 1)
                yield return unique[0];
        }
    }

    private void BuildContentFinderIndex()
    {
        foreach (var cfc in data.GetExcelSheet<ContentFinderCondition>())
        {
            var name = cfc.Name.ExtractText().Trim();

            if (cfc.RowId == 0 || string.IsNullOrWhiteSpace(name))
                continue;

            var territoryId = ReflectionUtil.GetNestedRowId(
                cfc,
                "TerritoryType",
                "Territory");

            var instanceContentId = ReflectionUtil.GetNestedRowId(
                cfc,
                "Content",
                "InstanceContent");

            var info = new ContentFinderInfo(
                cfc.RowId,
                name,
                territoryId,
                instanceContentId,
                ReflectionUtil.GetBoolProperty(cfc, "AllianceRoulette") == true,
                ReflectionUtil.GetBoolProperty(cfc, "NormalRaidRoulette") == true,
                ReflectionUtil.GetBoolProperty(cfc, "TrialRoulette") == true,
                (uint?)ReflectionUtil.GetNumericProperty(cfc, "QueueMaxPlayers") ?? 0);

            cfcById[cfc.RowId] = info;

            if (territoryId != 0)
            {
                if (!cfcByTerritory.TryGetValue(territoryId, out var list))
                {
                    list = [];
                    cfcByTerritory[territoryId] = list;
                }

                list.Add(info);
            }
        }
    }

    private SourceEntry? ResolveSupplemental(SupplementalSourceRecord record)
    {
        var row = record.Row;

        if (record.Dataset.StartsWith("Dungeon", StringComparison.OrdinalIgnoreCase))
        {
            var cfcId = SupplementalCsvIndex.FirstIdContaining(
                row, "ContentFinderCondition", "Cfc");

            // An explicit ContentFinderCondition on the item-source row is a
            // direct item -> duty relationship.  This is materially stronger
            // than a duty inferred by joining a generic chest/boss parent row,
            // so preserve that provenance instead of mixing it into the broad
            // supplemental-association bucket.
            if (cfcId != 0 && cfcById.TryGetValue(cfcId, out var direct))
                return DutySource(direct, "Supplemental exact duty relation");

            var territoryId = SupplementalCsvIndex.FirstIdContaining(
                row, "TerritoryType", "Territory");

            if (territoryId != 0 &&
                cfcByTerritory.TryGetValue(territoryId, out var duties))
            {
                var unique = duties
                    .GroupBy(d => d.CfcId)
                    .Select(g => g.First())
                    .ToArray();

                if (unique.Length == 1)
                    return DutySource(unique[0], "Supplemental duty association");
            }

            // Do not invent a duty name from an ambiguous territory mapping.
            return new SourceEntry(
                "Duty",
                record.Kind,
                "Duty family known; exact duty unresolved.",
                Priority: 90);
        }

        if (record.Dataset.Equals("FateItem", StringComparison.OrdinalIgnoreCase))
        {
            var name = ResolveFateName(row) ?? "FATE reward";
            return new SourceEntry("FATE", name, Priority: 50);
        }

        if (record.Dataset.Equals("MobDrop", StringComparison.OrdinalIgnoreCase))
        {
            var bnpcNameId = SupplementalCsvIndex.FirstIdNamed(
                row,
                "BNpcName", "BNpcNameId");
            var directBaseId = SupplementalCsvIndex.FirstIdNamed(
                row,
                "BNpcBase", "BNpcBaseId");
            uint? bnpcBaseId = directBaseId != 0
                ? directBaseId
                : supplemental.GetUniqueMobBaseId(bnpcNameId);
            var name = ResolveMobName(row) ?? "Monster drop";

            // BNpcName alone is not a complete monster identity. Use exact
            // base+name metadata only when the local BNpcLink relationship is
            // unambiguous; otherwise leave location completion to enrichment.
            var level = bnpcBaseId.HasValue
                ? supplemental.GetLowestMobLevel(bnpcBaseId.Value, bnpcNameId)
                : supplemental.GetLowestMobLevel(bnpcNameId);
            var zoneLevel = bnpcBaseId.HasValue
                ? supplemental.GetLowestMobZoneLevel(bnpcBaseId.Value, bnpcNameId)
                : supplemental.GetLowestMobZoneLevel(bnpcNameId);
            var locations = bnpcBaseId.HasValue
                ? supplemental.GetMobSpawnLocations(bnpcBaseId.Value, bnpcNameId)
                : Array.Empty<MonsterSpawnLocation>();

            return new SourceEntry(
                "Monster",
                name.StartsWith("Monster: ", StringComparison.OrdinalIgnoreCase)
                    ? name
                    : $"Monster: {name}",
                Priority: 50,
                SpecificityHint: "Named",
                MonsterLevel: level,
                MonsterZoneLevel: zoneLevel,
                MonsterLocations: locations,
                MonsterNameId: bnpcNameId == 0 ? null : bnpcNameId,
                MonsterBaseId: bnpcBaseId);
        }

        if (record.Dataset.Equals("FieldOpCoffer", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry("Field Operation", "Field operation coffer", Priority: 50);

        if (record.Dataset.Equals("RetainerVentureItem", StringComparison.OrdinalIgnoreCase))
        {
            var ventureId = SupplementalCsvIndex.FirstIdContaining(row, "RetainerTask");
            if (ventureId == 0)
                ventureId = SupplementalCsvIndex.FirstIdContaining(row, "Venture");

            if (ventureId != 0 &&
                retainerVentureLabelsById.TryGetValue(ventureId, out var ventureLabel))
            {
                return new SourceEntry(
                    "Retainer Venture",
                    $"Retainer Venture: {ventureLabel}",
                    "LuminaSupplemental venture relationship",
                    Priority: 55,
                    SpecificityHint: "Named");
            }

            var method = SupplementalCsvIndex.GetText(
                row,
                "VentureType",
                "Type",
                "Method",
                "Source");

            if (!string.IsNullOrWhiteSpace(method) &&
                !method.Equals("Retainer Venture", StringComparison.OrdinalIgnoreCase))
            {
                return new SourceEntry(
                    "Retainer Venture",
                    $"Retainer Venture: {method}",
                    "LuminaSupplemental venture relationship",
                    Priority: 55,
                    SpecificityHint: "Named");
            }

            return new SourceEntry("Retainer Venture", "Retainer Venture", Priority: 60);
        }

        if (record.Dataset.Equals("AirshipDrop", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry(
                "Airship Voyage",
                ResolveRouteName(row, airship: true) ?? "Airship voyage",
                Priority: 60);

        if (record.Dataset.Equals("SubmarineDrop", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry(
                "Submarine Voyage",
                ResolveRouteName(row, airship: false) ?? "Submarine voyage",
                Priority: 60);

        if (record.Dataset.Equals("GardeningCrossbreed", StringComparison.OrdinalIgnoreCase))
            return new SourceEntry("Gardening", "Gardening crossbreed", Priority: 60);

        if (record.Dataset.Equals("ItemSupplement", StringComparison.OrdinalIgnoreCase))
        {
            var type =
                SupplementalCsvIndex.GetText(row, "SourceType", "Type", "Method", "Reason")
                ?? "Item conversion";

            return new SourceEntry("Item Conversion", type, Priority: 70);
        }

        return new SourceEntry(record.Kind, record.Kind, Priority: 90);
    }

    private SourceEntry DutySource(ContentFinderInfo duty, string? specificityHint = null)
    {
        var unlocked = IsContentUnlocked(duty.InstanceContentId);

        return new SourceEntry(
            "Activity",
            duty.Name,
            IsUnlocked: unlocked,
            Priority: unlocked == true ? 10 : 15,
            SpecificityHint: specificityHint);
    }

}
