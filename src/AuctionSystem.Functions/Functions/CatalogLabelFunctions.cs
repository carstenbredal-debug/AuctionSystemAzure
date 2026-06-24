using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;
using AuctionSystem.Functions.Auth;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Net;

namespace AuctionSystem.Functions.Functions;

// Admin-only label sheet for a catalogue draft: one 7.5x8.5cm label per SHOWLOT lot (IsShow='Yes'),
// laid out 2-across on A4 with cut borders. Each label shows the Lot # (large), the showlot box number
// (the BoxType='Showlot' box within the lot), and a scannable Code 128 barcode of that box's barcode.
public class CatalogLabelFunctions
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CatalogLabelFunctions> _logger;

    private const string FontName = "PPPangramSans";
    private static bool _fontsRegistered;

    public CatalogLabelFunctions(IConfiguration configuration, ILogger<CatalogLabelFunctions> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GetConnectionString() =>
        _configuration["SqlConnectionString"] ?? _configuration["Values:SqlConnectionString"] ?? "";

    private static void RegisterFonts()
    {
        if (_fontsRegistered) return;
        var basePath = AppContext.BaseDirectory;
        foreach (var f in new[] { "PPPangramSans-Medium.otf", "PPPangramSans-Bold.otf" })
        {
            var path = Path.Combine(basePath, "Assets", f);
            if (File.Exists(path))
                using (var s = File.OpenRead(path))
                    QuestPDF.Drawing.FontManager.RegisterFont(s);
        }
        _fontsRegistered = true;
    }

    [RequireRole("Admin")]
    [Function("GenerateCatalogLabelsPdf")]
    public async Task<HttpResponseData> GenerateLabels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/labels-pdf")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (!int.TryParse(query["draftId"], out var draftId) || draftId <= 0)
                return await Text(req, HttpStatusCode.BadRequest, "draftId required.");

            QuestPDF.Settings.License = LicenseType.Community;
            RegisterFonts();

            var connectionString = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
                return await Text(req, HttpStatusCode.InternalServerError, "Connection string missing.");

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // Showlot lots in this draft (in catalogue order).
            var lots = (await connection.QueryAsync<LotRow>(
                @"SELECT LotNumber, ISNULL(IncludedBoxNumbers,'') AS IncludedBoxNumbers, ISNULL(RackPosition,'') AS RackPosition
                  FROM auction.CatalogDraftLots
                  WHERE DraftId = @draftId AND IsShow = 'Yes'
                  ORDER BY CatalogSortOrder, LotNumber",
                new { draftId })).ToList();

            if (lots.Count == 0)
                return await Text(req, HttpStatusCode.NotFound, "No showlot lots in this catalogue.");

            // All boxes referenced by those lots -> the showlot boxes + their barcode (one query).
            var allBoxes = lots
                .SelectMany(l => ParseBoxes(l.IncludedBoxNumbers))
                .Distinct().ToList();

            var showBoxes = new HashSet<int>();
            if (allBoxes.Count > 0)
            {
                var boxNums = await connection.QueryAsync<int>(
                    @"SELECT DISTINCT BoxNumber FROM dbo.SkinTable
                      WHERE BoxType = 'Showlot' AND IsActive = 1 AND BoxNumber IN @boxes",
                    new { boxes = allBoxes });
                foreach (var n in boxNums) showBoxes.Add(n);
            }

            var labels = new List<LabelData>();
            foreach (var l in lots)
            {
                var showBox = ParseBoxes(l.IncludedBoxNumbers).FirstOrDefault(b => showBoxes.Contains(b));
                labels.Add(new LabelData { LotNumber = l.LotNumber, ShowBox = showBox, Rack = l.RackPosition });
            }

            byte[] pdf = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(8, Unit.Millimetre);
                    page.DefaultTextStyle(x => x.FontFamily(FontName));

                    page.Content().Column(col =>
                    {
                        col.Spacing(4, Unit.Millimetre);
                        for (var i = 0; i < labels.Count; i += 2)
                        {
                            var pair = labels.Skip(i).Take(2).ToList();
                            col.Item().Row(row =>
                            {
                                row.Spacing(4, Unit.Millimetre);
                                foreach (var d in pair)
                                    row.ConstantItem(7.5f, Unit.Centimetre).Element(c => LabelCell(c, d));
                            });
                        }
                    });
                });
            }).GeneratePdf();

            _logger.LogInformation("Generated {Count} labels for catalogue draft {Id}", labels.Count, draftId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/pdf");
            response.Headers.Add("Content-Disposition", $"attachment; filename=labels-{draftId}.pdf");
            await response.Body.WriteAsync(pdf, 0, pdf.Length);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating label PDF");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.ToString());
            return response;
        }
    }

    // Simpler label: just the Lot # (as large as fits) for each showlot lot, on 100x30mm labels, 2-up on A4.
    [RequireRole("Admin")]
    [Function("GenerateLotNumberLabelsPdf")]
    public async Task<HttpResponseData> GenerateLotNumberLabels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "catalog/lot-labels-pdf")] HttpRequestData req)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (!int.TryParse(query["draftId"], out var draftId) || draftId <= 0)
                return await Text(req, HttpStatusCode.BadRequest, "draftId required.");

            QuestPDF.Settings.License = LicenseType.Community;
            RegisterFonts();

            var connectionString = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
                return await Text(req, HttpStatusCode.InternalServerError, "Connection string missing.");

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var lotNumbers = (await connection.QueryAsync<int>(
                @"SELECT LotNumber FROM auction.CatalogDraftLots
                  WHERE DraftId = @draftId AND IsShow = 'Yes'
                  ORDER BY CatalogSortOrder, LotNumber",
                new { draftId })).ToList();

            if (lotNumbers.Count == 0)
                return await Text(req, HttpStatusCode.NotFound, "No showlot lots in this catalogue.");

            byte[] pdf = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(6, Unit.Millimetre);
                    page.DefaultTextStyle(x => x.FontFamily(FontName));

                    page.Content().Column(col =>
                    {
                        col.Spacing(2, Unit.Millimetre);
                        for (var i = 0; i < lotNumbers.Count; i += 2)
                        {
                            var pair = lotNumbers.Skip(i).Take(2).ToList();
                            col.Item().Row(row =>
                            {
                                row.Spacing(2, Unit.Millimetre);
                                foreach (var ln in pair)
                                    row.ConstantItem(100, Unit.Millimetre).Element(cell =>
                                        cell.Height(30, Unit.Millimetre)
                                            .Border(1).BorderColor(Colors.Grey.Darken1)
                                            .AlignCenter().AlignMiddle()
                                            .Text(ln.ToString()).FontSize(64).Bold());
                            });
                        }
                    });
                });
            }).GeneratePdf();

            _logger.LogInformation("Generated {Count} lot-number labels for draft {Id}", lotNumbers.Count, draftId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/pdf");
            response.Headers.Add("Content-Disposition", $"attachment; filename=lot-labels-{draftId}.pdf");
            await response.Body.WriteAsync(pdf, 0, pdf.Length);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating lot-number label PDF");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync(ex.ToString());
            return response;
        }
    }

    private static void LabelCell(IContainer cell, LabelData d)
    {
        cell
            .Height(8.5f, Unit.Centimetre)
            .Border(1).BorderColor(Colors.Grey.Darken1)
            .Padding(6).AlignMiddle()
            .Column(c =>
            {
                c.Item().AlignCenter().Text(d.LotNumber.ToString()).FontSize(46).Bold();
                if (d.ShowBox > 0)
                    c.Item().AlignCenter().PaddingTop(2).Text($"Box {d.ShowBox}").FontSize(22);

                // Barcode encodes the showlot BOX NUMBER (no separate box-barcode field exists).
                if (d.ShowBox > 0)
                    c.Item().PaddingTop(10).Height(48).Element(e => RenderBarcode(e, d.ShowBox.ToString()));

                if (!string.IsNullOrWhiteSpace(d.Rack))
                    c.Item().AlignCenter().PaddingTop(4).Text($"Rack {d.Rack}").FontSize(16).Bold();
            });
    }

    // Render a Code 128 barcode as proportional black/white bars (vector -> crisp & scannable when printed).
    private static void RenderBarcode(IContainer container, string data)
    {
        var segments = Code128.EncodeB(data);
        container.Row(row =>
        {
            foreach (var (isBar, width) in segments)
                row.RelativeItem(width).Background(isBar ? Colors.Black : Colors.White);
        });
    }

    private static List<int> ParseBoxes(string csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0)
            .Where(n => n > 0).ToList();

    private static async Task<HttpResponseData> Text(HttpRequestData req, HttpStatusCode code, string msg)
    {
        var r = req.CreateResponse(code);
        await r.WriteStringAsync(msg);
        return r;
    }

    private sealed class LotRow
    {
        public int LotNumber { get; set; }
        public string IncludedBoxNumbers { get; set; } = "";
        public string RackPosition { get; set; } = "";
    }

    private sealed class LabelData
    {
        public int LotNumber { get; set; }
        public int ShowBox { get; set; }
        public string Rack { get; set; } = "";
    }
}

