using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Domain.Services;
using AuctionSystem.Functions.BusinessCentral.Models;
using AuctionSystem.Functions.BusinessCentral.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class SettlementFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly SettlementService _service;
    private readonly BusinessCentralApiClient? _bcClient;
    private readonly ILogger<SettlementFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SettlementFunctions(AuctionDbContext db, CatalogDbContext catalogDb, SettlementService service, ILogger<SettlementFunctions> logger, BusinessCentralApiClient? bcClient = null)
    {
        _db = db;
        _catalogDb = catalogDb;
        _service = service;
        _logger = logger;
        _bcClient = bcClient;
    }

    [Function("GetInvoicesByBroker")]
    public async Task<HttpResponseData> GetInvoicesByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var invoices = await _service.GetInvoicesByBrokerAsync(brokerId);
        return await CreateJsonResponse(req, invoices.Select(i => new
        {
            i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
            i.TotalAmount, i.Currency, Status = i.Status.ToString(),
            BuyerName = i.Buyer?.Name, LinesCount = i.Lines.Count,
            i.IsCreditNote, OriginalInvoiceNumber = i.OriginalInvoice?.InvoiceNumber,
            i.PdfUrl
        }));
    }

    [Function("MarkInvoicePaid")]
    public async Task<HttpResponseData> MarkPaid(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settlements/invoices/{invoiceId:int}/paid")] HttpRequestData req, int invoiceId)
    {
        var invoice = await _service.MarkInvoicePaidAsync(invoiceId);
        if (invoice == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, invoice);
    }

    [Function("UpdateInvoiceStatus")]
    public async Task<HttpResponseData> UpdateInvoiceStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settlements/invoices/{invoiceId:int}/status")] HttpRequestData req, int invoiceId)
    {
        var body = await req.ReadFromJsonAsync<UpdateStatusRequest>();
        if (body == null || !Enum.TryParse<InvoiceStatus>(body.Status, true, out var status))
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        invoice.Status = status;
        if (body.ReleaseForShipping)
        {
            invoice.ShippingStatus = "Released";
            invoice.Status = InvoiceStatus.ReleasedToShip;
        }
        if (status == InvoiceStatus.Issued)
        {
            invoice.ShippingStatus = null;
        }
        await _db.SaveChangesAsync();

        // When marked as Paid, apply existing payment in BC to close the invoice
        string? bcPaymentError = null;
        string? bcPaymentSuccess = null;
        if (status == InvoiceStatus.Paid && _bcClient != null && !string.IsNullOrEmpty(invoice.BcInvoiceNumber))
        {
            try
            {
                bcPaymentSuccess = await ApplyPaymentToBcAsync(invoice);

                // After application, check if invoice is actually fully paid in BC
                var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == invoice.BuyerId);
                var customerNo = buyer?.BuyerNumber?.ToString() ?? "";
                if (!string.IsNullOrEmpty(customerNo))
                {
                    var companyId = await _bcClient.ResolveCompanyIdAsync();
                    var entries = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(companyId, customerNo, "Invoice", false);
                    var bcEntry = entries.FirstOrDefault(e => e.DocumentNo == invoice.BcInvoiceNumber);
                    if (bcEntry != null && bcEntry.Open && bcEntry.RemainingAmount > 0)
                    {
                        // BC still has a remaining balance — downgrade to Downpayment
                        invoice.Status = body.ReleaseForShipping ? InvoiceStatus.ReleasedToShip : InvoiceStatus.Downpayment;
                        await _db.SaveChangesAsync();
                        _logger.LogInformation("Invoice {Id} still has BC remaining {Remaining}, set status to {Status}",
                            invoice.Id, bcEntry.RemainingAmount, invoice.Status);
                        bcPaymentSuccess += $" (Remaining on invoice: {bcEntry.RemainingAmount:N2})";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply payment in BC for invoice {Id}", invoice.Id);
                bcPaymentError = ex.Message;
            }
        }

        return await CreateJsonResponse(req, new { invoice.Id, Status = invoice.Status.ToString(), invoice.ShippingStatus, bcPaymentError, bcPaymentSuccess });
    }

    [Function("CheckBcPaymentBalance")]
    public async Task<HttpResponseData> CheckBcPaymentBalance(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices/{invoiceId:int}/bc-balance")] HttpRequestData req, int invoiceId)
    {
        var invoice = await _db.Invoices.Include(i => i.Buyer).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (_bcClient == null || string.IsNullOrEmpty(invoice.BcInvoiceNumber))
        {
            return await CreateJsonResponse(req, new
            {
                available = false,
                reason = "BC not configured or invoice not pushed to BC",
                availableAmount = 0m,
                invoiceAmount = invoice.TotalAmount
            });
        }

        var buyerNo = invoice.Buyer?.BuyerNumber ?? "";
        if (string.IsNullOrEmpty(buyerNo))
        {
            return await CreateJsonResponse(req, new
            {
                available = false,
                reason = "Buyer has no customer number",
                availableAmount = 0m,
                invoiceAmount = invoice.TotalAmount
            });
        }

        try
        {
            var companyId = await _bcClient.ResolveCompanyIdAsync();
            var payments = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(companyId, buyerNo, "Payment", true);
            var totalAvailable = payments.Sum(p => Math.Abs(p.RemainingAmount));

            // Fetch the actual remaining amount on the BC invoice (accounts for partial payments already applied)
            var bcInvoices = await _bcClient.GetSalesInvoicesAsync(companyId, 5000);
            var bcInvoice = bcInvoices.FirstOrDefault(i => i.Number == invoice.BcInvoiceNumber);
            var remainingOnInvoice = bcInvoice?.RemainingAmount ?? invoice.TotalAmount;

            return await CreateJsonResponse(req, new
            {
                available = true,
                availableAmount = totalAvailable,
                invoiceAmount = remainingOnInvoice,
                originalInvoiceAmount = invoice.TotalAmount,
                sufficient = totalAvailable >= remainingOnInvoice,
                buyerNumber = buyerNo,
                buyerName = invoice.Buyer?.Name ?? "",
                openPaymentCount = payments.Count,
                bcRemainingAmount = remainingOnInvoice
            });
        }
        catch (Exception ex)
        {
            return await CreateJsonResponse(req, new
            {
                available = false,
                reason = $"Failed to check BC: {ex.Message}",
                availableAmount = 0m,
                invoiceAmount = invoice.TotalAmount
            });
        }
    }

    [Function("CheckBcInvoiceRemaining")]
    public async Task<HttpResponseData> CheckBcInvoiceRemaining(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settlements/invoices/{invoiceId:int}/check-bc-remaining")] HttpRequestData req, int invoiceId)
    {
        var invoice = await _db.Invoices.Include(i => i.Buyer).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (_bcClient == null || string.IsNullOrEmpty(invoice.BcInvoiceNumber))
            return await CreateJsonResponse(req, new { fullyPaidInBc = false, reason = "BC not configured" });

        try
        {
            var companyId = await _bcClient.ResolveCompanyIdAsync();
            var invoices = await _bcClient.GetSalesInvoicesAsync(companyId, 5000);
            var bcInvoice = invoices.FirstOrDefault(i => i.Number == invoice.BcInvoiceNumber);

            if (bcInvoice == null)
                return await CreateJsonResponse(req, new { fullyPaidInBc = false, reason = "Invoice not found in BC" });

            if (bcInvoice.RemainingAmount == 0 || bcInvoice.Status == "Paid")
            {
                invoice.Status = Domain.Enums.InvoiceStatus.Paid;
                await _db.SaveChangesAsync();
                return await CreateJsonResponse(req, new { fullyPaidInBc = true, remainingAmount = bcInvoice.RemainingAmount, status = "Paid" });
            }

            return await CreateJsonResponse(req, new { fullyPaidInBc = false, remainingAmount = bcInvoice.RemainingAmount, status = bcInvoice.Status });
        }
        catch (Exception ex)
        {
            return await CreateJsonResponse(req, new { fullyPaidInBc = false, reason = $"BC check failed: {ex.Message}" });
        }
    }

    private class UpdateStatusRequest
    {
        public string Status { get; set; } = "";
        public bool ReleaseForShipping { get; set; }
    }

    [Function("ProcessDownpayment")]
    public async Task<HttpResponseData> ProcessDownpayment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settlements/invoices/{invoiceId:int}/downpayment")] HttpRequestData req, int invoiceId)
    {
        var body = await req.ReadFromJsonAsync<DownpaymentRequest>();
        if (body == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var invoice = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Calculate downpayment amount
        decimal amount;
        if (body.IsPercentage)
        {
            amount = invoice.TotalAmount * (body.Amount / 100m);
            invoice.DownpaymentPercentage = body.Amount;
            invoice.DownpaymentAmount = amount;
        }
        else
        {
            amount = body.Amount;
            invoice.DownpaymentAmount = amount;
            invoice.DownpaymentPercentage = invoice.TotalAmount > 0 ? (amount / invoice.TotalAmount) * 100m : 0;
        }

        // Update status to Downpayment
        invoice.Status = InvoiceStatus.Downpayment;

        // Release for shipping if requested
        if (body.ReleaseForShipping)
        {
            invoice.ShippingStatus = "Released";
            invoice.Status = InvoiceStatus.ReleasedToShip;
        }

        await _db.SaveChangesAsync();

        // Apply partial payment in BC
        string? bcPaymentError = null;
        string? bcPaymentSuccess = null;
        if (_bcClient != null && !string.IsNullOrEmpty(invoice.BcInvoiceNumber))
        {
            try
            {
                bcPaymentSuccess = await ApplyPaymentToBcAsync(invoice, amount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply partial payment in BC for invoice {Id}", invoice.Id);
                bcPaymentError = ex.Message;
            }
        }

        return await CreateJsonResponse(req, new { invoice.Id, Status = invoice.Status.ToString(), invoice.ShippingStatus, invoice.DownpaymentAmount, invoice.DownpaymentPercentage, bcPaymentError, bcPaymentSuccess });
    }

    private class DownpaymentRequest
    {
        public decimal Amount { get; set; }
        public bool IsPercentage { get; set; }
        public bool ReleaseForShipping { get; set; }
    }

    [Function("ApplyCreditNotesToBc")]
    public async Task<HttpResponseData> ApplyCreditNotesToBc(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settlements/apply-credit-notes")] HttpRequestData req)
    {
        if (_bcClient == null)
            return await CreateJsonResponse(req, new { error = "BC not configured" }, System.Net.HttpStatusCode.ServiceUnavailable);

        var companyId = await _bcClient.ResolveCompanyIdAsync();

        var creditNotes = await _db.Invoices
            .Include(i => i.Buyer)
            .Include(i => i.OriginalInvoice)
            .Where(i => i.IsCreditNote && !string.IsNullOrEmpty(i.BcInvoiceNumber) && i.OriginalInvoiceId != null)
            .ToListAsync();

        var results = new List<object>();
        foreach (var cn in creditNotes)
            results.Add(await ApplySingleCreditNoteToBcAsync(companyId, cn));

        return await CreateJsonResponse(req, new { totalCreditNotes = creditNotes.Count, results });
    }

    private async Task<object> ApplySingleCreditNoteToBcAsync(Guid companyId, Invoice cn)
    {
        var originalInvoice = cn.OriginalInvoice;
        if (originalInvoice == null || string.IsNullOrEmpty(originalInvoice.BcInvoiceNumber))
            return new { creditNote = cn.BcInvoiceNumber, status = "Skipped", reason = "Original invoice not pushed to BC" };

        var buyerNo = cn.Buyer?.BuyerNumber ?? "";
        if (string.IsNullOrEmpty(buyerNo))
            return new { creditNote = cn.BcInvoiceNumber, status = "Skipped", reason = "No buyer number" };

        try
        {
            var creditMemoEntries = await _bcClient!.GetCustomerLedgerEntriesByCustomerAsync(
                companyId, buyerNo, "Credit Memo", true);

            var creditMemoEntry = creditMemoEntries.FirstOrDefault(e => e.DocumentNo == cn.BcInvoiceNumber);
            if (creditMemoEntry == null)
                return new { creditNote = cn.BcInvoiceNumber, status = "Skipped", reason = "Credit memo entry not found in BC (may already be applied)" };

            // BC extension handles posting date automatically (uses max of today, payment date, invoice date)
            var result = await _bcClient!.ApplyCreditMemoToInvoiceAsync(
                companyId, buyerNo, creditMemoEntry.EntryNo, originalInvoice.BcInvoiceNumber!);

            if (result.ResultStatus != "Error")
                await UpdateCreditStatusesAsync(cn, originalInvoice);

            return new
            {
                creditNote = cn.BcInvoiceNumber,
                originalInvoice = originalInvoice.BcInvoiceNumber,
                status = result.ResultStatus,
                message = result.ResultMessage
            };
        }
        catch (Exception ex)
        {
            return new { creditNote = cn.BcInvoiceNumber, status = "Error", reason = ex.Message };
        }
    }

    private async Task UpdateCreditStatusesAsync(Invoice creditNote, Invoice originalInvoice)
    {
        creditNote.Status = Domain.Enums.InvoiceStatus.Alloted;

        var originalLotCount = await _db.Invoices
            .Where(i => i.Id == originalInvoice.Id)
            .SelectMany(i => i.Lines)
            .CountAsync();

        var creditedLotCount = await _db.Invoices
            .Where(i => i.OriginalInvoiceId == originalInvoice.Id && i.IsCreditNote)
            .SelectMany(i => i.Lines)
            .Select(l => l.LotNumber)
            .Distinct()
            .CountAsync();

        if (originalLotCount > 0 && creditedLotCount >= originalLotCount)
            originalInvoice.Status = Domain.Enums.InvoiceStatus.FullyCredited;
        else if (creditedLotCount > 0)
            originalInvoice.Status = Domain.Enums.InvoiceStatus.PartiallyCredited;

        await _db.SaveChangesAsync();
    }

    [Function("RecalculateCreditStatuses")]
    public async Task<HttpResponseData> RecalculateCreditStatuses(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settlements/recalculate-credit-statuses")] HttpRequestData req)
    {
        // Find all invoices that have credit notes against them
        var invoicesWithCredits = await _db.Invoices
            .Include(i => i.Lines)
            .Where(i => !i.IsCreditNote && _db.Invoices.Any(cn => cn.OriginalInvoiceId == i.Id && cn.IsCreditNote))
            .ToListAsync();

        var updated = new List<object>();

        foreach (var invoice in invoicesWithCredits)
        {
            var originalLotNumbers = invoice.Lines.Select(l => l.LotNumber).ToList();

            var creditedLotNumbers = await _db.Invoices
                .Where(cn => cn.OriginalInvoiceId == invoice.Id && cn.IsCreditNote)
                .SelectMany(cn => cn.Lines)
                .Select(l => l.LotNumber)
                .Distinct()
                .ToListAsync();

            var oldStatus = invoice.Status;
            if (originalLotNumbers.Count > 0 && creditedLotNumbers.Count >= originalLotNumbers.Count)
                invoice.Status = Domain.Enums.InvoiceStatus.FullyCredited;
            else if (creditedLotNumbers.Count > 0)
                invoice.Status = Domain.Enums.InvoiceStatus.PartiallyCredited;

            if (invoice.Status != oldStatus)
                updated.Add(new { invoice = invoice.InvoiceNumber, oldStatus = oldStatus.ToString(), newStatus = invoice.Status.ToString(), originalLots = originalLotNumbers.Count, creditedLots = creditedLotNumbers.Count });
        }

        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, new { updatedCount = updated.Count, updated });
    }

    [Function("GetShippingBoxes")]
    public async Task<HttpResponseData> GetShippingBoxes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipping/boxes")] HttpRequestData req)
    {
        var (uncreditedLotNumbers, lotInvoiceMap) = await BuildUncreditedLotMapAsync();

        var catalogLots = await _catalogDb.CatalogLots
            .Where(cl => uncreditedLotNumbers.Contains(cl.LotNumber))
            .Select(cl => new CatalogLotInfo(cl.LotNumber, cl.IncludedBoxNumbers))
            .ToListAsync();

        var boxInfo = await FetchBoxInfoAsync(catalogLots);

        var dimensions = await _db.BoxTypeDimensions.ToListAsync();
        var dimLookup = dimensions.ToDictionary(d => d.BoxType, d => d);

        var boxStagingLookup = await FetchBoxStagingAsync(catalogLots);

        var shippingBoxes = BuildShippingBoxList(catalogLots, lotInvoiceMap, boxInfo, dimLookup, boxStagingLookup);
        return await CreateJsonResponse(req, shippingBoxes);
    }

    private async Task<(List<int> LotNumbers, Dictionary<int, (Invoice Inv, InvoiceLine Line)> Map)> BuildUncreditedLotMapAsync()
    {
        var releasedInvoices = await _db.Invoices
            .Include(i => i.Lines).Include(i => i.Broker).Include(i => i.Buyer)
            .Where(i => i.ShippingStatus == "Released" && !i.IsCreditNote)
            .ToListAsync();

        var creditNotes = await _db.Invoices
            .Include(i => i.Lines)
            .Where(i => i.IsCreditNote && i.OriginalInvoiceId != null)
            .ToListAsync();

        var creditedLotsByInvoice = creditNotes
            .GroupBy(cn => cn.OriginalInvoiceId!.Value)
            .ToDictionary(g => g.Key, g => g.SelectMany(cn => cn.Lines.Select(l => l.LotNumber)).Distinct().ToHashSet());

        var lotNumbers = new List<int>();
        var map = new Dictionary<int, (Invoice Inv, InvoiceLine Line)>();
        foreach (var inv in releasedInvoices)
        {
            var creditedLots = creditedLotsByInvoice.GetValueOrDefault(inv.Id) ?? new HashSet<int>();
            foreach (var line in inv.Lines.Where(l => !creditedLots.Contains(l.LotNumber)))
            {
                lotNumbers.Add(line.LotNumber);
                map[line.LotNumber] = (inv, line);
            }
        }
        return (lotNumbers, map);
    }

    private record CatalogLotInfo(int LotNumber, string? IncludedBoxNumbers);

    private async Task<Dictionary<int, BoxViewInfo>> FetchBoxInfoAsync(List<CatalogLotInfo> catalogLots)
    {
        var allBoxNumbers = catalogLots
            .Where(cl => !string.IsNullOrEmpty(cl.IncludedBoxNumbers))
            .SelectMany(cl => cl.IncludedBoxNumbers!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0))
            .Distinct().ToList();

        var result = new Dictionary<int, BoxViewInfo>();
        if (allBoxNumbers.Count > 0)
        {
            var boxData = await _catalogDb.Database
                .SqlQueryRaw<BoxViewInfo>("SELECT BoxNumber, Skins, BoxType FROM auction.boxes WHERE BoxNumber IN (" +
                    string.Join(",", allBoxNumbers) + ")")
                .ToListAsync();
            foreach (var b in boxData)
                result[b.BoxNumber] = b;
        }
        return result;
    }

    private async Task<Dictionary<int, BoxStagingInfo>> FetchBoxStagingAsync(List<CatalogLotInfo> catalogLots)
    {
        var allBoxNumbers = catalogLots
            .Where(cl => !string.IsNullOrEmpty(cl.IncludedBoxNumbers))
            .SelectMany(cl => cl.IncludedBoxNumbers!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => int.TryParse(b.Trim(), out var n) ? n : 0).Where(n => n > 0))
            .Distinct().ToList();

        var result = new Dictionary<int, BoxStagingInfo>();
        if (allBoxNumbers.Count > 0)
        {
            try
            {
                var staging = await _catalogDb.Database
                    .SqlQueryRaw<BoxStagingInfo>("SELECT CAST(BoxNumber AS INT) AS BoxNumber, Weight AS BoxWeight, BoxLocation FROM dbo.boxstatingfromkphg WHERE BoxNumber IN (" +
                        string.Join(",", allBoxNumbers) + ")")
                    .ToListAsync();
                foreach (var s in staging)
                    result[s.BoxNumber] = s;
            }
            catch { /* table may not exist yet */ }
        }
        return result;
    }

    private class BoxStagingInfo
    {
        public int BoxNumber { get; set; }
        public decimal? BoxWeight { get; set; }
        public string? BoxLocation { get; set; }
    }

    private static List<object> BuildShippingBoxList(
        List<CatalogLotInfo> catalogLots, Dictionary<int, (Invoice Inv, InvoiceLine Line)> lotInvoiceMap, Dictionary<int, BoxViewInfo> boxInfo, Dictionary<string, BoxTypeDimension> dimLookup, Dictionary<int, BoxStagingInfo> boxStagingLookup)
    {
        var shippingBoxes = new List<object>();
        foreach (var cl in catalogLots)
        {
            if (string.IsNullOrEmpty(cl.IncludedBoxNumbers)) continue;
            if (!lotInvoiceMap.TryGetValue(cl.LotNumber, out var info)) continue;

            foreach (var boxStr in cl.IncludedBoxNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(boxStr.Trim(), out var boxNumber) || boxNumber <= 0) continue;
                var bi = boxInfo.GetValueOrDefault(boxNumber);
                var boxType = bi?.BoxType ?? "";
                var dim = !string.IsNullOrEmpty(boxType) && dimLookup.TryGetValue(boxType, out var d) ? d : null;
                shippingBoxes.Add(new
                {
                    InvoiceId = info.Inv.Id, info.Inv.InvoiceNumber,
                    BrokerName = info.Inv.Broker?.CompanyName, BuyerName = info.Inv.Buyer?.Name,
                    LotNumber = cl.LotNumber, BoxNumber = boxNumber,
                    BoxType = boxType, Skins = bi?.Skins ?? 0,
                    info.Line.PricePerSkin, HammerPrice = info.Line.HammerPrice,
                    VolumeM3 = dim != null ? dim.LengthM * dim.WidthM * dim.HeightM : (decimal?)null,
                    WeightKg = dim?.WeightKg,
                    BoxWeight = boxStagingLookup.TryGetValue(boxNumber, out var stg) ? stg.BoxWeight : null,
                    BoxLocation = stg?.BoxLocation
                });
            }
        }
        return shippingBoxes;
    }

    private class BoxViewInfo
    {
        public int BoxNumber { get; set; }
        public int Skins { get; set; }
        public string BoxType { get; set; } = "";
    }

    [Function("CreateSettlement")]
    public async Task<HttpResponseData> CreateSettlement(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settlements/create")] HttpRequestData req)
    {
        var lotId = await req.ReadFromJsonAsync<int>();
        var settlement = await _service.CreateSettlementAsync(lotId);
        if (settlement == null)
        {
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Cannot create settlement for this lot");
            return errorResponse;
        }
        return await CreateJsonResponse(req, settlement);
    }

    [Function("GetSettlementsByFarmer")]
    public async Task<HttpResponseData> GetByFarmer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/farmer/{farmerId:int}")] HttpRequestData req, int farmerId)
    {
        var settlements = await _service.GetSettlementsByFarmerAsync(farmerId);
        return await CreateJsonResponse(req, settlements);
    }

    [Function("DownloadInvoicePdf")]
    public async Task<HttpResponseData> DownloadInvoicePdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices/{invoiceId:int}/pdf")] HttpRequestData req, int invoiceId)
    {
        var invoice = await _db.Invoices.FindAsync(invoiceId);
        if (invoice == null || invoice.PdfData == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/pdf");
        response.Headers.Add("Content-Disposition", $"inline; filename=\"{invoice.InvoiceNumber}.pdf\"");
        await response.Body.WriteAsync(invoice.PdfData);
        return response;
    }

    [Function("GetInvoicesByBuyer")]
    public async Task<HttpResponseData> GetInvoicesByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices/buyer/{buyerId:int}")] HttpRequestData req, int buyerId)
    {
        var invoices = await _db.Invoices.Where(i => i.BuyerId == buyerId)
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => new
            {
                i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
                i.TotalAmount, i.Currency, Status = i.Status.ToString(),
                BrokerName = i.Broker.CompanyName, LinesCount = i.Lines.Count,
                i.IsCreditNote, OriginalInvoiceNumber = i.OriginalInvoice != null ? i.OriginalInvoice.InvoiceNumber : null,
                i.PdfUrl, i.BcInvoiceNumber
            }).ToListAsync();
        return await CreateJsonResponse(req, invoices);
    }

    [Function("GetAllInvoices")]
    public async Task<HttpResponseData> GetAllInvoices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices")] HttpRequestData req)
    {
        var invoices = await _db.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Broker)
            .Include(i => i.Buyer)
            .Include(i => i.OriginalInvoice)
            .OrderByDescending(i => i.InvoiceDate)
            .ToListAsync();

        // Fetch BC remaining amounts in bulk
        var bcRemainingMap = new Dictionary<string, decimal>();
        if (_bcClient != null)
        {
            try
            {
                var companyId = await _bcClient.ResolveCompanyIdAsync();
                var bcInvoices = await _bcClient.GetSalesInvoicesAsync(companyId, 5000);
                foreach (var bci in bcInvoices)
                    if (!string.IsNullOrEmpty(bci.Number))
                        bcRemainingMap[bci.Number] = bci.RemainingAmount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch BC remaining amounts");
            }
        }

        // Auto-correct status mismatches: Paid locally but BC still has remaining, or vice versa
        if (bcRemainingMap.Count > 0)
        {
            var needsSave = false;
            foreach (var inv in invoices.Where(i => !i.IsCreditNote && !string.IsNullOrEmpty(i.BcInvoiceNumber)))
            {
                if (!bcRemainingMap.TryGetValue(inv.BcInvoiceNumber!, out var bcRemaining)) continue;

                if (inv.Status == InvoiceStatus.Paid && bcRemaining > 0)
                {
                    inv.Status = InvoiceStatus.Downpayment;
                    needsSave = true;
                    _logger.LogInformation("Auto-corrected invoice {Id} ({Num}) from Paid to Downpayment — BC remaining: {Rem}",
                        inv.Id, inv.BcInvoiceNumber, bcRemaining);
                }
                else if (inv.Status == InvoiceStatus.Downpayment && bcRemaining == 0)
                {
                    inv.Status = InvoiceStatus.Paid;
                    needsSave = true;
                    _logger.LogInformation("Auto-corrected invoice {Id} ({Num}) from Downpayment to Paid — BC remaining: 0",
                        inv.Id, inv.BcInvoiceNumber);
                }
            }
            if (needsSave)
            {
                try { await _db.SaveChangesAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to save auto-corrected invoice statuses"); }
            }
        }

        // Build credit note lookup: original invoice ID -> list of credited lot numbers
        var creditNotesByOriginal = invoices
            .Where(i => i.IsCreditNote && i.OriginalInvoiceId != null)
            .GroupBy(i => i.OriginalInvoiceId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new {
                    Amount = g.Sum(cn => cn.TotalAmount),
                    LotNumbers = g.SelectMany(cn => cn.Lines.Select(l => l.LotNumber)).Distinct().ToHashSet()
                });

        var result = invoices.Select(i =>
        {
            var creditInfo = !i.IsCreditNote && creditNotesByOriginal.TryGetValue(i.Id, out var info) ? info : null;
            var invoiceLotNumbers = i.Lines.Select(l => l.LotNumber).Distinct().ToList();
            var uncreditedLots = creditInfo != null
                ? invoiceLotNumbers.Where(ln => !creditInfo.LotNumbers.Contains(ln)).ToList()
                : (i.IsCreditNote ? new List<int>() : invoiceLotNumbers);

            decimal? remainingBalance = null;
            if (!string.IsNullOrEmpty(i.BcInvoiceNumber) && bcRemainingMap.TryGetValue(i.BcInvoiceNumber, out var rem))
                remainingBalance = rem;

            return new
            {
                i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
                i.TotalAmount, i.Currency, Status = i.Status.ToString(),
                BrokerName = i.Broker?.CompanyName, BuyerName = i.Buyer?.Name, BuyerNumber = i.Buyer?.BuyerNumber, LinesCount = i.Lines.Count,
                i.IsCreditNote, OriginalInvoiceNumber = i.OriginalInvoice?.InvoiceNumber,
                i.PdfUrl, i.BcInvoiceNumber, i.ShippingStatus,
                i.DownpaymentAmount, i.DownpaymentPercentage,
                RemainingBalance = remainingBalance,
                CreditedAmount = creditInfo?.Amount ?? 0m,
                CreditedLots = creditInfo?.LotNumbers.Count ?? 0,
                UncreditedLotNumbers = uncreditedLots,
                Lots = i.Lines.Select(l => new
                {
                    l.LotNumber, l.Description, l.Skins, l.PricePerSkin, SubTotal = l.HammerPrice,
                    IsCredited = creditInfo?.LotNumbers.Contains(l.LotNumber) ?? false
                }).ToList()
            };
        }).ToList();

        return await CreateJsonResponse(req, result);
    }

    [Function("GetInvoicePaymentHistory")]
    public async Task<HttpResponseData> GetInvoicePaymentHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices/{invoiceId:int}/payment-history")] HttpRequestData req, int invoiceId)
    {
        var invoice = await _db.Invoices.Include(i => i.Buyer).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (_bcClient == null || string.IsNullOrEmpty(invoice.BcInvoiceNumber))
        {
            return await CreateJsonResponse(req, new
            {
                invoiceNumber = invoice.InvoiceNumber,
                bcInvoiceNumber = invoice.BcInvoiceNumber,
                entries = Array.Empty<object>(),
                message = "BC not configured or invoice not pushed to BC"
            });
        }

        var customerNo = invoice.Buyer?.BuyerNumber ?? "";
        if (string.IsNullOrEmpty(customerNo))
        {
            return await CreateJsonResponse(req, new
            {
                invoiceNumber = invoice.InvoiceNumber,
                bcInvoiceNumber = invoice.BcInvoiceNumber,
                entries = Array.Empty<object>(),
                message = "Buyer has no customer number"
            });
        }

        try
        {
            var companyId = await _bcClient.ResolveCompanyIdAsync();

            // Get the invoice ledger entry for this specific invoice
            var invoiceEntries = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(companyId, customerNo, "Invoice", false);
            var invoiceEntry = invoiceEntries.FirstOrDefault(e => e.DocumentNo == invoice.BcInvoiceNumber);

            // Build summary from invoice ledger entry
            decimal amountApplied = 0;
            var history = new List<object>();

            if (invoiceEntry != null)
            {
                amountApplied = invoiceEntry.OriginalAmount - invoiceEntry.RemainingAmount;

                // Add a single "Total Applied" row showing how much has been paid on this invoice
                if (amountApplied > 0)
                {
                    history.Add(new
                    {
                        type = "Payment Applied",
                        documentNo = invoiceEntry.DocumentNo,
                        postingDate = invoiceEntry.ClosedAtDate != "" ? invoiceEntry.ClosedAtDate : invoiceEntry.PostingDate,
                        originalAmount = invoiceEntry.OriginalAmount,
                        amountApplied = amountApplied,
                        remainingAmount = invoiceEntry.RemainingAmount,
                        open = invoiceEntry.Open,
                        description = $"Total applied to invoice {invoiceEntry.DocumentNo}"
                    });
                }

                // Check for credit memos applied against this invoice (from our local DB)
                var creditNotes = await _db.Invoices
                    .Where(i => i.IsCreditNote && i.OriginalInvoiceId == invoice.Id && !string.IsNullOrEmpty(i.BcInvoiceNumber))
                    .Select(i => new { i.BcInvoiceNumber, i.TotalAmount, i.InvoiceDate })
                    .ToListAsync();

                foreach (var cn in creditNotes)
                {
                    history.Add(new
                    {
                        type = "Credit Memo",
                        documentNo = cn.BcInvoiceNumber ?? "",
                        postingDate = cn.InvoiceDate.ToString("yyyy-MM-dd"),
                        originalAmount = cn.TotalAmount,
                        amountApplied = cn.TotalAmount,
                        remainingAmount = 0m,
                        open = false,
                        description = $"Credit memo against {invoiceEntry.DocumentNo}"
                    });
                }
            }

            return await CreateJsonResponse(req, new
            {
                invoiceNumber = invoice.InvoiceNumber,
                bcInvoiceNumber = invoice.BcInvoiceNumber,
                customerNo,
                customerName = invoice.Buyer?.Name ?? "",
                invoiceTotal = invoice.TotalAmount,
                bcOriginalAmount = invoiceEntry?.OriginalAmount,
                bcAmountApplied = amountApplied,
                bcRemainingAmount = invoiceEntry?.RemainingAmount,
                bcOpen = invoiceEntry?.Open,
                entries = history
            });
        }
        catch (Exception ex)
        {
            return await CreateJsonResponse(req, new
            {
                invoiceNumber = invoice.InvoiceNumber,
                bcInvoiceNumber = invoice.BcInvoiceNumber,
                entries = Array.Empty<object>(),
                message = $"Failed to fetch BC data: {ex.Message}"
            });
        }
    }

    [Function("GetInvoicesByBrokerAndBuyer")]
    public async Task<HttpResponseData> GetInvoicesByBrokerAndBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices/broker/{brokerId:int}/buyer/{buyerId:int}")] HttpRequestData req, int brokerId, int buyerId)
    {
        var invoices = await _db.Invoices
            .Where(i => i.BrokerId == brokerId && i.BuyerId == buyerId)
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => new
            {
                i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
                i.TotalAmount, i.Currency, Status = i.Status.ToString(),
                BuyerName = i.Buyer.Name, LinesCount = i.Lines.Count,
                i.IsCreditNote, OriginalInvoiceNumber = i.OriginalInvoice != null ? i.OriginalInvoice.InvoiceNumber : null,
                i.PdfUrl, i.BcInvoiceNumber
            }).ToListAsync();
        return await CreateJsonResponse(req, invoices);
    }

    [Function("GetUnpushedInvoices")]
    public async Task<HttpResponseData> GetUnpushedInvoices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices/unpushed")] HttpRequestData req)
    {
        var invoices = await _db.Invoices
            .Where(i => !i.IsCreditNote && (i.BcInvoiceNumber == null || i.BcInvoiceNumber == ""))
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => new
            {
                i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
                i.TotalAmount, i.Currency, Status = i.Status.ToString(),
                BrokerName = i.Broker.CompanyName, BuyerName = i.Buyer.Name, LinesCount = i.Lines.Count
            }).ToListAsync();
        return await CreateJsonResponse(req, invoices);
    }

    [Function("GetUnpushedCreditNotes")]
    public async Task<HttpResponseData> GetUnpushedCreditNotes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/credit-notes/unpushed")] HttpRequestData req)
    {
        var creditNotes = await _db.Invoices
            .Where(i => i.IsCreditNote && (i.BcInvoiceNumber == null || i.BcInvoiceNumber == ""))
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => new
            {
                i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
                i.TotalAmount, i.Currency, Status = i.Status.ToString(),
                BrokerName = i.Broker.CompanyName, BuyerName = i.Buyer.Name, LinesCount = i.Lines.Count
            }).ToListAsync();
        return await CreateJsonResponse(req, creditNotes);
    }

    [Function("GetInvoiceLinksForResults")]
    public async Task<HttpResponseData> GetInvoiceLinksForResults(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoice-links/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        // Get auction result IDs for this broker
        var brokerResultIds = await _db.AuctionResults
            .Where(r => r.BrokerId == brokerId)
            .Select(r => r.Id)
            .ToListAsync();

        // Get all invoice lines for those results (regardless of invoice broker)
        var lines = await _db.Set<InvoiceLine>()
            .Where(l => brokerResultIds.Contains(l.AuctionResultId))
            .Select(l => new
            {
                l.AuctionResultId,
                InvoiceId = l.Invoice.Id,
                Number = l.Invoice.InvoiceNumber,
                PdfUrl = l.Invoice.PdfUrl,
                l.Invoice.IsCreditNote
            })
            .ToListAsync();

        var resultMap = lines.GroupBy(l => l.AuctionResultId).ToDictionary(
            g => g.Key,
            g => new
            {
                Documents = g.DistinctBy(l => l.InvoiceId)
                    .OrderBy(l => l.InvoiceId)
                    .Select(l => new
                    {
                        Id = l.InvoiceId,
                        l.Number,
                        l.PdfUrl,
                        l.IsCreditNote
                    })
                    .ToList()
            });

        return await CreateJsonResponse(req, resultMap);
    }

    [Function("GetAllSettlements")]
    public async Task<HttpResponseData> GetAllSettlements(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements")] HttpRequestData req)
    {
        var settlements = await _db.Settlements.Include(s => s.Farmer).Include(s => s.Lot)
            .OrderByDescending(s => s.CreatedAt).ToListAsync();
        return await CreateJsonResponse(req, settlements);
    }

    [Function("MarkSettlementComplete")]
    public async Task<HttpResponseData> MarkComplete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settlements/{settlementId:int}/complete")] HttpRequestData req, int settlementId)
    {
        var settlement = await _service.MarkSettlementCompletedAsync(settlementId);
        if (settlement == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, settlement);
    }

    private async Task<string> ApplyPaymentToBcAsync(Invoice invoice, decimal? partialAmount = null)
    {
        var companyId = await _bcClient!.ResolveCompanyIdAsync();

        // Find the buyer's BC customer number
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == invoice.BuyerId);
        var customerNumber = buyer?.BuyerNumber?.ToString() ?? "";

        if (string.IsNullOrEmpty(customerNumber))
            throw new InvalidOperationException("Buyer has no customer number for BC lookup");

        // Check if this invoice is already fully paid in BC (remaining = 0 or not open)
        var invoiceEntries = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(companyId, customerNumber, "Invoice", false);
        var bcInvoiceEntry = invoiceEntries.FirstOrDefault(e => e.DocumentNo == invoice.BcInvoiceNumber);
        if (bcInvoiceEntry != null && !bcInvoiceEntry.Open)
        {
            _logger.LogInformation("Invoice {BcNumber} is already closed in BC (remaining={Remaining}), skipping payment application",
                invoice.BcInvoiceNumber, bcInvoiceEntry.RemainingAmount);
            return $"Invoice already fully paid in BC (no application needed).";
        }

        // Find open payment entries for this customer
        var payments = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(companyId, customerNumber, "Payment", true);
        if (payments.Count == 0)
            throw new InvalidOperationException($"No open payment found for customer {customerNumber} in BC");

        // Use the first open payment entry
        var paymentEntry = payments.First();

        // Apply the payment to the invoice via the custom API
        // BC extension handles posting date automatically (uses max of today, payment date, invoice date)
        var amountToApply = partialAmount ?? 0; // 0 means full invoice amount (handled by AL)
        var result = await _bcClient.ApplyPaymentToInvoiceAsync(
            companyId,
            customerNumber,
            paymentEntry.EntryNo,
            invoice.BcInvoiceNumber!,
            amountToApply);

        if (result.ResultStatus == "Error")
            throw new InvalidOperationException($"BC payment application failed: {result.ResultMessage}");

        _logger.LogInformation("Applied payment entry #{EntryNo} to invoice {BcNumber}, amount {Amount}",
            paymentEntry.EntryNo, invoice.BcInvoiceNumber, result.AmountToApply);

        return result.ResultMessage;
    }

    [Function("DiagnosePaymentApplication")]
    public async Task<HttpResponseData> DiagnosePaymentApplication(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/diagnose-payment/{customerNo}/{invoiceDocNo}")] HttpRequestData req,
        string customerNo, string invoiceDocNo)
    {
        if (_bcClient == null)
            return await CreateJsonResponse(req, new { error = "BC not configured" });

        try
        {
            var companyId = await _bcClient.ResolveCompanyIdAsync();

            // Step 1: Find all open payment entries for this customer
            var payments = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(companyId, customerNo, "Payment", true);

            // Step 2: Find all invoice entries (open and closed) for this customer
            var allInvoices = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(companyId, customerNo, "Invoice", false);
            var targetInvoice = allInvoices.FirstOrDefault(e => e.DocumentNo == invoiceDocNo);

            // Step 3: Prepare diagnostic info
            var diag = new
            {
                customer = customerNo,
                invoiceDocNo,
                invoiceFound = targetInvoice != null,
                invoiceOpen = targetInvoice?.Open,
                invoicePostingDate = targetInvoice?.PostingDate,
                invoiceOriginalAmount = targetInvoice?.OriginalAmount,
                invoiceRemainingAmount = targetInvoice?.RemainingAmount,
                invoiceEntryNo = targetInvoice?.EntryNo,
                openPayments = payments.Select(p => new
                {
                    p.EntryNo,
                    p.DocumentNo,
                    p.PostingDate,
                    p.OriginalAmount,
                    p.RemainingAmount,
                    p.Open
                }).ToList(),
                openPaymentCount = payments.Count,
                totalAvailablePayment = payments.Sum(p => Math.Abs(p.RemainingAmount)),
                selectedPaymentEntryNo = payments.FirstOrDefault()?.EntryNo,
                amountToApplyWouldBe = 0, // 0 = full invoice amount in AL
                note = "To test the actual BC application, POST to this same URL"
            };

            return await CreateJsonResponse(req, diag);
        }
        catch (Exception ex)
        {
            return await CreateJsonResponse(req, new { error = ex.Message, stack = ex.StackTrace });
        }
    }

    [Function("TestPaymentApplication")]
    public async Task<HttpResponseData> TestPaymentApplication(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settlements/diagnose-payment/{customerNo}/{invoiceDocNo}")] HttpRequestData req,
        string customerNo, string invoiceDocNo)
    {
        if (_bcClient == null)
            return await CreateJsonResponse(req, new { error = "BC not configured" });

        try
        {
            var companyId = await _bcClient.ResolveCompanyIdAsync();

            // Find open payments
            var payments = await _bcClient.GetCustomerLedgerEntriesByCustomerAsync(companyId, customerNo, "Payment", true);
            if (payments.Count == 0)
                return await CreateJsonResponse(req, new { error = $"No open payment entries for customer {customerNo}" });

            var paymentEntry = payments.First();

            // Try the actual application
            var result = await _bcClient.ApplyPaymentToInvoiceAsync(
                companyId, customerNo, paymentEntry.EntryNo, invoiceDocNo, 0);

            return await CreateJsonResponse(req, new
            {
                success = result.ResultStatus != "Error",
                resultStatus = result.ResultStatus,
                resultMessage = result.ResultMessage,
                amountApplied = result.AmountToApply,
                paymentEntryUsed = paymentEntry.EntryNo,
                paymentDocNo = paymentEntry.DocumentNo,
                paymentPostingDate = paymentEntry.PostingDate,
                paymentRemaining = paymentEntry.RemainingAmount
            });
        }
        catch (Exception ex)
        {
            return await CreateJsonResponse(req, new
            {
                success = false,
                error = ex.Message,
                errorType = ex.GetType().Name,
                innerError = ex.InnerException?.Message
            });
        }
    }

    private static async Task<HttpResponseData> CreateJsonResponse<T>(
        HttpRequestData req, T data, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, JsonOptions));
        return response;
    }
}


