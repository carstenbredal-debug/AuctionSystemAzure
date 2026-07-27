using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Services;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Functions.Services;
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

    // Per farmer x Type x Gender x Group: sold/unsold skins + sold value (lot has a hammer
    // result). Shared by the RAW Excel export and the Sales Notes PDF so they always agree.
    private async Task<List<BreakdownRow>> LoadBreakdownRowsAsync(string auctionNumber, int auctionId)
    {
        var skinsTable = $"auction.[{auctionNumber}.Skins]";
        var lotsTable = $"auction.[{auctionNumber}.Lots]";
        var rows = new List<BreakdownRow>();

        await using var conn = new SqlConnection(_db.Database.GetConnectionString()!);
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
        return rows;
    }

    // One PDF with a SALES NOTE per farmer (sold Type/Gender/Group breakdown + VAT), same
    // data as the RAW Excel export. Presentation only — settlement documents are posted in BC.
    [RequireRole("Admin")]
    [Function("ExportSalesNotesPdf")]
    public async Task<HttpResponseData> ExportSalesNotesPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/sales-notes-pdf")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (!int.TryParse(query["auctionId"], out var auctionId))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var auction = await _db.Auctions.FindAsync(auctionId);
        if (auction == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var rows = await LoadBreakdownRowsAsync(auction.AuctionNumber, auctionId);

        var farmers = await _db.Farmers.AsNoTracking()
            .Where(f => f.FarmerGUID != null)
            .ToListAsync();
        var farmerByGuid = farmers
            .GroupBy(f => f.FarmerGUID!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var notes = rows
            .Where(r => r.Sold > 0)
            .GroupBy(r => r.FarmerGuid.HasValue ? r.FarmerGuid.Value.ToString() : (r.FarmerName ?? ""))
            .Select(g =>
            {
                var first = g.First();
                var master = first.FarmerGuid.HasValue ? farmerByGuid.GetValueOrDefault(first.FarmerGuid.Value) : null;
                return new SalesNotesPdfService.FarmerNote
                {
                    FarmerNumber = master?.FarmerNumber ?? "",
                    Name = master?.Name ?? first.FarmerName ?? "(unknown)",
                    Name2 = master?.Name2,
                    AddressLine1 = master?.AddressLine1,
                    AddressLine2 = master?.AddressLine2,
                    PostalCode = master?.PostalCode,
                    City = master?.City,
                    Country = master?.Country,
                    VatRegistrationNo = master?.VatRegistrationNo,
                    VatRate = VatRules.RateFor(master?.VatBusPostingGroup),
                    Lines = g.Where(r => r.Sold > 0)
                        .Select(r => new SalesNotesPdfService.FarmerNote.Line
                        {
                            SalesType = r.SalesType,
                            Gender = r.Gender,
                            Group = r.Group,
                            SoldSkins = r.Sold,
                            SoldValue = r.SoldValue
                        }).ToList()
                };
            })
            .OrderBy(n => n.FarmerNumber)
            .ToList();

        if (notes.Count == 0)
        {
            var empty = req.CreateResponse(System.Net.HttpStatusCode.NotFound);
            await empty.WriteStringAsync("No farmers with sold skins found for this auction.");
            return empty;
        }

        var pdf = SalesNotesPdfService.Generate(notes, auction.AuctionNumber, DateTime.UtcNow.Date);

        var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/pdf");
        resp.Headers.Add("Content-Disposition", $"inline; filename=\"SalesNotes-{auction.AuctionNumber}.pdf\"");
        await resp.WriteBytesAsync(pdf);
        return resp;
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
        var rows = await LoadBreakdownRowsAsync(auction.AuctionNumber, auctionId);

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
            })
            .ToList();

        // --- Summary page first: totals across all farmers + the combined breakdown. VAT is summed
        // per farmer (rates differ by VAT group), so the summary reconciles with the farmer sheets.
        if (perFarmer.Count > 0)
        {
            var totDelivered = 0; var totSold = 0; var totUnsold = 0;
            decimal totSales = 0, totSalesVat = 0, totFee = 0, totFeeVat = 0;
            foreach (var g in perFarmer)
            {
                var gFirst = g.First();
                var gMaster = gFirst.FarmerGuid.HasValue ? farmerByGuid.GetValueOrDefault(gFirst.FarmerGuid.Value) : null;
                var gRate = VatRules.RateFor(gMaster?.VatBusPostingGroup);
                var gSold = g.Sum(r => r.Sold);
                var gSales = g.Sum(r => r.SoldValue);
                var gFee = gSold * feePerSkin;
                totDelivered += gFirst.FarmerGuid.HasValue ? deliveredByGuid.GetValueOrDefault(gFirst.FarmerGuid.Value) : 0;
                totSold += gSold;
                totUnsold += g.Sum(r => r.Unsold);
                totSales += gSales; totSalesVat += gSales * gRate;
                totFee += gFee; totFeeVat += gFee * gRate;
            }
            var totInAuction = totSold + totUnsold;
            var totMissing = totDelivered - totInAuction;
            var totAfregning = totSales - totFee;
            var totAfregningVat = totSalesVat - totFeeVat;

            usedSheetNames.Add("Summary");
            var sum = wb.AddWorksheet("Summary");
            sum.Cell(1, 1).Value = "Settlement — Summary (all farmers)";
            sum.Cell(1, 1).Style.Font.Bold = true;

            sum.Cell(3, 1).Value = "Delivered"; sum.Cell(3, 2).Value = totDelivered;
            sum.Cell(4, 1).Value = "In Auction"; sum.Cell(4, 2).Value = totInAuction;
            sum.Cell(6, 1).Value = "Missing Skins"; sum.Cell(6, 2).Value = totMissing;
            sum.Cell(8, 1).Value = "Sold"; sum.Cell(8, 2).Value = totSold;
            sum.Cell(9, 1).Value = "Unsold"; sum.Cell(9, 2).Value = totUnsold;

            sum.Cell(11, 2).Value = "Excluding VAT";
            sum.Cell(11, 3).Value = "VAT";
            sum.Cell(11, 4).Value = "Including VAT";
            sum.Range(11, 2, 11, 4).Style.Font.Bold = true;

            var sumFeeLabel = feePerSkin.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            sum.Cell(13, 1).Value = "Salesresult";
            sum.Cell(13, 2).Value = totSales;
            sum.Cell(13, 3).Value = totSalesVat;
            sum.Cell(13, 4).Value = totSales + totSalesVat;

            sum.Cell(15, 1).Value = $"Grading Fee {sumFeeLabel} Euro Cent";
            sum.Cell(15, 2).Value = totFee;
            sum.Cell(15, 3).Value = totFeeVat;
            sum.Cell(15, 4).Value = totFee + totFeeVat;

            sum.Cell(17, 1).Value = "Til Afregning";
            sum.Cell(17, 2).Value = totAfregning;
            sum.Cell(17, 3).Value = totAfregningVat;
            sum.Cell(17, 4).Value = totAfregning + totAfregningVat;
            sum.Range(17, 1, 17, 4).Style.Font.Bold = true;

            sum.Cell(19, 1).Value = "Still til Settle";
            sum.Cell(19, 2).Value = $"{totMissing} Skins at Average Price";

            sum.Cell(21, 1).Value = "Sales Result";
            sum.Cell(21, 1).Style.Font.Bold = true;

            sum.Cell(23, 1).Value = "Type";
            sum.Cell(23, 2).Value = "Gender";
            sum.Cell(23, 3).Value = "Group";
            sum.Cell(23, 4).Value = "Sold skins";
            sum.Cell(23, 5).Value = "Sold value (EUR)";
            sum.Cell(23, 6).Value = "Unsold skins";
            sum.Range(23, 1, 23, 6).Style.Font.Bold = true;

            var sr = 25;
            var combined = rows
                .GroupBy(x => (x.SalesType, x.Gender, x.Group))
                .OrderBy(x => x.Key.SalesType).ThenBy(x => x.Key.Gender).ThenBy(x => x.Key.Group);
            foreach (var c in combined)
            {
                sum.Cell(sr, 1).Value = c.Key.SalesType;
                sum.Cell(sr, 2).Value = c.Key.Gender;
                sum.Cell(sr, 3).Value = c.Key.Group;
                sum.Cell(sr, 4).Value = c.Sum(x => x.Sold);
                sum.Cell(sr, 5).Value = c.Sum(x => x.SoldValue);
                sum.Cell(sr, 6).Value = c.Sum(x => x.Unsold);
                sr++;
            }
            sr++;
            sum.Cell(sr, 3).Value = "Total";
            sum.Cell(sr, 4).Value = totSold;
            sum.Cell(sr, 5).Value = totSales;
            sum.Cell(sr, 6).Value = totUnsold;
            sum.Row(sr).Style.Font.Bold = true;

            sum.Column(5).Style.NumberFormat.Format = "#,##0.00";
            sum.Range(11, 2, 17, 4).Style.NumberFormat.Format = "#,##0.00";
            sum.Columns().AdjustToContents();
        }

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
