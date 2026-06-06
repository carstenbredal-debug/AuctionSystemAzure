using AuctionSystem.Functions.Models;

namespace AuctionSystem.Functions.Services;

public static class LotPropertyHelper
{
    public static string GetPropertyValue(string columnName,
        string salesType, string gender, string group,
        string hairLength, string size, string quality,
        string color, string clarity, string damages)
    {
        return columnName switch
        {
            "SalesType" => salesType ?? "",
            "Gender" => gender ?? "",
            "Group" => group ?? "",
            "HairLength" => hairLength ?? "",
            "Size" => size ?? "",
            "Quality" => quality ?? "",
            "Color" => color ?? "",
            "Clarity" => clarity ?? "",
            "Damages" => damages ?? "",
            _ => throw new InvalidOperationException(
                $"Unknown column: {columnName}")
        };
    }

    public static string BuildSortKey(string columnName, string value)
    {
        return $"{columnName?.Trim() ?? ""}|{value?.Trim() ?? ""}";
    }

    public static int GetSortRank(
        IReadOnlyDictionary<string, int> sortMap,
        string columnName,
        string value)
    {
        var key = BuildSortKey(columnName, value);

        return sortMap.TryGetValue(key, out var sortOrder)
            ? sortOrder
            : int.MaxValue;
    }

    public static string GetSizeColumnName(string gender)
    {
        return gender switch
        {
            "Females" => "Size_Females",
            "Males" => "Size_Males",
            _ => "Size"
        };
    }

    public static Dictionary<string, int> BuildSortMap(
        IEnumerable<LotSortOrderDto> sortOrders)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var so in sortOrders)
        {
            var key = BuildSortKey(so.ColumnName, so.Value);
            map.TryAdd(key, so.SortOrder);
        }

        return map;
    }
}
