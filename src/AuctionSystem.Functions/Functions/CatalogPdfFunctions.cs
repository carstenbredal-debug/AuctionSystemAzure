using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Auth;
using AuctionSystem.Functions.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Net;
using System.Text.RegularExpressions;

namespace AuctionSystem.Functions.Functions;

public class CatalogPdfFunctions
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CatalogPdfFunctions> _logger;
    private const string FontName = "PPPangramSans";
    private static bool _fontsRegistered;

    // One consistent rule weight/colour for every data-row line (single rows and multi-lot group
    // boxes) so the catalogue doesn't mix heavy black boxes with faint grey row separators.
    private const float RowLineWidth = 0.75f;
    private const float StringBorderWidth = RowLineWidth * 2;   // the box around a multi-lot string — 2x the row lines
    private const float DescriptionFontSize = 8f;               // slightly smaller — the description shows 2 stacked lines
    private const float ColumnDividerWidth = 0.5f;              // thin vertical lines between columns, through every lot
    private static readonly Color RowLineColor = Colors.Grey.Medium;

    public CatalogPdfFunctions(
        IConfiguration configuration,
        ILogger<CatalogPdfFunctions> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GetCatalogConnectionString()
    {
        return _configuration["SqlConnectionString"]
            ?? _configuration["Values:SqlConnectionString"]
            ?? "";
    }

    // See CatalogApiFunctions: auctionNumber is interpolated into a table name, so it must be
    // whitelisted before use. Anything invalid falls back to the live (active-auction) catalog.
    private static readonly Regex AuctionNumberPattern = new("^[A-Za-z0-9]{1,20}$", RegexOptions.Compiled);

    private static string GetCatalogTable(System.Collections.Specialized.NameValueCollection query)
    {
        var auctionNumber = query["auctionNumber"];
        if (!string.IsNullOrEmpty(auctionNumber) && AuctionNumberPattern.IsMatch(auctionNumber))
            return $"auction.[{auctionNumber}.Lots]";
        return "auction.cataloglots";
    }

    // activeAuction=true (no explicit auctionNumber) -> read the active auction's snapshot. Status 1 = Active.
    private static async Task ResolveActiveAuctionIntoQueryAsync(System.Collections.Specialized.NameValueCollection query, SqlConnection connection)
    {
        if (!string.IsNullOrEmpty(query["auctionNumber"])) return;
        if (!string.Equals(query["activeAuction"], "true", StringComparison.OrdinalIgnoreCase)) return;
        var num = await connection.ExecuteScalarAsync<string?>(
            "SELECT TOP 1 AuctionNumber FROM auction.Auctions WHERE Status = 1 ORDER BY Id DESC");
        if (!string.IsNullOrEmpty(num)) query["auctionNumber"] = num;
    }

    private static void RegisterFonts()
    {
        if (_fontsRegistered) return;

        var basePath = AppContext.BaseDirectory;

        var mediumPath = Path.Combine(basePath, "Assets", "PPPangramSans-Medium.otf");
        var boldPath = Path.Combine(basePath, "Assets", "PPPangramSans-Bold.otf");

        if (File.Exists(mediumPath))
            using (var stream = File.OpenRead(mediumPath))
                QuestPDF.Drawing.FontManager.RegisterFont(stream);

        if (File.Exists(boldPath))
            using (var stream = File.OpenRead(boldPath))
                QuestPDF.Drawing.FontManager.RegisterFont(stream);

        _fontsRegistered = true;
    }

    // Customer catalogue PDF (no prices). Public; auctionNumber is whitelisted (GetCatalogTable) and all
    // filters are parameterized.
    [AllowAnonymous]
    [Function("GenerateCatalogPdf")]
    public Task<HttpResponseData> GeneratePdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/pdf")] HttpRequestData req)
        => RenderAsync(req, allowAuc: false);

    // Auctioneer catalogue PDF (estimated price + remarks). Admin-only — the priced variant is reachable
    // ONLY through this endpoint, never the public one.
    [RequireRole("Admin")]
    [Function("GenerateAucCatalogPdf")]
    public Task<HttpResponseData> GenerateAucPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/pdf-auc")] HttpRequestData req)
        => RenderAsync(req, allowAuc: true);

    private async Task<HttpResponseData> RenderAsync(HttpRequestData req, bool allowAuc)
    {
        try
        {
            QuestPDF.Settings.License = LicenseType.Community;
            RegisterFonts();

            var connectionString = GetCatalogConnectionString();

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("Connection string missing.");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Connection string missing.");
                return errorResponse;
            }

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

            await ResolveActiveAuctionIntoQueryAsync(query, connection);
            int.TryParse(query["draftId"], out var draftId);
            var sourceTable = draftId > 0 ? "auction.CatalogDraftLots" : GetCatalogTable(query);
            // Auctioneer variant (only the admin pdf-auc endpoint sets allowAuc): add estimated price +
            // remarks. CatalogDraftLots always has them; an auction snapshot only if it was catalog-imported,
            // so probe for the Estimate column first to stay safe on old-flow snapshots.
            bool includeAuc = false;
            if (allowAuc)
                includeAuc = draftId > 0
                    || await connection.ExecuteScalarAsync<int?>("SELECT COL_LENGTH(@t, 'Estimate')", new { t = sourceTable }) != null;

            var sql = @"
                SELECT
                    CatalogSortOrder,
                    StringNumber,
                    LotNumber,
                    IsShow,
                    SalesType,
                    Gender,
                    [Group],
                    HairLength,
                    Size,
                    Quality,
                    Color,
                    Clarity,
                    Damages,
                    IncludedBoxNumbers,
                    BoxCount,
                    TotalSkins,

                    COUNT(*) OVER (
                        PARTITION BY StringNumber
                    ) AS LotsInString,

                    ROW_NUMBER() OVER (
                        PARTITION BY StringNumber
                        ORDER BY CatalogSortOrder
                    ) AS LotSequenceInString,

                    SUM(TotalSkins) OVER (
                        PARTITION BY StringNumber
                    ) AS StringTotalSkins,

                    SUM(BoxCount) OVER (
                        PARTITION BY StringNumber
                    ) AS StringBoxCount
                " + (includeAuc ? ", ISNULL(Estimate, '') AS Estimate, ISNULL(Remarks, '') AS Remarks" : "") + @"
                FROM " + sourceTable + @"
                WHERE 1=1";

            var parameters = new DynamicParameters();
            if (draftId > 0)
            {
                // Frozen catalogue draft — source the frozen lots, no other filters.
                sql += " AND DraftId = @DraftId";
                parameters.Add("DraftId", draftId);
            }
            else
            {
                AddFilterParameters(query, ref sql, parameters);
            }

            sql += " ORDER BY CatalogSortOrder";

            var rows = (await connection.QueryAsync<CatalogPdfRow>(sql, parameters)).ToList();

            // Filter by farmer if specified
            var farmerName = query["farmerName"];
            var hasFarmerGuid = Guid.TryParse(query["farmerGuid"], out var farmerGuidVal);
            var isFarmerCatalog = hasFarmerGuid || !string.IsNullOrEmpty(farmerName);
            int.TryParse(query["brokerId"], out var brokerCatalogId);
            var isBrokerCatalog = !isFarmerCatalog && brokerCatalogId > 0;
            var lotSaleData = new Dictionary<int, LotSaleInfo>();

            if (isFarmerCatalog)
            {
                var auctionNumber = query["auctionNumber"];
                if (!string.IsNullOrEmpty(auctionNumber))
                {
                    var skinsTable = $"auction.[{auctionNumber}.Skins]";
                    var farmerBoxes = new HashSet<int>();
                    var farmerSkinsByBox = new Dictionary<int, int>();
                    // Prefer the stable farmerGUID; fall back to the legacy Farmer name.
                    var farmerClause = hasFarmerGuid ? "farmerGUID = @FarmerKey" : "Farmer = @FarmerKey";
                    object farmerKey = hasFarmerGuid ? farmerGuidVal : (object)(farmerName ?? "");
                    var boxSql = $"SELECT BoxNumber, COUNT(*) AS Cnt FROM {skinsTable} WHERE {farmerClause} GROUP BY BoxNumber";
                    var boxRows = await connection.QueryAsync<dynamic>(boxSql, new { FarmerKey = farmerKey });
                    foreach (var b in boxRows)
                    {
                        farmerBoxes.Add((int)b.BoxNumber);
                        farmerSkinsByBox[(int)b.BoxNumber] = (int)b.Cnt;
                    }

                    rows = rows.Where(r =>
                    {
                        var boxes = (r.IncludedBoxNumbers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0);
                        return boxes.Any(b => farmerBoxes.Contains(b));
                    }).ToList();

                    // Load lot results from AuctionResults (any typist result = hammer price)
                    var soldSql = @"SELECT ar.LotNumber, ar.PriceEur, CASE WHEN ar.SoldToBuyerId IS NOT NULL THEN 1 ELSE 0 END AS IsSoldToBuyer
                        FROM auction.AuctionResults ar
                        INNER JOIN auction.Lots l ON ar.LotNumber = l.LotNumber
                        INNER JOIN auction.Auctions a ON l.AuctionId = a.Id
                        WHERE a.AuctionNumber = @AuctionNumber";
                    var soldRows = await connection.QueryAsync<dynamic>(soldSql, new { AuctionNumber = auctionNumber });
                    var resultByLot = new Dictionary<int, (decimal Price, bool IsSoldToBuyer)>();
                    foreach (var s in soldRows)
                        resultByLot[(int)s.LotNumber] = ((decimal)s.PriceEur, (int)s.IsSoldToBuyer == 1);

                    // Load invoiced lot numbers (lot has an invoice line)
                    var invoicedSql = @"SELECT DISTINCT il.LotNumber, i.Status
                        FROM auction.InvoiceLines il
                        INNER JOIN auction.Invoices i ON il.InvoiceId = i.Id
                        WHERE i.IsCreditNote = 0";
                    var invoicedRows = await connection.QueryAsync<dynamic>(invoicedSql);
                    var invoicedLotSet = new HashSet<int>(invoicedRows.Select(x => (int)x.LotNumber));
                    // Paid = status 4 (Paid), 10 (ReleasedToShip), 11 (Packing)
                    var paidStatusValues = new[] { 4, 10, 11 };
                    var paidLotSet = new HashSet<int>(invoicedRows.Where(x => paidStatusValues.Contains((int)x.Status)).Select(x => (int)x.LotNumber));

                    // Compute sale data per catalog lot
                    foreach (var r in rows)
                    {
                        var boxes = (r.IncludedBoxNumbers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0).ToList();
                        var hasResult = resultByLot.ContainsKey(r.LotNumber);
                        var isInvoiced = invoicedLotSet.Contains(r.LotNumber);
                        var isPaid = paidLotSet.Contains(r.LotNumber);
                        decimal value = 0;
                        int farmerSkins = 0;
                        foreach (var bn in boxes)
                        {
                            if (farmerSkinsByBox.TryGetValue(bn, out var cnt))
                            {
                                farmerSkins += cnt;
                                if (hasResult)
                                    value += cnt * resultByLot[r.LotNumber].Price;
                            }
                        }
                        lotSaleData[r.LotNumber] = new LotSaleInfo
                        {
                            HasResult = hasResult,
                            IsInvoiced = isInvoiced,
                            IsPaid = isPaid,
                            PricePerSkin = hasResult ? resultByLot[r.LotNumber].Price : 0,
                            Value = value,
                            FarmerSkins = farmerSkins
                        };
                    }

                    // Re-scope the catalogue to the farmer: each lot's skin count becomes the farmer's count
                    // in that lot, and the per-string aggregates (count / sequence / string total) recompute
                    // over ONLY the farmer's lots (the rest were filtered out above). Without this the farmer
                    // PDF printed the whole lot's skin total instead of the farmer's share.
                    foreach (var strGrp in rows.GroupBy(r => r.StringNumber))
                    {
                        var ordered = strGrp.ToList();   // rows are already sorted by CatalogSortOrder
                        var stringTotal = ordered.Sum(r => lotSaleData[r.LotNumber].FarmerSkins);
                        for (int i = 0; i < ordered.Count; i++)
                        {
                            ordered[i].TotalSkins = lotSaleData[ordered[i].LotNumber].FarmerSkins;
                            ordered[i].LotsInString = ordered.Count;
                            ordered[i].LotSequenceInString = i + 1;
                            ordered[i].StringTotalSkins = stringTotal;
                        }
                    }
                }
            }
            else if (isBrokerCatalog)
            {
                // Broker catalogue: only the lots this broker WON in this auction, at full lot skins (the
                // broker buys the whole lot, unlike a farmer who sees only their share). Filter to those lots
                // and recompute the per-string aggregates over them.
                var auctionNumber = query["auctionNumber"];
                if (!string.IsNullOrEmpty(auctionNumber))
                {
                    var brokerSql = @"SELECT ar.LotNumber, ar.PriceEur
                        FROM auction.AuctionResults ar
                        INNER JOIN auction.Lots l ON ar.LotNumber = l.LotNumber
                        INNER JOIN auction.Auctions a ON l.AuctionId = a.Id
                        WHERE a.AuctionNumber = @AuctionNumber AND ar.BrokerId = @BrokerId";
                    var brokerRows = await connection.QueryAsync<dynamic>(brokerSql, new { AuctionNumber = auctionNumber, BrokerId = brokerCatalogId });
                    var brokerLotPrice = new Dictionary<int, decimal>();
                    foreach (var b in brokerRows)
                        brokerLotPrice[(int)b.LotNumber] = (decimal)b.PriceEur;

                    rows = rows.Where(r => brokerLotPrice.ContainsKey(r.LotNumber)).ToList();

                    foreach (var r in rows)
                    {
                        var price = brokerLotPrice[r.LotNumber];
                        lotSaleData[r.LotNumber] = new LotSaleInfo
                        {
                            HasResult = true,
                            PricePerSkin = price,
                            Value = r.TotalSkins * price,
                            FarmerSkins = r.TotalSkins   // full lot — the broker bought the whole lot
                        };
                    }

                    foreach (var strGrp in rows.GroupBy(r => r.StringNumber))
                    {
                        var ordered = strGrp.ToList();
                        var stringTotal = ordered.Sum(r => r.TotalSkins);   // full lot skins
                        for (int i = 0; i < ordered.Count; i++)
                        {
                            ordered[i].LotsInString = ordered.Count;
                            ordered[i].LotSequenceInString = i + 1;
                            ordered[i].StringTotalSkins = stringTotal;
                        }
                    }
                }
            }

            if (!rows.Any())
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("No lots found matching your filters.");
                return notFound;
            }

            var sections = rows
                .GroupBy(BuildSectionTitle)
                .ToList();

            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "kopenhagenfur-logo.png");

            byte[] pdfBytes = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(25);

                    page.DefaultTextStyle(x => x.FontFamily(FontName));

                    page.Header().Element(x => ComposeHeader(x, logoPath));

                    page.Content()
                        .Column(column =>
                        {
                            bool firstSection = true;

                            foreach (var section in sections)
                            {
                                if (!firstSection)
                                {
                                    column.Item().PageBreak();
                                }

                                firstSection = false;

                                column.Item()
                                    .Table(table =>
                                    {
                                        if (isFarmerCatalog)
                                        {
                                            table.ColumnsDefinition(columns =>
                                            {
                                                columns.ConstantColumn(70);   // Lots
                                                columns.ConstantColumn(55);   // Skins
                                                columns.RelativeColumn();     // Description
                                                columns.ConstantColumn(65);   // Price/Skin
                                                columns.ConstantColumn(75);   // Value
                                                columns.ConstantColumn(50);   // Status
                                            });
                                        }
                                        else
                                        {
                                            table.ColumnsDefinition(columns =>
                                            {
                                                columns.ConstantColumn(70);   // Lots
                                                columns.ConstantColumn(55);   // Skins
                                                columns.RelativeColumn();     // Description
                                                columns.ConstantColumn(45);   // Price (narrower, closer to Description)
                                                columns.ConstantColumn(125);  // Comments (more room)
                                            });
                                        }

                                        var colSpan = isFarmerCatalog ? (uint)6 : (uint)5;

                                        table.Header(header =>
                                        {
                                            header.Cell()
                                                .ColumnSpan(colSpan)
                                                .Element(SectionCell)
                                                .Text(section.Key)
                                                .FontSize(11)
                                                .Bold();

                                            AddColumnHeader(header, isFarmerCatalog);
                                        });

                                        var sectionRows = section
                                            .OrderBy(x => x.CatalogSortOrder)
                                            .ToList();

                                        int i = 0;
                                        while (i < sectionRows.Count)
                                        {
                                            var row = sectionRows[i];

                                            if (row.IsMultiLotString && row.LotSequenceInString == 1)
                                            {
                                                var groupRows = new List<CatalogPdfRow>();
                                                var stringNum = row.StringNumber;
                                                int j = i;
                                                while (j < sectionRows.Count
                                                    && sectionRows[j].IsMultiLotString
                                                    && sectionRows[j].StringNumber == stringNum)
                                                {
                                                    groupRows.Add(sectionRows[j]);
                                                    j++;
                                                }
                                                AddStringGroup(table, groupRows, isFarmerCatalog, lotSaleData);
                                                i = j;
                                            }
                                            else
                                            {
                                                bool nextIsStringStart = i + 1 < sectionRows.Count
                                                    && sectionRows[i + 1].IsMultiLotString
                                                    && sectionRows[i + 1].LotSequenceInString == 1;
                                                AddCatalogRow(table, row, nextIsStringStart, isFarmerCatalog, lotSaleData);
                                                i++;
                                            }
                                        }
                                    });
                            }
                        });

                    page.Footer().Element(ComposeFooter);
                });

            }).GeneratePdf();

            _logger.LogInformation("PDF generated: {Sections} sections, {Rows} rows",
                sections.Count, rows.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/pdf");
            response.Headers.Add("Content-Disposition", "attachment; filename=lot-catalog.pdf");
            await response.Body.WriteAsync(pdfBytes, 0, pdfBytes.Length);
            return response;
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // Catalog table not created yet (no lot generation has run) -> treat as no lots, not a 500.
            var none = req.CreateResponse(HttpStatusCode.NotFound);
            await none.WriteStringAsync("No catalogue available yet.");
            return none;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating PDF");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.ToString());
            return response;
        }
    }

    private static void AddFilterParameters(
        System.Collections.Specialized.NameValueCollection query,
        ref string sql,
        DynamicParameters parameters)
    {
        if (!string.IsNullOrEmpty(query["type"]))
        {
            sql += " AND SalesType = @SalesType";
            parameters.Add("SalesType", query["type"]);
        }
        if (!string.IsNullOrEmpty(query["gender"]))
        {
            sql += " AND Gender = @Gender";
            parameters.Add("Gender", query["gender"]);
        }
        if (!string.IsNullOrEmpty(query["group"]))
        {
            sql += " AND [Group] = @Group";
            parameters.Add("Group", query["group"]);
        }
        if (!string.IsNullOrEmpty(query["color"]))
        {
            sql += " AND Color = @Color";
            parameters.Add("Color", query["color"]);
        }
        if (!string.IsNullOrEmpty(query["quality"]))
        {
            sql += " AND Quality = @Quality";
            parameters.Add("Quality", query["quality"]);
        }
        if (!string.IsNullOrEmpty(query["clarity"]))
        {
            sql += " AND Clarity = @Clarity";
            parameters.Add("Clarity", query["clarity"]);
        }
        if (!string.IsNullOrEmpty(query["size"]))
        {
            sql += " AND Size = @Size";
            parameters.Add("Size", query["size"]);
        }
        if (!string.IsNullOrEmpty(query["damage"]))
        {
            sql += " AND Damages = @Damages";
            parameters.Add("Damages", query["damage"]);
        }
        if (!string.IsNullOrEmpty(query["hairLength"]))
        {
            sql += " AND HairLength = @HairLength";
            parameters.Add("HairLength", query["hairLength"]);
        }
    }

    private static void AddColumnHeader(TableCellDescriptor table, bool isFarmerCatalog = false)
    {
        var titles = isFarmerCatalog
            ? new[] { "Lot", "Skins", "Description", "Price/Skin", "Value", "Status" }
            : new[] { "Lot", "Skins", "Description", "Price", "Comments" };

        for (var i = 0; i < titles.Length; i++)
        {
            var first = i == 0;
            var last = i == titles.Length - 1;
            // Top + outer sides match the string box (StringBorderWidth); internal dividers stay thin.
            table.Cell().Element(c => c
                    .BorderTop(StringBorderWidth)
                    .BorderBottom(0.5f)
                    .BorderLeft(first ? StringBorderWidth : 0.5f)
                    .BorderRight(last ? StringBorderWidth : 0.5f)
                    .BorderColor(Colors.Grey.Medium)
                    .Background(Colors.Grey.Lighten3)
                    .PaddingVertical(4)
                    .PaddingHorizontal(4))
                .Text(titles[i]).Bold();
        }
    }

    private static void AddCatalogRow(
        TableDescriptor table,
        CatalogPdfRow row,
        bool nextIsStringStart = false,
        bool isFarmerCatalog = false,
        Dictionary<int, LotSaleInfo>? lotSaleData = null)
    {
        var lastColIndex = isFarmerCatalog ? 5 : 4;
        var colIndex = 0;

        // Each cell carries the table's vertical lines so they run continuously through every lot: thick
        // outer sides (StringBorderWidth), thin column dividers (ColumnDividerWidth). The bottom row line is
        // suppressed right before a string starts (the string's thick top is the divider there).
        IContainer Cell()
        {
            var ci = colIndex++;
            return table.Cell().Element(c =>
            {
                if (!nextIsStringStart) c = c.BorderBottom(RowLineWidth);
                if (ci == 0) c = c.BorderLeft(StringBorderWidth);
                c = c.BorderRight(ci == lastColIndex ? StringBorderWidth : ColumnDividerWidth);
                return c.BorderColor(RowLineColor).PaddingVertical(3).PaddingHorizontal(4);
            });
        }

        Cell().Text(BuildLotsText(row));
        Cell().Text(BuildSkinsText(row));
        RenderDescriptionCell(Cell(), row);

        if (isFarmerCatalog && lotSaleData != null && lotSaleData.TryGetValue(row.LotNumber, out var sale))
        {
            Cell().AlignRight().Text(sale.HasResult ? $"\u20ac{sale.PricePerSkin:N2}" : "-");
            Cell().AlignRight().Text(sale.HasResult ? $"\u20ac{sale.Value:N2}" : "-");
            Cell().Text(sale.PdfStatus).FontColor(sale.HasResult ? Colors.Green.Darken2 : Colors.Grey.Medium).Bold();
        }
        else if (isFarmerCatalog)
        {
            Cell().Text("-");
            Cell().Text("-");
            Cell().Text("");
        }
        else
        {
            // Price = estimated price, Comments = remarks (auctioneer PDF); empty on the customer catalogue.
            Cell().Text(row.Estimate ?? "");
            Cell().Text(row.Remarks ?? "");
        }
    }

    private static void AddStringGroup(
        TableDescriptor table,
        List<CatalogPdfRow> groupRows,
        bool isFarmerCatalog = false,
        Dictionary<int, LotSaleInfo>? lotSaleData = null)
    {
        // Render the multi-lot string as flat rows in the SAME section table (identical column
        // widths as single-lot rows) and draw the group "box" with borders on those cells, instead
        // of a nested sub-table. A nested table was inset by its own border, so its columns no
        // longer lined up with the single-lot rows and its narrower Description column wrapped to an
        // extra line \u2014 making those rows look a different size.
        var lastColIndex = isFarmerCatalog ? 5 : 4;

        for (var k = 0; k < groupRows.Count; k++)
        {
            var row = groupRows[k];
            var isFirst = k == 0;
            var isLast = k == groupRows.Count - 1;
            var colIndex = 0;

            IContainer Cell()
            {
                var ci = colIndex++;
                return table.Cell().Element(c =>
                {
                    if (isFirst) c = c.BorderTop(StringBorderWidth);
                    if (isLast) c = c.BorderBottom(StringBorderWidth);
                    else c = c.BorderBottom(RowLineWidth);   // thin separator between lots inside the string (half the box)
                    if (ci == 0) c = c.BorderLeft(StringBorderWidth);
                    c = c.BorderRight(ci == lastColIndex ? StringBorderWidth : ColumnDividerWidth);   // thin column dividers through the string
                    return c.BorderColor(RowLineColor).PaddingVertical(3).PaddingHorizontal(4);
                });
            }

            Cell().Text(BuildLotsText(row));
            Cell().Text(BuildSkinsText(row));
            RenderDescriptionCell(Cell(), row);

            if (isFarmerCatalog && lotSaleData != null && lotSaleData.TryGetValue(row.LotNumber, out var sale))
            {
                Cell().AlignRight().Text(sale.HasResult ? $"\u20ac{sale.PricePerSkin:N2}" : "-");
                Cell().AlignRight().Text(sale.HasResult ? $"\u20ac{sale.Value:N2}" : "-");
                Cell().Text(sale.PdfStatus).FontColor(sale.HasResult ? Colors.Green.Darken2 : Colors.Grey.Medium).Bold();
            }
            else if (isFarmerCatalog)
            {
                Cell().Text("-");
                Cell().Text("-");
                Cell().Text("");
            }
            else
            {
                Cell().Text(row.Estimate ?? "");
                Cell().Text(row.Remarks ?? "");
            }
        }
    }

    private static void ComposeHeader(IContainer container, string logoPath)
    {
        container
            .PaddingBottom(15)
            .Row(row =>
            {
                row.RelativeItem()
                    .AlignBottom()
                    .Column(column =>
                    {
                        column.Item()
                            .AlignLeft()
                            .Text(Environment.GetEnvironmentVariable("CATALOG_HEADER_TEXT") ?? "261 JULY 26")
                            .FontSize(9)
                            .Bold();
                    });

                row.ConstantItem(120)
                    .Height(45)
                    .AlignRight()
                    .AlignBottom()
                    .Image(logoPath, ImageScaling.FitArea);
            });
    }

    private static void ComposeFooter(IContainer container)
    {
        container
            .AlignCenter()
            .Text(text =>
            {
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
    }

    private static string BuildSectionTitle(CatalogPdfRow row)
    {
        return $"{row.SalesType} - {row.Gender} - {row.Group}";
    }

    private static string BuildLotsText(CatalogPdfRow row)
    {
        return row.LotNumber.ToString();
    }

    private static string BuildSkinsText(CatalogPdfRow row)
    {
        return row.TotalSkins.ToString("#,##0");
    }

    private static string BuildDescriptionText(CatalogPdfRow row)
    {
        if (!row.IsMultiLotString)
            return BuildDescription(row);

        if (row.LotSequenceInString == 1)
            return BuildDescription(row);

        if (row.IsLastLotInString)
            return $"{row.StringTotalSkins:#,##0} skins";

        return row.LotSequenceInString.ToString();
    }

    private static string BuildDescription(CatalogPdfRow row)
    {
        var parts = new List<string>
        {
            row.HairLength, row.Size, row.Quality, row.Color, row.Clarity
        };

        if (!string.IsNullOrWhiteSpace(row.Damages)
            && !string.Equals(row.Damages, "None", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(row.Damages);
        }

        return string.Join(" / ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    // The description's groups laid out as 3 columns of 2 stacked lines (slightly smaller font): the first
    // two groups in column 1, the next two in column 2, the last two in column 3. Blank groups are dropped
    // first (so a missing Damages just leaves column 3 with one line), keeping the same order as the old
    // "/"-joined text.
    private static void RenderDescriptionCell(IContainer container, CatalogPdfRow row)
    {
        // In a multi-lot string only the first lot shows the description; the other rows show the sequence
        // number / string skin total — keep those as plain text.
        if (row.IsMultiLotString && row.LotSequenceInString != 1)
        {
            container.Text(row.IsLastLotInString ? $"{row.StringTotalSkins:#,##0} skins" : row.LotSequenceInString.ToString());
            return;
        }

        var parts = BuildDescriptionParts(row);
        container.Row(r =>
        {
            for (var col = 0; col < 3; col++)
            {
                var top = col * 2 < parts.Count ? parts[col * 2] : "";
                var bottom = col * 2 + 1 < parts.Count ? parts[col * 2 + 1] : "";
                r.RelativeItem().Column(cc =>
                {
                    cc.Item().Text(top).FontSize(DescriptionFontSize);
                    cc.Item().Text(bottom).FontSize(DescriptionFontSize);
                });
            }
        });
    }

    private static List<string> BuildDescriptionParts(CatalogPdfRow row)
    {
        var parts = new List<string?> { row.HairLength, row.Size, row.Quality, row.Color, row.Clarity };
        if (!string.IsNullOrWhiteSpace(row.Damages)
            && !string.Equals(row.Damages, "None", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(row.Damages);
        }
        return parts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
    }

    // No Background here: a white cell background overpaints the bottom border of the cell above (borders
    // straddle the shared edge), which made the thick line below a string's last lot look thinner. The page
    // is white anyway, so dropping it is purely a fix.
    private static IContainer NormalCell(IContainer container)
    {
        return container
            .BorderBottom(RowLineWidth)
            .BorderColor(RowLineColor)
            .PaddingVertical(3)
            .PaddingHorizontal(4);
    }

    private static IContainer NoBorderCell(IContainer container)
    {
        return container
            .PaddingVertical(3)
            .PaddingHorizontal(4);
    }

    private static IContainer HeaderCell(IContainer container)
    {
        return container
            .Border(0.5f)
            .BorderColor(Colors.Grey.Medium)
            .Background(Colors.Grey.Lighten3)
            .PaddingVertical(4)
            .PaddingHorizontal(4);
    }

    private static IContainer SectionCell(IContainer container)
    {
        // Section title: top + sides match the string box (StringBorderWidth) so the header reads as a box.
        return container
            .BorderTop(StringBorderWidth)
            .BorderLeft(StringBorderWidth)
            .BorderRight(StringBorderWidth)
            .BorderBottom(RowLineWidth)
            .BorderColor(Colors.Grey.Darken1)
            .Background(Colors.Grey.Lighten2)
            .PaddingVertical(5)
            .PaddingHorizontal(4);
    }

    private class LotSaleInfo
    {
        public bool HasResult { get; set; }
        public bool IsInvoiced { get; set; }
        public decimal PricePerSkin { get; set; }
        public decimal Value { get; set; }
        public int FarmerSkins { get; set; }
        public bool IsPaid { get; set; }
        public string Status => IsPaid ? "Paid" : IsInvoiced ? "Sold" : HasResult ? "Hammer" : "Auction";
        // Farmer catalogue: a hammer price = Sold (the invoiced/paid distinction is hidden from farmers).
        public string PdfStatus => HasResult ? "Sold" : "";
    }
}
