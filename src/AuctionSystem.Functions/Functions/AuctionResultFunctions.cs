using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class AuctionResultFunctions
{
    private readonly AuctionDbContext _db;
    private readonly ILogger<AuctionResultFunctions> _logger;
    private readonly BlobStorageService? _blobStorage;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AuctionResultFunctions(AuctionDbContext db, ILogger<AuctionResultFunctions> logger, BlobStorageService? blobStorage = null)
    {
        _db = db;
        _logger = logger;
        _blobStorage = blobStorage;
    }

    [Function("SubmitAuctionResult")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SubmitAuctionResultRequest>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var broker = await _db.Brokers.FindAsync(body.BrokerId);
        if (broker == null)
        {
            var response = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { error = "Broker not found" }, JsonOptions));
            return response;
        }

        // Look up lot in auction.Lots
        var auctionLot = await _db.Lots
            .FirstOrDefaultAsync(l => l.LotNumber == body.LotNumber);

        var result = new AuctionResult
        {
            LotNumber = body.LotNumber,
            BrokerId = body.BrokerId,
            PriceEur = body.PriceEur,
            SalesType = auctionLot?.Description?.Split(' ').FirstOrDefault(),
            Gender = auctionLot != null ? ParseField(auctionLot.Description, 1) : null,
            Group = auctionLot?.Category,
            Color = auctionLot != null ? ParseField(auctionLot.Description, 2) : null,
            Quality = auctionLot != null ? ParseField(auctionLot.Description, 3) : null,
            TotalSkins = auctionLot?.Quantity ?? 0,
            Processed = false,
            ReceivedAt = DateTime.UtcNow
        };

        _db.AuctionResults.Add(result);
        await _db.SaveChangesAsync();

        await ProcessAuctionResult(result, auctionLot);

        var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        await resp.WriteStringAsync(JsonSerializer.Serialize(new
        {
            resultId = result.Id,
            lotNumber = result.LotNumber,
            brokerId = result.BrokerId,
            priceEur = result.PriceEur,
            processed = result.Processed,
            lotFound = auctionLot != null,
            salesType = result.SalesType,
            gender = result.Gender,
            group = result.Group,
            color = result.Color,
            quality = result.Quality,
            totalSkins = result.TotalSkins
        }, JsonOptions));
        return resp;
    }

    private static string? ParseField(string? description, int index)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var parts = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? parts[index] : null;
    }

    private async Task ProcessAuctionResult(AuctionResult result, Lot? existingLot)
    {
        try
        {
            if (existingLot == null)
            {
                var activeAuction = await _db.Auctions
                    .Where(a => a.Status == AuctionStatus.Active)
                    .OrderByDescending(a => a.ScheduledDate)
                    .FirstOrDefaultAsync();

                if (activeAuction == null)
                {
                    activeAuction = await _db.Auctions
                        .OrderByDescending(a => a.Id)
                        .FirstOrDefaultAsync();
                }

                if (activeAuction == null)
                {
                    _logger.LogWarning("No auction found to assign lot {LotNumber}", result.LotNumber);
                    return;
                }

                existingLot = new Lot
                {
                    AuctionId = activeAuction.Id,
                    LotNumber = result.LotNumber,
                    Description = $"Lot {result.LotNumber}",
                    Quantity = 1,
                    Unit = "skins",
                    StartingPrice = 0,
                    HammerPrice = result.PriceEur,
                    Status = LotStatus.Broker
                };
                _db.Lots.Add(existingLot);
                await _db.SaveChangesAsync();
            }
            else
            {
                existingLot.HammerPrice = result.PriceEur;
                existingLot.Status = LotStatus.Broker;
                await _db.SaveChangesAsync();
            }

            var existingAllocation = await _db.LotAllocations
                .FirstOrDefaultAsync(a => a.LotId == existingLot.Id && a.BrokerId == result.BrokerId);

            if (existingAllocation == null)
            {
                var defaultBuyer = await _db.Buyers
                    .FirstOrDefaultAsync(b => b.BrokerId == result.BrokerId);

                if (defaultBuyer == null)
                {
                    defaultBuyer = await _db.Buyers.FirstOrDefaultAsync();
                }

                if (defaultBuyer == null)
                {
                    _logger.LogWarning("No buyer found to create allocation for lot {LotNumber}", result.LotNumber);
                    return;
                }

                var allocation = new LotAllocation
                {
                    LotId = existingLot.Id,
                    BrokerId = result.BrokerId,
                    BuyerId = defaultBuyer.Id,
                    Quantity = existingLot.Quantity,
                    PricePerUnit = existingLot.Quantity > 0 ? result.PriceEur / existingLot.Quantity : result.PriceEur,
                    TotalPrice = result.PriceEur,
                    Status = AllocationStatus.Allocated,
                    AllocatedAt = DateTime.UtcNow
                };
                _db.LotAllocations.Add(allocation);
            }

            result.Processed = true;
            result.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Processed auction result: Lot {LotNumber} assigned to Broker {BrokerId} at {Price} EUR",
                result.LotNumber, result.BrokerId, result.PriceEur);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process auction result for lot {LotNumber}", result.LotNumber);
        }
    }

    private static object ProjectResult(AuctionResult r) => new
    {
        r.Id,
        r.LotNumber,
        r.BrokerId,
        brokerName = r.Broker?.CompanyName,
        brokerNumber = r.Broker?.BrokerNumber,
        r.PriceEur,
        r.SalesType,
        r.Gender,
        r.Group,
        r.Color,
        r.Quality,
        r.Size,
        r.Clarity,
        r.HairLength,
        r.TotalSkins,
        r.BoxCount,
        r.Processed,
        r.ReceivedAt,
        r.ProcessedAt,
        r.SoldToBuyerId,
        soldToBuyerName = r.SoldToBuyer?.Name,
        soldToBuyerNumber = r.SoldToBuyer?.BuyerNumber,
        r.SoldAt,
        r.CommissionType,
        r.CommissionValue,
        r.CommissionAmount
    };

    [Function("GetAuctionResults")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results")] HttpRequestData req)
    {
        var results = await _db.AuctionResults
            .OrderByDescending(r => r.ReceivedAt)
            .Select(r => new
            {
                r.Id, r.LotNumber, r.BrokerId,
                brokerName = r.Broker.CompanyName,
                brokerNumber = r.Broker.BrokerNumber,
                r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
                r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
                r.Processed, r.ReceivedAt, r.ProcessedAt,
                r.SoldToBuyerId,
                soldToBuyerName = r.SoldToBuyer != null ? r.SoldToBuyer.Name : null,
                soldToBuyerNumber = r.SoldToBuyer != null ? r.SoldToBuyer.BuyerNumber : null,
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results, JsonOptions));
        return response;
    }

    [Function("GetAuctionResultsByBroker")]
    public async Task<HttpResponseData> GetByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var results = await _db.AuctionResults
            .Where(r => r.BrokerId == brokerId)
            .OrderByDescending(r => r.ReceivedAt)
            .Select(r => new
            {
                r.Id, r.LotNumber, r.BrokerId,
                brokerName = r.Broker.CompanyName,
                brokerNumber = r.Broker.BrokerNumber,
                r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
                r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
                r.Processed, r.ReceivedAt, r.ProcessedAt,
                r.SoldToBuyerId,
                soldToBuyerName = r.SoldToBuyer != null ? r.SoldToBuyer.Name : null,
                soldToBuyerNumber = r.SoldToBuyer != null ? r.SoldToBuyer.BuyerNumber : null,
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results, JsonOptions));
        return response;
    }

    [Function("GetNextUnsoldLot")]
    public async Task<HttpResponseData> GetNextUnsoldLot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/next-unsold-lot")] HttpRequestData req)
    {
        var nextLot = await _db.Lots
            .Where(l => l.Status == LotStatus.Pending || l.Status == LotStatus.Active)
            .OrderBy(l => l.LotNumber)
            .Select(l => new
            {
                l.LotNumber,
                l.Description,
                l.Category,
                l.Quantity,
                l.Unit
            })
            .FirstOrDefaultAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(nextLot, JsonOptions));
        return response;
    }

    [Function("SellLotsToBuyer")]
    public async Task<HttpResponseData> SellToBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/sell-to-buyer")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<SellToBuyerRequest>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0 || body.BuyerId <= 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var buyer = await _db.Buyers.FindAsync(body.BuyerId);
        if (buyer == null)
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Buyer not found" }, JsonOptions));
            return resp;
        }

        var results = await _db.AuctionResults
            .Where(r => body.AuctionResultIds.Contains(r.Id) && r.SoldToBuyerId == null)
            .ToListAsync();

        foreach (var result in results)
        {
            result.SoldToBuyerId = body.BuyerId;
            result.SoldAt = DateTime.UtcNow;
            result.CommissionType = body.CommissionType;
            result.CommissionValue = body.CommissionValue;
            if (body.CommissionType == "percentage" && body.CommissionValue.HasValue)
            {
                var hammerPrice = result.TotalSkins * result.PriceEur;
                result.CommissionAmount = hammerPrice * body.CommissionValue.Value / 100m;
            }
            else if (body.CommissionType == "amount" && body.CommissionValue.HasValue)
            {
                result.CommissionAmount = body.CommissionValue.Value;
            }
        }

        // Update lot status to Sold
        var lotNumbers = results.Select(r => r.LotNumber).ToList();
        var lots = await _db.Lots.Where(l => lotNumbers.Contains(l.LotNumber)).ToListAsync();
        foreach (var lot in lots)
        {
            lot.Status = LotStatus.Sold;
        }

        await _db.SaveChangesAsync();

        // Generate invoice
        int? invoiceId = null;
        if (results.Count > 0)
        {
            try
            {
                var brokerId = results.First().BrokerId;
                var auctionFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "AuctionFee");
                var handlingFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "HandlingFee");
                var auctionFeePercent = auctionFeeParam != null ? decimal.Parse(auctionFeeParam.Value, CultureInfo.InvariantCulture) : 0m;
                var handlingFeePerSkin = handlingFeeParam != null ? decimal.Parse(handlingFeeParam.Value, CultureInfo.InvariantCulture) : 0m;

                var invoiceCount = await _db.Invoices.CountAsync();
                var invoice = new Invoice
                {
                    InvoiceNumber = $"INV-{DateTime.UtcNow:yyyyMMdd}-{invoiceCount + 1:D5}",
                    InvoiceDate = DateTime.UtcNow,
                    BrokerId = brokerId,
                    BuyerId = body.BuyerId,
                    Status = InvoiceStatus.Issued
                };

                decimal subTotal = 0;
                decimal totalAuctionFee = 0;
                decimal totalCommission = 0;

                foreach (var r in results)
                {
                    var hammerPrice = r.TotalSkins * r.PriceEur;
                    var handlingFee = r.TotalSkins * handlingFeePerSkin;
                    var lotAuctionFee = (hammerPrice + handlingFee) * auctionFeePercent / 100m;
                    var description = string.Join(", ", new[] { r.SalesType, r.Gender, r.Group, r.Color, r.Quality, r.Size }.Where(s => !string.IsNullOrEmpty(s)));

                    invoice.Lines.Add(new InvoiceLine
                    {
                        LotNumber = r.LotNumber,
                        Description = description,
                        Skins = r.TotalSkins,
                        PricePerSkin = r.PriceEur,
                        HammerPrice = hammerPrice,
                        AuctionResultId = r.Id
                    });

                    subTotal += hammerPrice;
                    totalAuctionFee += lotAuctionFee;
                    totalCommission += r.CommissionAmount ?? 0;
                }

                invoice.SubTotal = subTotal;
                invoice.AuctionFee = totalAuctionFee;
                invoice.Commission = totalCommission;
                invoice.TotalAmount = subTotal + totalAuctionFee + totalCommission;

                // Load buyer for PDF
                invoice.Buyer = buyer;

                var pdfBytes = InvoicePdfService.GeneratePdf(invoice);
                invoice.PdfData = pdfBytes;

                _db.Invoices.Add(invoice);
                await _db.SaveChangesAsync();
                invoiceId = invoice.Id;

                // Upload to blob storage (non-critical)
                if (_blobStorage != null)
                {
                    try
                    {
                        var fileName = $"{invoice.InvoiceNumber}.pdf";
                        invoice.PdfUrl = await _blobStorage.UploadPdfAsync(fileName, pdfBytes);
                        await _db.SaveChangesAsync();
                    }
                    catch (Exception blobEx)
                    {
                        _logger.LogWarning(blobEx, "Failed to upload invoice PDF to blob storage");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate invoice for {Count} lots", results.Count);
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { soldCount = results.Count, invoiceId }, JsonOptions));
        return response;
    }

    [Function("GetAuctionResultsByBuyer")]
    public async Task<HttpResponseData> GetByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/buyer/{buyerId:int}")] HttpRequestData req, int buyerId)
    {
        var results = await _db.AuctionResults
            .Where(r => r.SoldToBuyerId == buyerId)
            .OrderByDescending(r => r.SoldAt)
            .Select(r => new
            {
                r.Id, r.LotNumber, r.BrokerId,
                brokerName = r.Broker.CompanyName,
                brokerNumber = r.Broker.BrokerNumber,
                r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
                r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
                r.Processed, r.ReceivedAt, r.ProcessedAt,
                r.SoldToBuyerId,
                soldToBuyerName = r.SoldToBuyer != null ? r.SoldToBuyer.Name : null,
                soldToBuyerNumber = r.SoldToBuyer != null ? r.SoldToBuyer.BuyerNumber : null,
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(results, JsonOptions));
        return response;
    }
    [Function("RequestTakeback")]
    public async Task<HttpResponseData> RequestTakeback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "takeback-requests")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<TakebackRequestBody>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var results = await _db.AuctionResults
            .Where(r => body.AuctionResultIds.Contains(r.Id) && r.SoldToBuyerId != null)
            .ToListAsync();

        var created = new List<TakebackRequest>();
        var takenBackResultIds = new List<int>();
        foreach (var result in results)
        {
            var existing = await _db.TakebackRequests
                .FirstOrDefaultAsync(t => t.AuctionResultId == result.Id && t.Status == CustomerRequestStatus.Pending);
            if (existing != null) continue;

            var takebackReq = new TakebackRequest
            {
                AuctionResultId = result.Id,
                BrokerId = result.BrokerId,
                BuyerId = result.SoldToBuyerId!.Value,
                InitiatedBy = "Broker",
                Status = CustomerRequestStatus.Approved,
                RequestedAt = DateTime.UtcNow,
                RespondedAt = DateTime.UtcNow
            };
            _db.TakebackRequests.Add(takebackReq);
            created.Add(takebackReq);
            takenBackResultIds.Add(result.Id);

            result.SoldToBuyerId = null;
            result.SoldAt = null;

            // Update lot status back to Broker
            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == result.LotNumber);
            if (lot != null) lot.Status = LotStatus.Broker;
        }

        await _db.SaveChangesAsync();

        int? creditNoteId = null;
        if (takenBackResultIds.Count > 0)
        {
            try
            {
                creditNoteId = await GenerateCreditNoteAsync(takenBackResultIds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate credit note");
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { requestCount = created.Count, creditNoteId }, JsonOptions));
        return response;
    }

    [Function("RequestTakebackByBuyer")]
    public async Task<HttpResponseData> RequestTakebackByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "takeback-requests/buyer-initiated")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<TakebackRequestBody>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var results = await _db.AuctionResults
            .Where(r => body.AuctionResultIds.Contains(r.Id) && r.SoldToBuyerId != null)
            .ToListAsync();

        var created = new List<TakebackRequest>();
        foreach (var result in results)
        {
            var existing = await _db.TakebackRequests
                .FirstOrDefaultAsync(t => t.AuctionResultId == result.Id && t.Status == CustomerRequestStatus.Pending);
            if (existing != null) continue;

            var takebackReq = new TakebackRequest
            {
                AuctionResultId = result.Id,
                BrokerId = result.BrokerId,
                BuyerId = result.SoldToBuyerId!.Value,
                InitiatedBy = "Buyer",
                Status = CustomerRequestStatus.Pending,
                RequestedAt = DateTime.UtcNow
            };
            _db.TakebackRequests.Add(takebackReq);
            created.Add(takebackReq);
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { requestCount = created.Count }, JsonOptions));
        return response;
    }

    [Function("GetTakebackRequestsByBuyer")]
    public async Task<HttpResponseData> GetTakebacksByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "takeback-requests/buyer/{buyerId:int}")] HttpRequestData req, int buyerId)
    {
        var requests = await _db.TakebackRequests
            .Include(t => t.AuctionResult)
            .Include(t => t.Broker)
            .Include(t => t.Buyer)
            .Where(t => t.BuyerId == buyerId)
            .OrderByDescending(t => t.RequestedAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(requests.Select(t => new
        {
            t.Id,
            t.AuctionResultId,
            lotNumber = t.AuctionResult.LotNumber,
            salesType = t.AuctionResult.SalesType,
            color = t.AuctionResult.Color,
            totalSkins = t.AuctionResult.TotalSkins,
            priceEur = t.AuctionResult.PriceEur,
            brokerName = t.Broker.CompanyName,
            brokerNumber = t.Broker.BrokerNumber,
            t.InitiatedBy,
            t.Status,
            t.RequestedAt,
            t.RespondedAt
        }), JsonOptions));
        return response;
    }

    [Function("GetTakebackRequestsByBroker")]
    public async Task<HttpResponseData> GetTakebacksByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "takeback-requests/broker/{brokerId:int}")] HttpRequestData req, int brokerId)
    {
        var requests = await _db.TakebackRequests
            .Include(t => t.AuctionResult).ThenInclude(a => a.SoldToBuyer)
            .Include(t => t.Broker)
            .Include(t => t.Buyer)
            .Where(t => t.BrokerId == brokerId)
            .OrderByDescending(t => t.RequestedAt)
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(requests.Select(t => new
        {
            t.Id,
            t.AuctionResultId,
            lotNumber = t.AuctionResult.LotNumber,
            salesType = t.AuctionResult.SalesType,
            gender = t.AuctionResult.Gender,
            color = t.AuctionResult.Color,
            quality = t.AuctionResult.Quality,
            totalSkins = t.AuctionResult.TotalSkins,
            priceEur = t.AuctionResult.PriceEur,
            buyerName = t.Buyer.Name,
            buyerNumber = t.Buyer.BuyerNumber,
            t.InitiatedBy,
            t.Status,
            t.RequestedAt,
            t.RespondedAt
        }), JsonOptions));
        return response;
    }

    [Function("RespondTakebackRequest")]
    public async Task<HttpResponseData> RespondTakeback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "takeback-requests/{id:int}")] HttpRequestData req, int id)
    {
        var body = await req.ReadFromJsonAsync<TakebackResponseBody>();
        if (body == null)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var takebackReq = await _db.TakebackRequests
            .Include(t => t.AuctionResult)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (takebackReq == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        if (takebackReq.Status != CustomerRequestStatus.Pending)
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Request already responded to" }, JsonOptions));
            return resp;
        }

        takebackReq.Status = body.Approve ? CustomerRequestStatus.Approved : CustomerRequestStatus.Declined;
        takebackReq.RespondedAt = DateTime.UtcNow;

        int? creditNoteId = null;
        if (body.Approve)
        {
            takebackReq.AuctionResult.SoldToBuyerId = null;
            takebackReq.AuctionResult.SoldAt = null;

            // Update lot status back to Broker
            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == takebackReq.AuctionResult.LotNumber);
            if (lot != null) lot.Status = LotStatus.Broker;
        }

        await _db.SaveChangesAsync();

        if (body.Approve)
        {
            try
            {
                creditNoteId = await GenerateCreditNoteAsync(new List<int> { takebackReq.AuctionResultId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate credit note for takeback approval");
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { status = takebackReq.Status.ToString(), creditNoteId }, JsonOptions));
        return response;
    }

    private async Task<int?> GenerateCreditNoteAsync(List<int> auctionResultIds)
    {
        var invoiceLines = await _db.Set<InvoiceLine>()
            .Include(l => l.Invoice).ThenInclude(i => i.Buyer)
            .Include(l => l.Invoice).ThenInclude(i => i.Broker)
            .Where(l => auctionResultIds.Contains(l.AuctionResultId) && !l.Invoice.IsCreditNote)
            .ToListAsync();

        if (invoiceLines.Count == 0) return null;

        var originalInvoice = invoiceLines.First().Invoice;

        // Use auction result's broker to ensure credit note belongs to the correct broker
        var firstResult = await _db.AuctionResults.FindAsync(invoiceLines.First().AuctionResultId);
        var brokerId = firstResult?.BrokerId ?? originalInvoice.BrokerId;

        var auctionFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "AuctionFee");
        var handlingFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "HandlingFee");
        var auctionFeePercent = auctionFeeParam != null ? decimal.Parse(auctionFeeParam.Value, CultureInfo.InvariantCulture) : 0m;
        var handlingFeePerSkin = handlingFeeParam != null ? decimal.Parse(handlingFeeParam.Value, CultureInfo.InvariantCulture) : 0m;

        var creditNoteCount = await _db.Invoices.CountAsync(i => i.IsCreditNote);
        var creditNote = new Invoice
        {
            InvoiceNumber = $"CN-{DateTime.UtcNow:yyyyMMdd}-{creditNoteCount + 1:D5}",
            InvoiceDate = DateTime.UtcNow,
            BrokerId = brokerId,
            BuyerId = originalInvoice.BuyerId,
            IsCreditNote = true,
            OriginalInvoiceId = originalInvoice.Id,
            Status = InvoiceStatus.Issued
        };

        decimal subTotal = 0;
        decimal totalAuctionFee = 0;
        decimal totalCommission = 0;

        foreach (var line in invoiceLines)
        {
            var handlingFee = line.Skins * handlingFeePerSkin;
            var lotAuctionFee = (line.HammerPrice + handlingFee) * auctionFeePercent / 100m;

            creditNote.Lines.Add(new InvoiceLine
            {
                LotNumber = line.LotNumber,
                Description = line.Description,
                Skins = -line.Skins,
                PricePerSkin = line.PricePerSkin,
                HammerPrice = -line.HammerPrice,
                AuctionResultId = line.AuctionResultId
            });

            subTotal -= line.HammerPrice;
            totalAuctionFee -= lotAuctionFee;

            var result = await _db.AuctionResults.FindAsync(line.AuctionResultId);
            totalCommission -= result?.CommissionAmount ?? 0;
        }

        creditNote.SubTotal = subTotal;
        creditNote.AuctionFee = totalAuctionFee;
        creditNote.Commission = totalCommission;
        creditNote.TotalAmount = subTotal + totalAuctionFee + totalCommission;

        creditNote.Buyer = originalInvoice.Buyer;
        creditNote.OriginalInvoice = originalInvoice;

        var pdfBytes = InvoicePdfService.GeneratePdf(creditNote);
        creditNote.PdfData = pdfBytes;

        _db.Invoices.Add(creditNote);
        await _db.SaveChangesAsync();

        if (_blobStorage != null)
        {
            try
            {
                var fileName = $"{creditNote.InvoiceNumber}.pdf";
                creditNote.PdfUrl = await _blobStorage.UploadPdfAsync(fileName, pdfBytes);
                await _db.SaveChangesAsync();
            }
            catch (Exception blobEx)
            {
                _logger.LogWarning(blobEx, "Failed to upload credit note PDF to blob storage");
            }
        }

        return creditNote.Id;
    }
}

public class SubmitAuctionResultRequest
{
    public int LotNumber { get; set; }
    public int BrokerId { get; set; }
    public decimal PriceEur { get; set; }
}

public class SellToBuyerRequest
{
    public List<int> AuctionResultIds { get; set; } = new();
    public int BuyerId { get; set; }
    public string? CommissionType { get; set; }
    public decimal? CommissionValue { get; set; }
}

public class TakebackRequestBody
{
    public List<int> AuctionResultIds { get; set; } = new();
}

public class TakebackResponseBody
{
    public bool Approve { get; set; }
}
