using AuctionSystem.Functions.Models;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

public class CatalogBuildService
{
    private readonly ILogger<CatalogBuildService> _logger;

    public CatalogBuildService(ILogger<CatalogBuildService> logger)
    {
        _logger = logger;
    }

    public CatalogBuildResult BuildCatalogLots(
        IEnumerable<GeneratedLotDto> lots,
        IEnumerable<StringDefinitionDto> stringDefinitions,
        IEnumerable<LotGroupOrderDto> groupOrders,
        IEnumerable<LotSortOrderDto> sortOrders,
        IEnumerable<CatalogNumberRuleDto> catalogNumberRules,
        Guid runId)
    {
        var result = new CatalogBuildResult();
        var lotList = lots.ToList();

        var stringColumns = stringDefinitions
            .Where(x => x.IsActive)
            .Select(x => x.ColumnName)
            .ToList();

        var orderedColumns = groupOrders
            .OrderBy(x => x.GroupOrder)
            .Select(x => x.ColumnName)
            .ToList();

        var sortMap = LotPropertyHelper.BuildSortMap(sortOrders);

        var ruleMap = catalogNumberRules
            .Where(x => x.IsActive)
            .ToDictionary(
                x => BuildRuleKey(x.SalesType, x.Gender, x.Group),
                x => x.StartNumber,
                StringComparer.OrdinalIgnoreCase
            );

        var strings = lotList
            .GroupBy(lot => BuildStringKey(lot, stringColumns))
            .Select(g => new CatalogStringGroup
            {
                Representative = g.First(),
                Lots = g.ToList()
            })
            .ToList();

        var sortedStrings = SortStrings(strings, orderedColumns, sortMap);

        var nextNumberByRule = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase
        );

        var catalogSortOrder = 1;

        foreach (var catalogString in sortedStrings)
        {
            var representative = catalogString.Representative;

            var ruleKey = BuildRuleKey(
                representative.SalesType,
                representative.Gender,
                representative.Group
            );

            if (!ruleMap.TryGetValue(ruleKey, out var prefix))
            {
                _logger.LogWarning("Skipping catalog string: Missing CatalogNumberRule for {RuleKey}", ruleKey);

                foreach (var lot in catalogString.Lots)
                {
                    result.SkippedGroups.Add(new SkippedGroupDto
                    {
                        RunID = runId,
                        Reason = "Missing CatalogNumberRule: " + ruleKey,
                        SalesType = lot.SalesType,
                        Group = lot.Group,
                        Gender = lot.Gender,
                        Size = lot.Size,
                        Color = lot.Color,
                        Quality = lot.Quality,
                        Clarity = lot.Clarity,
                        HairLength = lot.HairLength,
                        Damages = lot.Damages,
                        BoxCount = lot.BoxCount,
                        ShowlotCount = lot.IsShow == "Yes" ? 1 : 0,
                        TotalSkins = lot.TotalSkins,
                        BoxNumbers = lot.IncludedBoxNumbers
                    });
                }

                continue;
            }

            if (!nextNumberByRule.ContainsKey(ruleKey))
            {
                nextNumberByRule[ruleKey] = prefix * 1000 + 1;
            }

            var orderedLotsInString = catalogString.Lots
                .OrderByDescending(x => x.IsShow == "Yes")
                .ThenByDescending(x => x.TotalSkins)
                .ThenByDescending(x => x.BoxCount)
                .ThenBy(x => x.ShowlotBoxNumber)
                .ToList();

            var stringNumber = nextNumberByRule[ruleKey];

            foreach (var lot in orderedLotsInString)
            {
                var lotNumber = nextNumberByRule[ruleKey];

                result.CatalogLots.Add(new CatalogLotDto
                {
                    LotUniqueID = lot.UniqueID,

                    StringNumber = stringNumber,
                    LotNumber = lotNumber,
                    CatalogSortOrder = catalogSortOrder++,

                    IsShow = lot.IsShow,

                    SalesType = lot.SalesType,
                    Gender = lot.Gender,
                    Group = lot.Group,
                    HairLength = lot.HairLength,
                    Size = lot.Size,
                    Quality = lot.Quality,
                    Color = lot.Color,
                    Clarity = lot.Clarity,
                    Damages = lot.Damages,

                    IncludedBoxNumbers = lot.IncludedBoxNumbers,
                    BoxCount = lot.BoxCount,
                    TotalSkins = lot.TotalSkins
                });

                nextNumberByRule[ruleKey]++;
            }
        }

        _logger.LogInformation("Catalog build complete: {CatalogLotCount} catalog lots, {SkippedCount} skipped",
            result.CatalogLots.Count, result.SkippedGroups.Count);

        return result;
    }

    private static List<CatalogStringGroup> SortStrings(
        List<CatalogStringGroup> strings,
        List<string> orderedColumns,
        IReadOnlyDictionary<string, int> sortMap)
    {
        IOrderedEnumerable<CatalogStringGroup>? ordered = null;

        foreach (var columnName in orderedColumns)
        {
            Func<CatalogStringGroup, int> selector = item =>
            {
                var lot = item.Representative;

                var effectiveColumn =
                    columnName == "Size"
                        ? LotPropertyHelper.GetSizeColumnName(lot.Gender)
                        : columnName;

                var value = GetLotValue(lot, columnName);

                return LotPropertyHelper.GetSortRank(sortMap, effectiveColumn, value);
            };

            ordered = ordered == null
                ? strings.OrderBy(selector)
                : ordered.ThenBy(selector);
        }

        return (ordered ?? strings.OrderBy(x => 0)).ToList();
    }

    private static string BuildStringKey(
        GeneratedLotDto lot,
        List<string> stringColumns)
    {
        return string.Join("|",
            stringColumns.Select(column => GetLotValue(lot, column))
        );
    }

    private static string GetLotValue(GeneratedLotDto lot, string columnName)
    {
        return LotPropertyHelper.GetPropertyValue(
            columnName,
            lot.SalesType, lot.Gender, lot.Group,
            lot.HairLength, lot.Size, lot.Quality,
            lot.Color, lot.Clarity, lot.Damages);
    }

    private static string BuildRuleKey(
        string salesType,
        string gender,
        string group)
    {
        return $"{salesType?.Trim() ?? ""}|{gender?.Trim() ?? ""}|{group?.Trim() ?? ""}";
    }

    private sealed class CatalogStringGroup
    {
        public GeneratedLotDto Representative { get; set; } = new();
        public List<GeneratedLotDto> Lots { get; set; } = new();
    }
}
