using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
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

    [Function("GetSettlementsBySeller")]
    public async Task<HttpResponseData> GetBySeller(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/seller/{sellerId:int}")] HttpRequestData req, int sellerId)
    {
        var settlements = await _service.GetSettlementsBySellerAsync(sellerId);
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
            .Include(i => i.Lines).Include(i => i.Broker).Include(i => i.OriginalInvoice)
            .OrderByDescending(i => i.InvoiceDate).ToListAsync();
        return await CreateJsonResponse(req, invoices.Select(i => new
        {
            i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
            i.TotalAmount, i.Currency, Status = i.Status.ToString(),
            BrokerName = i.Broker?.CompanyName, LinesCount = i.Lines.Count,
            i.IsCreditNote, OriginalInvoiceNumber = i.OriginalInvoice?.InvoiceNumber,
            i.PdfUrl
        }));
    }

    [Function("GetAllInvoices")]
    public async Task<HttpResponseData> GetAllInvoices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoices")] HttpRequestData req)
    {
        var invoices = await _db.Invoices.Include(i => i.Broker).Include(i => i.Buyer).Include(i => i.Lines).Include(i => i.OriginalInvoice)
            .OrderByDescending(i => i.InvoiceDate).ToListAsync();
        return await CreateJsonResponse(req, invoices.Select(i => new
        {
            i.Id, i.InvoiceNumber, i.InvoiceDate, i.SubTotal, i.AuctionFee, i.Commission,
            i.TotalAmount, i.Currency, Status = i.Status.ToString(),
            BrokerName = i.Broker?.CompanyName, BuyerName = i.Buyer?.Name, LinesCount = i.Lines.Count,
            i.IsCreditNote, OriginalInvoiceNumber = i.OriginalInvoice?.InvoiceNumber,
            i.PdfUrl
        }));
    }

    [Function("GetInvoiceLinksForResults")]
    public async Task<HttpResponseData> GetInvoiceLinksForResults(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements/invoice-links/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var lines = await _db.Set<InvoiceLine>()
            .Include(l => l.Invoice)
            .Where(l => l.Invoice.BrokerId == brokerId)
            .ToListAsync();

        var resultMap = lines.GroupBy(l => l.AuctionResultId).ToDictionary(
            g => g.Key,
            g =>
            {
                var invoice = g.FirstOrDefault(l => !l.Invoice.IsCreditNote)?.Invoice;
                var creditNote = g.FirstOrDefault(l => l.Invoice.IsCreditNote)?.Invoice;
                return new
                {
                    InvoiceId = invoice?.Id,
                    InvoiceNumber = invoice?.InvoiceNumber,
                    InvoicePdfUrl = invoice?.PdfUrl,
                    CreditNoteId = creditNote?.Id,
                    CreditNoteNumber = creditNote?.InvoiceNumber,
                    CreditNotePdfUrl = creditNote?.PdfUrl
                };
            });

        return await CreateJsonResponse(req, resultMap);
    }

    [Function("GetAllSettlements")]
    public async Task<HttpResponseData> GetAllSettlements(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settlements")] HttpRequestData req)
    {
        var settlements = await _db.Settlements.Include(s => s.Seller).Include(s => s.Lot)
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


