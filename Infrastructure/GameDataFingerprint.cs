using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

/// <summary>
/// Builds a cheap local signature for the installed FFXIV data set.
///
/// The signature is not a cache key and does not invalidate cached answers.
/// It only tells CQG that network-derived enrichment should be revalidated the
/// next time the affected item is selected. This preserves instant cache-first
/// UI while allowing major patches/expansions to refresh external data once.
/// </summary>
internal static class GameDataFingerprint
{
    public static string Create(IDataManager data)
    {
        var item = GetItemSignature(data);
        var territory = GetTerritorySignature(data);
        var monster = GetMonsterSignature(data);

        return $"item:{item.Count}:{item.MaxRowId}|territory:{territory.Count}:{territory.MaxRowId}|monster:{monster.Count}:{monster.MaxRowId}";
    }

    private static (int Count, uint MaxRowId) GetItemSignature(IDataManager data)
    {
        var count = 0;
        uint maxRowId = 0;
        foreach (var row in data.GetExcelSheet<Item>())
        {
            count++;
            if (row.RowId > maxRowId)
                maxRowId = row.RowId;
        }

        return (count, maxRowId);
    }

    private static (int Count, uint MaxRowId) GetTerritorySignature(IDataManager data)
    {
        var count = 0;
        uint maxRowId = 0;
        foreach (var row in data.GetExcelSheet<TerritoryType>())
        {
            count++;
            if (row.RowId > maxRowId)
                maxRowId = row.RowId;
        }

        return (count, maxRowId);
    }

    private static (int Count, uint MaxRowId) GetMonsterSignature(IDataManager data)
    {
        var count = 0;
        uint maxRowId = 0;
        foreach (var row in data.GetExcelSheet<BNpcName>())
        {
            count++;
            if (row.RowId > maxRowId)
                maxRowId = row.RowId;
        }

        return (count, maxRowId);
    }
}
