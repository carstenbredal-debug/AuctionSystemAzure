using AuctionSystem.Functions.Models;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

public class LotGenerationService
{
    private readonly ILogger<LotGenerationService> _logger;

    public LotGenerationService(ILogger<LotGenerationService> logger)
    {
        _logger = logger;
    }

    public LotGenerationResult GenerateLots(
        IEnumerable<BoxRow> boxes,
        IEnumerable<LotSizeRuleDto> rules,
        IEnumerable<LotGroupOrderDto> groupOrders,
        IEnumerable<LotSortOrderDto> sortOrders)
    {
        var runId = Guid.NewGuid();
        var result = new LotGenerationResult { RunId = runId };

        var activeBoxes = boxes.ToList();
        var sortOrderList = sortOrders.ToList();

        var missingSortKeys = FindMissingSortMappings(activeBoxes, sortOrderList);

        var orderedColumns = groupOrders
            .OrderBy(x => x.GroupOrder)
            .Select(x => x.ColumnName)
            .ToList();

        var sortMap = LotPropertyHelper.BuildSortMap(sortOrderList);

        var groups = activeBoxes.GroupBy(box =>
            string.Join("|", orderedColumns.Select(col => GetBoxValue(box, col)))
        );

        foreach (var group in groups)
        {
            var groupBoxes = group.ToList();
            var representative = groupBoxes.First();

            var showlots = groupBoxes
                .Where(x => x.BoxType == "Showlot")
                .ToList();

            var groupMissingSortKeys = groupBoxes
                .SelectMany(box => GetMissingSortKeysForBox(box, missingSortKeys))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (groupMissingSortKeys.Any())
            {
                _logger.LogWarning(
                    "Skipping group: Missing LotSortOrder for {Keys}",
                    string.Join(", ", groupMissingSortKeys));

                result.SkippedGroups.Add(
                    CreateSkippedGroup(
                        runId,
                        "Missing LotSortOrder: " + string.Join(", ", groupMissingSortKeys),
                        representative,
                        groupBoxes,
                        showlots.Count
                    )
                );

                continue;
            }

            if (showlots.Count == 0)
            {
                _logger.LogWarning("Skipping group: Missing showlot for {SalesType}/{Gender}/{Group}",
                    representative.SalesType, representative.Gender, representative.Group);

                result.SkippedGroups.Add(
                    CreateSkippedGroup(runId, "Missing showlot", representative, groupBoxes, showlots.Count)
                );

                continue;
            }

            if (showlots.Count > 1)
            {
                _logger.LogWarning("Skipping group: Multiple showlots ({Count}) for {SalesType}/{Gender}/{Group}",
                    showlots.Count, representative.SalesType, representative.Gender, representative.Group);

                result.SkippedGroups.Add(
                    CreateSkippedGroup(runId, "Multiple showlots", representative, groupBoxes, showlots.Count)
                );

                continue;
            }

            var rule = FindRule(
                representative.Gender,
                representative.Size,
                rules
            );

            if (rule == null)
            {
                _logger.LogWarning("Skipping group: Missing LotSizeRule for {Gender}/{Size}",
                    representative.Gender, representative.Size);

                result.SkippedGroups.Add(
                    CreateSkippedGroup(runId, "Missing LotSizeRule", representative, groupBoxes, showlots.Count)
                );

                continue;
            }

            var showlot = showlots.Single();

            var storageBoxes = SortBoxesInsideGroup(
                groupBoxes.Where(x => x.BoxType != "Showlot"),
                orderedColumns,
                sortMap
            );

            var firstLotBoxes = new List<BoxRow> { showlot };
            var storageBoxCount = 0;
            var currentSkins = showlot.Skins;
            var consumed = new HashSet<int>();

            for (var i = 0; i < storageBoxes.Count; i++)
            {
                var box = storageBoxes[i];
                var potentialBoxes = storageBoxCount + 1;
                var potentialSkins = currentSkins + box.Skins;

                if (potentialBoxes <= rule.MaxBoxes
                    && potentialSkins <= rule.MaxLotSizeInclShowlot)
                {
                    firstLotBoxes.Add(box);
                    consumed.Add(i);
                    storageBoxCount++;
                    currentSkins = potentialSkins;
                }

                if (storageBoxCount >= rule.MaxBoxes)
                    break;
            }

            var remainingBoxes = storageBoxes
                .Where((_, i) => !consumed.Contains(i))
                .ToList();

            result.Lots.Add(
                CreateLot(firstLotBoxes, "Yes", showlot.BoxNumber)
            );

            var startIndex = 0;

            while (startIndex < remainingBoxes.Count)
            {
                var lotBoxes = new List<BoxRow>();
                var lotSkins = 0;

                while (startIndex < remainingBoxes.Count
                    && lotBoxes.Count < rule.MaxBoxes
                    && lotSkins + remainingBoxes[startIndex].Skins <= rule.MaxLotSizeExclShowlot)
                {
                    var box = remainingBoxes[startIndex];
                    lotBoxes.Add(box);
                    lotSkins += box.Skins;
                    startIndex++;
                }

                if (!lotBoxes.Any() && startIndex < remainingBoxes.Count)
                {
                    lotBoxes.Add(remainingBoxes[startIndex]);
                    startIndex++;
                }

                result.Lots.Add(
                    CreateLot(lotBoxes, "No", showlot.BoxNumber)
                );
            }
        }

        _logger.LogInformation("Lot generation complete: {LotCount} lots, {SkippedCount} skipped groups",
            result.Lots.Count, result.SkippedGroups.Count);

        return result;
    }

