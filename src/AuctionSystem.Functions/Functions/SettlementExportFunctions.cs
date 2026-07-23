using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Services;
using AuctionSystem.Functions.Auth;
using ClosedXML.Excel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Functions.Functions;

// Settlement RAW export: one workbook, one sheet per farmer with skins in the auction.
// Sheet layout mirrors the agreed "Settlement RAW All" design: Delivered (dbo.FarmerDeliveries)
// vs In Auction vs Missing, Sold/Unsold, Salesresult -> Grading Fee -> Til Afregning with VAT
// columns (rate from the farmer's VAT Bus. Posting Group), and the Type/Gender/Group breakdown.
public class SettlementExportFunctions
{
    private readonly AuctionDbContext _db;

    public SettlementExportFunctions(AuctionDbContext db)
    {
        _db = db;
    }

    private sealed class BreakdownRow
    {
        public Guid? FarmerGuid;
        public string? FarmerName;
        public string SalesType = "";
        public string Gender = "";
        public string Group = "";
        public int Sold;
        public decimal SoldValue;
        public int Unsold;
    }

    [RequireRole("Admin")]
    [Function("ExportSettlementXlsx")]
    public async Task<HttpResponseData> ExportSettlementXlsx(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/export-settlement-xlsx")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (!int.TryParse(query["auctionId"], out var auctionId))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var auction = await _db.Auctions.FindAsync(auctionId);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var skinsTable = $"auction.[{auction.AuctionNumber}.Skins]";
        var lotsTable = $"auction.[{auction.AuctionNumber}.Lots]";
        var connStr = _db.Database.GetConnectionString()!;

        // Grading fee per sold skin — parameter override, default 0.70 EUR.
        var feePerSkin = 0.70m;
        var feeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "GradingFeePerSkin");
        if (feeParam != null && decimal.TryParse(feeParam.Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var fp))
            feePerSkin = fp;

