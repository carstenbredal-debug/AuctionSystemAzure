using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class SettlementFunctions
{
    private readonly AuctionDbContext _db;
    private readonly SettlementService _service;
    private readonly ILogger<SettlementFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SettlementFunctions(AuctionDbContext db, SettlementService service, ILogger<SettlementFunctions> logger)
    {
        _db = db;
        _service = service;
        _logger = logger;
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

        var invoice = await _service.UpdateInvoiceStatusAsync(invoiceId, status);
        if (invoice == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);
        return await CreateJsonResponse(req, new { invoice.Id, Status = invoice.Status.ToString() });
    }

    private class UpdateStatusRequest { public string Status { get; set; } = ""; }

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
        }

        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, new { invoice.Id, Status = invoice.Status.ToString(), invoice.ShippingStatus, invoice.DownpaymentAmount, invoice.DownpaymentPercentage });
    }

    private class DownpaymentRequest
    {
        public decimal Amount { get; set; }
        public bool IsPercentage { get; set; }
        public bool ReleaseForShipping { get; set; }
    }

    [Function("GetShippingBoxes")]
    public async Task<HttpResponseData> GetShippingBoxes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shipping/boxes")] HttpRequestData req)
    {
        // Get invoices released for shipping
        var releasedInvoices = await _db.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Broker)
            .Include(i => i.Buyer)
            .Where(i => i.ShippingStatus == "Released" && !i.IsCreditNote)
            .ToListAsync();

        // Build credit note lookup to find credited lots
        var creditNotes = await _db.Invoices
            .Include(i => i.Lines)
            .Where(i => i.IsCreditNote && i.OriginalInvoiceId != null)
            .ToListAsync();

        var creditedLotsByInvoice = creditNotes
            .GroupBy(cn => cn.OriginalInvoiceId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(cn => cn.Lines.Select(l => l.LotNumber)).Distinct().ToHashSet());

        // Get uncredited lot numbers for each released invoice
        var shippingLots = new List<object>();
        foreach (var inv in releasedInvoices)
        {
            var creditedLots = creditedLotsByInvoice.GetValueOrDefault(inv.Id) ?? new HashSet<int>();
            var uncreditedLines = inv.Lines.Where(l => !creditedLots.Contains(l.LotNumber)).ToList();
            foreach (var line in uncreditedLines)
            {
                shippingLots.Add(new
                {
                    InvoiceId = inv.Id,
                    inv.InvoiceNumber,
                    BrokerName = inv.Broker?.CompanyName,
                    BuyerName = inv.Buyer?.Name,
                    line.LotNumber,
                    line.Skins,
                    line.PricePerSkin,
                    HammerPrice = line.HammerPrice
                });
            }
        }

        return await CreateJsonResponse(req, shippingLots);
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

            return new
            {
                i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
                i.TotalAmount, i.Currency, Status = i.Status.ToString(),
                BrokerName = i.Broker?.CompanyName, BuyerName = i.Buyer?.Name, LinesCount = i.Lines.Count,
                i.IsCreditNote, OriginalInvoiceNumber = i.OriginalInvoice?.InvoiceNumber,
                i.PdfUrl, i.BcInvoiceNumber, i.ShippingStatus,
                i.DownpaymentAmount, i.DownpaymentPercentage,
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

    private static async Task<HttpResponseData> CreateJsonResponse<T>(
        HttpRequestData req, T data, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(data, JsonOptions));
        return response;
    }
}