// Minimal Code 128 (subset B) encoder -> list of (isBar, moduleWidth) segments. Numeric/ASCII data.
internal static class Code128
{
    private static readonly string[] Patterns =
    {
        "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213",
        "221312","231212","112232","122132","122231","113222","123122","123221","223211","221132",
        "221231","213212","223112","312131","311222","321122","321221","312212","322112","322211",
        "212123","212321","232121","111323","131123","131321","112313","132113","132311","211313",
        "231113","231311","112133","112331","132131","113123","113321","133121","313121","211331",
        "231131","213113","213311","213131","311123","311321","331121","312113","312311","332111",
        "314111","221411","431111","111224","111422","121124","121421","141122","141221","112214",
        "112412","122114","122411","142112","142211","241211","221114","413111","241112","134111",
        "111242","121142","121241","114212","124112","124211","411212","421112","421211","212141",
        "214121","412121","111143","111341","131141","114113","114311","411113","411311","113141",
        "114131","311141","411131","211412","211214","211232","2331112"
    };

    public static List<(bool IsBar, int Width)> EncodeB(string data)
    {
        var values = new List<int> { 104 }; // Start Code B
        long sum = 104;
        for (var i = 0; i < data.Length; i++)
        {
            var v = data[i] - 32;        // Code B: value = ASCII - 32 (0..94)
            if (v < 0 || v > 94) v = 0;  // out-of-range char -> space, keeps it valid
            values.Add(v);
            sum += (long)v * (i + 1);
        }
        values.Add((int)(sum % 103));    // checksum
        values.Add(106);                 // Stop

        var segments = new List<(bool, int)>();
        foreach (var v in values)
        {
            var pattern = Patterns[v];
            for (var k = 0; k < pattern.Length; k++)
                segments.Add((k % 2 == 0, pattern[k] - '0')); // even index = bar
        }
        return segments;
    }
}