    private static List<BoxRow> SortBoxesInsideGroup(
        IEnumerable<BoxRow> boxes,
        List<string> orderedColumns,
        IReadOnlyDictionary<string, int> sortMap)
    {
        IOrderedEnumerable<BoxRow>? orderedBoxes = null;

        foreach (var columnName in orderedColumns)
        {
            Func<BoxRow, int> selector = box =>
            {
                var effectiveColumnName =
                    columnName == "Size"
                        ? LotPropertyHelper.GetSizeColumnName(box.Gender)
                        : columnName;

                var value = GetBoxValue(box, columnName);

                return LotPropertyHelper.GetSortRank(sortMap, effectiveColumnName, value);
            };

            orderedBoxes = orderedBoxes == null
                ? boxes.OrderBy(selector)
                : orderedBoxes.ThenBy(selector);
        }

        return (orderedBoxes ?? boxes.OrderBy(x => x.BoxNumber))
            .ThenByDescending(x => x.Skins)
            .ThenBy(x => x.BoxNumber)
            .ToList();
    }

    private static HashSet<string> FindMissingSortMappings(
        IEnumerable<BoxRow> boxes,
        IEnumerable<LotSortOrderDto> sortOrders)
    {
        var lookup = sortOrders
            .Select(x => LotPropertyHelper.BuildSortKey(x.ColumnName, x.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var box in boxes)
        {
            CheckValue("SalesType", box.SalesType);
            CheckValue("Gender", box.Gender);
            CheckValue("Group", box.Group);
            CheckValue("HairLength", box.HairLength);
            CheckValue(LotPropertyHelper.GetSizeColumnName(box.Gender), box.Size);
            CheckValue("Quality", box.Quality);
            CheckValue("Color", box.Color);
            CheckValue("Clarity", box.Clarity);
            CheckValue("Damages", box.Damages);
        }

        return missing;

        void CheckValue(string columnName, string value)
        {
            var key = LotPropertyHelper.BuildSortKey(columnName, value);

            if (!lookup.Contains(key))
                missing.Add(key);
        }
    }

    private static List<string> GetMissingSortKeysForBox(
        BoxRow box,
        HashSet<string> missingSortKeys)
    {
        var result = new List<string>();

        CheckValue("SalesType", box.SalesType);
        CheckValue("Gender", box.Gender);
        CheckValue("Group", box.Group);
        CheckValue("HairLength", box.HairLength);
        CheckValue(LotPropertyHelper.GetSizeColumnName(box.Gender), box.Size);
        CheckValue("Quality", box.Quality);
        CheckValue("Color", box.Color);
        CheckValue("Clarity", box.Clarity);
        CheckValue("Damages", box.Damages);

        return result;

        void CheckValue(string columnName, string value)
        {
            var key = LotPropertyHelper.BuildSortKey(columnName, value);

            if (missingSortKeys.Contains(key))
                result.Add(key);
        }
    }

    private static string GetBoxValue(BoxRow box, string columnName)
    {
        return LotPropertyHelper.GetPropertyValue(
            columnName,
            box.SalesType, box.Gender, box.Group,
            box.HairLength, box.Size, box.Quality,
            box.Color, box.Clarity, box.Damages);
    }

    private static LotSizeRuleDto? FindRule(
        string gender,
        string size,
        IEnumerable<LotSizeRuleDto> rules)
    {
        return rules
            .Where(x =>
                x.IsActive
                &&
                string.Equals(x.Gender, gender, StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(x.Size, size, StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(x => x.Priority)
            .FirstOrDefault();
    }

    private static GeneratedLotDto CreateLot(
        List<BoxRow> boxes,
        string isShow,
        int? showlotBoxNumber)
    {
        var first = boxes.First();

        return new GeneratedLotDto
        {
            UniqueID = Guid.NewGuid(),

            IsShow = isShow,
            ShowlotBoxNumber = showlotBoxNumber,

            SalesType = first.SalesType,
            Group = first.Group,
            Gender = first.Gender,
            Size = first.Size,
            Color = first.Color,
            Quality = first.Quality,
            Clarity = first.Clarity,
            HairLength = first.HairLength,
            Damages = first.Damages,

            IncludedBoxNumbers = string.Join(", ", boxes.Select(x => x.BoxNumber)),
            BoxCount = boxes.Count,
            TotalSkins = boxes.Sum(x => x.Skins)
        };
    }

    private static SkippedGroupDto CreateSkippedGroup(
        Guid runId,
        string reason,
        BoxRow representative,
        List<BoxRow> boxes,
        int showlotCount)
    {
        return new SkippedGroupDto
        {
            RunID = runId,
            Reason = reason,

            SalesType = representative.SalesType,
            Group = representative.Group,
            Gender = representative.Gender,
            Size = representative.Size,
            Color = representative.Color,
            Quality = representative.Quality,
            Clarity = representative.Clarity,
            HairLength = representative.HairLength,
            Damages = representative.Damages,

            BoxCount = boxes.Count,
            ShowlotCount = showlotCount,
            TotalSkins = boxes.Sum(x => x.Skins),
            BoxNumbers = string.Join(", ", boxes.Select(x => x.BoxNumber))
        };
    }
}