        // Per farmer x Type x Gender x Group: sold/unsold skins + sold value (lot has a hammer result).
        var rows = new List<BreakdownRow>();
        await using (var conn = new SqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($@"
                WITH BoxLot AS (
                    SELECT l.LotNumber, l.SalesType, l.Gender, l.[Group],
                           TRY_CAST(s.value AS INT) AS BoxNumber
                    FROM {lotsTable} l
                    CROSS APPLY STRING_SPLIT(l.IncludedBoxNumbers, ',') s
                    WHERE TRY_CAST(s.value AS INT) IS NOT NULL
                ),
                Res AS (
                    SELECT r.LotNumber, r.PriceEur
                    FROM auction.AuctionResults r
                    WHERE r.AuctionId = @auctionId
                )
                SELECT sk.farmerGUID, MAX(sk.Farmer) AS Farmer,
                       ISNULL(bl.SalesType, '') AS SalesType,
                       ISNULL(bl.Gender, '') AS Gender,
                       ISNULL(bl.[Group], '') AS [Group],
                       SUM(CASE WHEN res.LotNumber IS NOT NULL THEN 1 ELSE 0 END) AS Sold,
                       SUM(CASE WHEN res.LotNumber IS NOT NULL THEN res.PriceEur ELSE 0 END) AS SoldValue,
                       SUM(CASE WHEN res.LotNumber IS NULL THEN 1 ELSE 0 END) AS Unsold
                FROM {skinsTable} sk
                JOIN BoxLot bl ON bl.BoxNumber = sk.BoxNumber
                LEFT JOIN Res res ON res.LotNumber = bl.LotNumber
                WHERE sk.IsActive = 1
                GROUP BY sk.farmerGUID, bl.SalesType, bl.Gender, bl.[Group]", conn);
            cmd.Parameters.AddWithValue("@auctionId", auctionId);
            cmd.CommandTimeout = 120;

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new BreakdownRow
                {
                    FarmerGuid = reader.IsDBNull(0) ? null : reader.GetGuid(0),
                    FarmerName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    SalesType = reader.GetString(2),
                    Gender = reader.GetString(3),
                    Group = reader.GetString(4),
                    Sold = reader.GetInt32(5),
                    SoldValue = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                    Unsold = reader.GetInt32(7)
                });
            }
        }

        // Delivered skins per farmer (support table loaded from the portal).
        var deliveredByGuid = new Dictionary<Guid, int>();
        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT FarmerId, SUM(ISNULL(SkinCount, 0)) FROM dbo.FarmerDeliveries GROUP BY FarmerId", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                deliveredByGuid[reader.GetGuid(0)] = reader.GetInt32(1);
        }
        catch { /* table may not exist yet — Delivered shows 0 */ }

        // Farmer master for number/name/VAT group.
        var farmers = await _db.Farmers.AsNoTracking()
            .Where(f => f.FarmerGUID != null)
            .Select(f => new { f.FarmerGUID, f.FarmerNumber, f.Name, f.VatBusPostingGroup })
            .ToListAsync();
        var farmerByGuid = farmers
            .GroupBy(f => f.FarmerGUID!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        using var wb = new XLWorkbook();
        var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var perFarmer = rows
            .GroupBy(r => r.FarmerGuid.HasValue ? r.FarmerGuid.Value.ToString() : (r.FarmerName ?? ""))
            .OrderBy(g =>
            {
                var first = g.First();
                return first.FarmerGuid.HasValue && farmerByGuid.TryGetValue(first.FarmerGuid.Value, out var fm)
                    ? fm.FarmerNumber : first.FarmerName ?? "";
            });

        foreach (var farmerGroup in perFarmer)
        {
            var first = farmerGroup.First();
            var master = first.FarmerGuid.HasValue ? farmerByGuid.GetValueOrDefault(first.FarmerGuid.Value) : null;
            var farmerNumber = master?.FarmerNumber ?? "";
            var farmerName = master?.Name ?? first.FarmerName ?? "(unknown)";

            var sold = farmerGroup.Sum(r => r.Sold);
            var unsold = farmerGroup.Sum(r => r.Unsold);
            var inAuction = sold + unsold;
            var delivered = first.FarmerGuid.HasValue ? deliveredByGuid.GetValueOrDefault(first.FarmerGuid.Value) : 0;
            var missing = delivered - inAuction;
            var salesResult = farmerGroup.Sum(r => r.SoldValue);
            var gradingFee = sold * feePerSkin;
            var tilAfregning = salesResult - gradingFee;
            var vatRate = VatRules.RateFor(master?.VatBusPostingGroup);

            // Sheet name: farmer number (unique, <=31 chars, no invalid chars).
            var baseName = string.IsNullOrWhiteSpace(farmerNumber)
                ? new string(farmerName.Where(char.IsLetterOrDigit).Take(20).ToArray())
                : farmerNumber;
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "farmer";
            var sheetName = baseName;
            var suffix = 2;
            while (!usedSheetNames.Add(sheetName)) sheetName = $"{baseName}_{suffix++}";

            var ws = wb.AddWorksheet(sheetName.Length > 31 ? sheetName[..31] : sheetName);

            ws.Cell(1, 1).Value = "Settlement";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(3, 1).Value = "Farmer Number"; ws.Cell(3, 2).Value = farmerNumber;
            ws.Cell(4, 1).Value = "Farmer Name"; ws.Cell(4, 2).Value = farmerName;

            ws.Cell(6, 1).Value = "Delivered"; ws.Cell(6, 2).Value = delivered;
            ws.Cell(7, 1).Value = "In Auction"; ws.Cell(7, 2).Value = inAuction;
            ws.Cell(9, 1).Value = "Missing Skins"; ws.Cell(9, 2).Value = missing;

            ws.Cell(11, 1).Value = "Sold"; ws.Cell(11, 2).Value = sold;
            ws.Cell(12, 1).Value = "Unsold"; ws.Cell(12, 2).Value = unsold;

            ws.Cell(14, 2).Value = "Excluding VAT";
            ws.Cell(14, 3).Value = "VAT";
            ws.Cell(14, 4).Value = "Including VAT";
            ws.Range(14, 2, 14, 4).Style.Font.Bold = true;

            var feeLabel = feePerSkin.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            ws.Cell(16, 1).Value = "Salesresult";
            ws.Cell(16, 2).Value = salesResult;
            ws.Cell(16, 3).Value = salesResult * vatRate;
            ws.Cell(16, 4).Value = salesResult * (1 + vatRate);

            ws.Cell(18, 1).Value = $"Grading Fee {feeLabel} Euro Cent";
            ws.Cell(18, 2).Value = gradingFee;
            ws.Cell(18, 3).Value = gradingFee * vatRate;
            ws.Cell(18, 4).Value = gradingFee * (1 + vatRate);

            ws.Cell(20, 1).Value = "Til Afregning";
            ws.Cell(20, 2).Value = tilAfregning;
            ws.Cell(20, 3).Value = tilAfregning * vatRate;
            ws.Cell(20, 4).Value = tilAfregning * (1 + vatRate);
            ws.Range(20, 1, 20, 4).Style.Font.Bold = true;

            ws.Cell(22, 1).Value = "Still til Settle";
            ws.Cell(22, 2).Value = $"{missing} Skins at Average Price";

            ws.Cell(24, 1).Value = "Sales Result";
            ws.Cell(24, 1).Style.Font.Bold = true;

            ws.Cell(26, 1).Value = "Type";
            ws.Cell(26, 2).Value = "Gender";
            ws.Cell(26, 3).Value = "Group";
            ws.Cell(26, 4).Value = "Sold skins";
            ws.Cell(26, 5).Value = "Sold value (EUR)";
            ws.Cell(26, 6).Value = "Unsold skins";
            ws.Range(26, 1, 26, 6).Style.Font.Bold = true;

            var r = 28;
            foreach (var row in farmerGroup.OrderBy(x => x.SalesType).ThenBy(x => x.Gender).ThenBy(x => x.Group))
            {
                ws.Cell(r, 1).Value = row.SalesType;
                ws.Cell(r, 2).Value = row.Gender;
                ws.Cell(r, 3).Value = row.Group;
                ws.Cell(r, 4).Value = row.Sold;
                ws.Cell(r, 5).Value = row.SoldValue;
                ws.Cell(r, 6).Value = row.Unsold;
                r++;
            }

            r++;
            ws.Cell(r, 3).Value = "Total";
            ws.Cell(r, 4).Value = sold;
            ws.Cell(r, 5).Value = salesResult;
            ws.Cell(r, 6).Value = unsold;
            ws.Row(r).Style.Font.Bold = true;

            ws.Column(5).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(14, 2, 20, 4).Style.NumberFormat.Format = "#,##0.00";
            ws.Columns().AdjustToContents();
        }

        if (!wb.Worksheets.Any())
            wb.AddWorksheet("Empty").Cell(1, 1).Value = "No farmer skins found for this auction.";

        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        resp.Headers.Add("Content-Disposition", $"attachment; filename=\"Settlement-RAW-{auction.AuctionNumber}.xlsx\"");
        await resp.WriteBytesAsync(ms.ToArray());
        return resp;
    }
}
