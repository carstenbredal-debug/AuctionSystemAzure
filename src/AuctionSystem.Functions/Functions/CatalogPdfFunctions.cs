using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Net;

namespace AuctionSystem.Functions.Functions;

public class CatalogPdfFunctions
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CatalogPdfFunctions> _logger;
    private const string FontName = "PPPangramSans";
    private static bool _fontsRegistered;

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

    private static string GetCatalogTable(System.Collections.Specialized.NameValueCollection query)
    {
        var auctionNumber = query["auctionNumber"];
        if (!string.IsNullOrEmpty(auctionNumber))
            return $"auction.[{auctionNumber}.Lots]";
        return "auction.cataloglots";
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

    [Function("GenerateCatalogPdf")]
    public async Task<HttpResponseData> GeneratePdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/pdf")]
        HttpRequestData req)
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

                FROM " + GetCatalogTable(query) + @"
                WHERE 1=1";

            var parameters = new DynamicParameters();
            AddFilterParameters(query, ref sql, parameters);

            sql += " ORDER BY CatalogSortOrder";

            var rows = (await connection.QueryAsync<CatalogPdfRow>(sql, parameters)).ToList();

            // Filter by farmer if specified
            var farmerName = query["farmerName"];
            var isFarmerCatalog = !string.IsNullOrEmpty(farmerName);
            var lotSaleData = new Dictionary<int, LotSaleInfo>();

            if (isFarmerCatalog)
            {
                var auctionNumber = query["auctionNumber"];
                if (!string.IsNullOrEmpty(auctionNumber))
                {
                    var skinsTable = $"auction.[{auctionNumber}.Skins]";
                    var farmerBoxes = new HashSet<int>();
                    var farmerSkinsByBox = new Dictionary<int, int>();
                    var boxSql = $"SELECT BoxNumber, COUNT(*) AS Cnt FROM {skinsTable} WHERE Farmer = @Farmer GROUP BY BoxNumber";
                    var boxRows = await connection.QueryAsync<dynamic>(boxSql, new { Farmer = farmerName });
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
                                                columns.ConstantColumn(60);   // Price
                                                columns.ConstantColumn(90);   // Comments
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
        table.Cell().Element(HeaderCell).Text("Lots").Bold();
        table.Cell().Element(HeaderCell).Text("Skins").Bold();
        table.Cell().Element(HeaderCell).Text("Description").Bold();
        if (isFarmerCatalog)
        {
            table.Cell().Element(HeaderCell).Text("Price/Skin").Bold();
            table.Cell().Element(HeaderCell).Text("Value").Bold();
            table.Cell().Element(HeaderCell).Text("Status").Bold();
        }
        else
        {
            table.Cell().Element(HeaderCell).Text("Price").Bold();
            table.Cell().Element(HeaderCell).Text("Comments").Bold();
        }
    }

    private static void AddCatalogRow(
        TableDescriptor table,
        CatalogPdfRow row,
        bool nextIsStringStart = false,
        bool isFarmerCatalog = false,
        Dictionary<int, LotSaleInfo>? lotSaleData = null)
    {
        IContainer CellStyle(IContainer c) => nextIsStringStart ? NoBorderCell(c) : NormalCell(c);

        table.Cell().Element(CellStyle).Text(BuildLotsText(row));
        table.Cell().Element(CellStyle).Text(BuildSkinsText(row));
        table.Cell().Element(CellStyle).Text(BuildDescriptionText(row));

        if (isFarmerCatalog && lotSaleData != null && lotSaleData.TryGetValue(row.LotNumber, out var sale))
        {
            table.Cell().Element(CellStyle).AlignRight().Text(sale.HasResult ? $"\u20ac{sale.PricePerSkin:N2}" : "-");
            table.Cell().Element(CellStyle).AlignRight().Text(sale.HasResult ? $"\u20ac{sale.Value:N2}" : "-");
            table.Cell().Element(CellStyle).Text(sale.PdfStatus).FontColor(sale.IsPaid ? Color.FromHex("#230A3C") : sale.IsInvoiced ? Colors.Green.Darken2 : sale.HasResult ? Colors.Blue.Darken2 : Colors.Grey.Medium).Bold();
        }
        else if (isFarmerCatalog)
        {
            table.Cell().Element(CellStyle).Text("-");
            table.Cell().Element(CellStyle).Text("-");
            table.Cell().Element(CellStyle).Text("");
        }
        else
        {
            table.Cell().Element(CellStyle).Text("");
            table.Cell().Element(CellStyle).Text("");
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
        const float box = 1.5f;
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
                    c = c.Background(Colors.White);
                    if (isFirst) c = c.BorderTop(box);
                    if (isLast) c = c.BorderBottom(box);
                    if (ci == 0) c = c.BorderLeft(box);
                    if (ci == lastColIndex) c = c.BorderRight(box);
                    return c.BorderColor(Colors.Black).PaddingVertical(3).PaddingHorizontal(4);
                });
            }

            Cell().Text(BuildLotsText(row));
            Cell().Text(BuildSkinsText(row));
            Cell().Text(BuildDescriptionText(row));

            if (isFarmerCatalog && lotSaleData != null && lotSaleData.TryGetValue(row.LotNumber, out var sale))
            {
                Cell().AlignRight().Text(sale.HasResult ? $"\u20ac{sale.PricePerSkin:N2}" : "-");
                Cell().AlignRight().Text(sale.HasResult ? $"\u20ac{sale.Value:N2}" : "-");
                Cell().Text(sale.PdfStatus).FontColor(sale.IsPaid ? Color.FromHex("#230A3C") : sale.IsInvoiced ? Colors.Green.Darken2 : sale.HasResult ? Colors.Blue.Darken2 : Colors.Grey.Medium).Bold();
            }
            else if (isFarmerCatalog)
            {
                Cell().Text("-");
                Cell().Text("-");
                Cell().Text("");
            }
            else
            {
                Cell().Text("");
                Cell().Text("");
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

    private static IContainer NormalCell(IContainer container)
    {
        return container
            .BorderBottom(0.5f)
            .BorderColor(Colors.Grey.Lighten1)
            .Background(Colors.White)
            .PaddingVertical(3)
            .PaddingHorizontal(4);
    }

    private static IContainer NoBorderCell(IContainer container)
    {
        return container
            .Background(Colors.White)
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
        return container
            .Border(0.75f)
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
        public string PdfStatus => IsPaid ? "Paid" : IsInvoiced ? "Sold" : HasResult ? "Hmr" : "";
    }
}
