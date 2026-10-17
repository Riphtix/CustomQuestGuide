using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Local reverse index from battle-leve enemy identities to the levequests that
/// can contain them. The relationship comes from the game's Leve -> BattleLeve
/// -> LeveData rows, so normal runtime use is deterministic and zero-network.
/// </summary>
public sealed class LeveMonsterIndex
{
    private readonly Dictionary<(uint BaseId, uint NameId), List<MonsterLeveContext>> byIdentity = [];
    private readonly Dictionary<uint, List<MonsterLeveContext>> byNameId = [];

    public int IndexedMonsterIdentityCount => byIdentity.Count;
    public int IndexedMonsterNameCount => byNameId.Count;

    public LeveMonsterIndex(IDataManager data, IPluginLog log)
    {
        try
        {
            Build(data);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to build local battle-leve monster index.");
        }
    }

    public IReadOnlyList<MonsterLeveContext> GetLeves(uint? baseId, uint? nameId)
    {
        var merged = new Dictionary<uint, MonsterLeveContext>();

        if (baseId is > 0 && nameId is > 0 &&
            byIdentity.TryGetValue((baseId.Value, nameId.Value), out var exact))
        {
            foreach (var context in exact)
                merged[context.LeveId] = context;
        }

        // BNpcName is the explicit enemy-name relationship stored in BattleLeve.
        // Keep it as a conservative fallback when another provider has not yet
        // recovered BNpcBase, or when the schema cannot resolve the BaseID union.
        if (nameId is > 0 && byNameId.TryGetValue(nameId.Value, out var byName))
        {
            foreach (var context in byName)
                merged.TryAdd(context.LeveId, context);
        }

        return merged.Values
            .OrderBy(x => x.Level)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.LeveId)
            .ToArray();
    }

    public IReadOnlyList<LeveMonsterTestCandidate> BuildTestCandidates()
    {
        var results = new List<LeveMonsterTestCandidate>();

        foreach (var pair in byNameId.OrderBy(x => x.Key))
        {
            var contexts = pair.Value
                .GroupBy(x => x.LeveId)
                .Select(x => x.First())
                .OrderBy(x => x.Level)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (contexts.Length == 0)
                continue;

            var baseId = byIdentity.Keys
                .Where(x => x.NameId == pair.Key)
                .Select(x => x.BaseId)
                .OrderBy(x => x)
                .FirstOrDefault();

            results.Add(new LeveMonsterTestCandidate(
                baseId,
                pair.Key,
                contexts));
        }

        return results;
    }

    private void Build(IDataManager data)
    {
        var leveContextsByBattleLeveId = new Dictionary<uint, List<MonsterLeveContext>>();

        foreach (var leve in data.GetExcelSheet<Leve>())
        {
            if (leve.RowId == 0)
                continue;

            var dataRef = ReflectionUtil.GetPropertyValue(leve, "DataId");
            if (!ReferencesType(dataRef, "BattleLeve"))
                continue;

            var battleLeveId = ReflectionUtil.ExtractRowId(dataRef);
            if (battleLeveId == 0)
                continue;

            var name = leve.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = $"Levequest {leve.RowId}";

            var level = ReadUInt(leve, "ClassJobLevel", "Level");
            var context = new MonsterLeveContext(leve.RowId, name, level);

            if (!leveContextsByBattleLeveId.TryGetValue(battleLeveId, out var contexts))
            {
                contexts = [];
                leveContextsByBattleLeveId[battleLeveId] = contexts;
            }

            if (!contexts.Any(x => x.LeveId == context.LeveId))
                contexts.Add(context);
        }

        foreach (var battleLeve in data.GetExcelSheet<BattleLeve>())
        {
            if (battleLeve.RowId == 0 ||
                !leveContextsByBattleLeveId.TryGetValue(battleLeve.RowId, out var leveContexts) ||
                leveContexts.Count == 0)
            {
                continue;
            }

            var leveData = ReflectionUtil.GetPropertyValue(battleLeve, "LeveData");
            if (leveData is not IEnumerable entries)
                continue;

            foreach (var entry in entries)
            {
                if (entry is null)
                    continue;

                var nameRef = ReflectionUtil.GetPropertyValue(entry, "BNpcName");
                var nameId = ReflectionUtil.ExtractRowId(nameRef);
                if (nameId == 0)
                    continue;

                var baseRef = ReflectionUtil.GetPropertyValue(entry, "BaseID", "BaseId");
                var baseId = ReferencesType(baseRef, "BNpcBase")
                    ? ReflectionUtil.ExtractRowId(baseRef)
                    : 0u;

                foreach (var context in leveContexts)
                {
                    Add(byNameId, nameId, context);
                    if (baseId != 0)
                        Add(byIdentity, (baseId, nameId), context);
                }
            }
        }
    }

    private static void Add<TKey>(Dictionary<TKey, List<MonsterLeveContext>> index, TKey key, MonsterLeveContext context)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        if (!list.Any(x => x.LeveId == context.LeveId))
            list.Add(context);
    }

    private static bool ReferencesType(object? rowRef, string expectedTypeName)
    {
        if (rowRef is null)
            return false;

        try
        {
            var rowType = rowRef.GetType().GetProperty("RowType")?.GetValue(rowRef) as Type;
            if (rowType is not null)
            {
                return rowType.Name.Equals(expectedTypeName, StringComparison.OrdinalIgnoreCase) ||
                       rowType.FullName?.EndsWith($".{expectedTypeName}", StringComparison.OrdinalIgnoreCase) == true;
            }

            // Generic RowRef<T> implementations encode their target in the
            // generic argument even if a RowType property is absent.
            var type = rowRef.GetType();
            if (type.IsGenericType)
            {
                return type.GetGenericArguments().Any(argument =>
                    argument.Name.Equals(expectedTypeName, StringComparison.OrdinalIgnoreCase) ||
                    argument.FullName?.EndsWith($".{expectedTypeName}", StringComparison.OrdinalIgnoreCase) == true);
            }
        }
        catch
        {
            // Treat an unreadable union reference as unknown rather than guessing.
        }

        return false;
    }

    private static uint ReadUInt(object row, params string[] names)
    {
        var numeric = ReflectionUtil.GetNumericProperty(row, names);
        if (!numeric.HasValue || numeric.Value <= 0)
            return 0;
        return numeric.Value > uint.MaxValue ? uint.MaxValue : (uint)numeric.Value;
    }
}

public sealed record LeveMonsterTestCandidate(
    uint MonsterBaseId,
    uint MonsterNameId,
    IReadOnlyList<MonsterLeveContext> Leves);
