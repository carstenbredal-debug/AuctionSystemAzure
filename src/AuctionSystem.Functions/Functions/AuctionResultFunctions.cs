using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.BusinessCentral.Services;
using AuctionSystem.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class AuctionResultFunctions
{
    private readonly AuctionDbContext _db;
    private readonly CatalogDbContext _catalogDb;
    private readonly ILogger<AuctionResultFunctions> _logger;
    private readonly BlobStorageService? _blobStorage;
    private readonly BusinessCentralSyncService? _bcSyncService;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AuctionResultFunctions(AuctionDbContext db, CatalogDbContext catalogDb, ILogger<AuctionResultFunctions> logger, BlobStorageService? blobStorage = null, BusinessCentralSyncService? bcSyncService = null)
    {
        _db = db;
        _catalogDb = catalogDb;
        _logger = logger;
        _blobStorage = blobStorage;
        _bcSyncService = bcSyncService;
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

        // Look up lot in auction.Lots and snapshot/CatalogLots
        var auctionLot = await _db.Lots
            .Include(l => l.Auction)
            .FirstOrDefaultAsync(l => l.LotNumber == body.LotNumber);

        // Try snapshot table first, fall back to live CatalogLots
        CatalogLot? catalogLot = null;
        if (auctionLot?.Auction?.AuctionNumber != null)
        {
            var snapTable = $"auction.[{auctionLot.Auction.AuctionNumber}.Lots]";
            try
            {
                catalogLot = await _catalogDb.Database
                    .SqlQueryRaw<CatalogLot>($"SELECT * FROM {snapTable} WHERE LotNumber = {body.LotNumber}")
                    .FirstOrDefaultAsync();
            }
            catch { }
        }
        catalogLot ??= await _catalogDb.CatalogLots
            .FirstOrDefaultAsync(cl => cl.LotNumber == body.LotNumber);

        var result = new AuctionResult
        {
            AuctionId = auctionLot?.AuctionId ?? 0,
            LotNumber = body.LotNumber,
            BrokerId = body.BrokerId,
            PriceEur = body.PriceEur,
            SalesType = catalogLot?.SalesType ?? auctionLot?.Description?.Split(' ').FirstOrDefault(),
            Gender = catalogLot?.Gender ?? (auctionLot != null ? ParseField(auctionLot.Description, 1) : null),
            Group = catalogLot?.Group ?? auctionLot?.Category,
            Color = catalogLot?.Color ?? (auctionLot != null ? ParseField(auctionLot.Description, 2) : null),
            Quality = catalogLot?.Quality ?? (auctionLot != null ? ParseField(auctionLot.Description, 3) : null),
            Size = catalogLot?.Size,
            HairLength = catalogLot?.HairLength,
            Clarity = catalogLot?.Clarity,
            TotalSkins = catalogLot?.TotalSkins ?? auctionLot?.Quantity ?? 0,
            BoxCount = catalogLot?.BoxCount ?? 0,
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
        r.CommissionAmount,
        r.LastModifiedBy,
        r.LastModifiedAt
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
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
                r.LastModifiedBy, r.LastModifiedAt
            })
            .ToListAsync();

        var allLotNumbers = results.Select(r => r.LotNumber).Distinct().ToList();
        var allLotShipmentStatus = await GetLotShipmentStatusAsync(allLotNumbers);

        var enrichedAll = results.Select(r => new
        {
            r.Id, r.LotNumber, r.BrokerId,
            r.brokerName, r.brokerNumber,
            r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
            r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
            r.Processed, r.ReceivedAt, r.ProcessedAt,
            r.SoldToBuyerId, r.soldToBuyerName, r.soldToBuyerNumber,
            r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
            r.LastModifiedBy, r.LastModifiedAt,
            ShippingStatus = allLotShipmentStatus.GetValueOrDefault(r.LotNumber)
        });

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(enrichedAll, JsonOptions));
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
                r.Id, r.AuctionId, r.LotNumber, r.BrokerId,
                brokerName = r.Broker.CompanyName,
                brokerNumber = r.Broker.BrokerNumber,
                r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
                r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
                r.Processed, r.ReceivedAt, r.ProcessedAt,
                r.SoldToBuyerId,
                soldToBuyerName = r.SoldToBuyer != null ? r.SoldToBuyer.Name : null,
                soldToBuyerNumber = r.SoldToBuyer != null ? r.SoldToBuyer.BuyerNumber : null,
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
                r.LastModifiedBy, r.LastModifiedAt
            })
            .ToListAsync();

        // Get shipping status for lots
        var lotNumbers = results.Select(r => r.LotNumber).Distinct().ToList();
        var lotShipmentStatus = await GetLotShipmentStatusAsync(lotNumbers);

        // A lot only counts as truly "Invoiced" once its invoice actually posted to BC (has a BC
        // number). Sold lots whose invoice hasn't posted are surfaced as "Not Posted", so a failed
        // BC push never looks like a completed invoice.
        var resultIds = results.Select(r => r.Id).ToList();
        var postedResultIds = (await _db.InvoiceLines
            .Where(l => resultIds.Contains(l.AuctionResultId)
                        && !l.Invoice.IsCreditNote
                        && l.Invoice.BcInvoiceNumber != null && l.Invoice.BcInvoiceNumber != "")
            .Select(l => l.AuctionResultId)
            .Distinct()
            .ToListAsync()).ToHashSet();

        var enriched = results.Select(r => new
        {
            r.Id, r.AuctionId, r.LotNumber, r.BrokerId,
            r.brokerName, r.brokerNumber,
            r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
            r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
            r.Processed, r.ReceivedAt, r.ProcessedAt,
            r.SoldToBuyerId, r.soldToBuyerName, r.soldToBuyerNumber,
            r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
            r.LastModifiedBy, r.LastModifiedAt,
            ShippingStatus = lotShipmentStatus.GetValueOrDefault(r.LotNumber),
            InvoicePosted = postedResultIds.Contains(r.Id)
        });

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(enriched, JsonOptions));
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

        // Bulletproof: a lot may only be sold to a customer LINKED to that lot's broker
        // (the BrokerBuyers junction). Enforced server-side so a tampered/stale client can't
        // sell to a non-linked buyer even if the dropdown were bypassed.
        var brokerIds = results.Select(r => r.BrokerId).Distinct().ToList();
        if (brokerIds.Count > 0)
        {
            var linkedBrokerIds = await _db.BrokerBuyers
                .Where(bb => bb.BuyerId == body.BuyerId && brokerIds.Contains(bb.BrokerId))
                .Select(bb => bb.BrokerId)
                .Distinct()
                .ToListAsync();
            if (brokerIds.Except(linkedBrokerIds).Any())
            {
                var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                resp.Headers.Add("Content-Type", "application/json");
                await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Buyer is not a linked customer of the broker for one or more of these lots." }, JsonOptions));
                return resp;
            }
        }

        foreach (var result in results)
        {
            result.SoldToBuyerId = body.BuyerId;
            result.SoldAt = DateTime.UtcNow;
            result.CommissionType = body.CommissionType;
            result.CommissionValue = body.CommissionValue;
            result.LastModifiedBy = body.Initials;
            result.LastModifiedAt = DateTime.UtcNow;
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

        // Record sales history
        foreach (var result in results)
        {
            var hammerPrice = result.TotalSkins * result.PriceEur;
            _db.LotSalesHistories.Add(new LotSalesHistory
            {
                LotNumber = result.LotNumber,
                AuctionResultId = result.Id,
                ActionType = "Sold",
                Initials = body.Initials,
                BuyerId = body.BuyerId,
                BuyerName = buyer.Name,
                Amount = hammerPrice,
                CreatedAt = DateTime.UtcNow
            });
        }

        int? invoiceId = null;
        string? bcError = null;
        if (results.Count > 0 && _bcSyncService != null)
            (invoiceId, bcError) = await CreateAndPushInvoiceAsync(results, body.BuyerId, buyer);

        string? pdfUrl = null;
        if (invoiceId != null)
        {
            var inv = await _db.Invoices.FindAsync(invoiceId);
            pdfUrl = inv?.PdfUrl;
            // Update sales history with invoice info
            var lotNums = results.Select(r => r.LotNumber).ToList();
            var historyEntries = await _db.LotSalesHistories
                .Where(h => lotNums.Contains(h.LotNumber) && h.InvoiceId == null && h.ActionType == "Sold")
                .OrderByDescending(h => h.CreatedAt)
                .ToListAsync();
            foreach (var h in historyEntries)
            {
                h.InvoiceId = invoiceId;
                h.InvoiceNumber = inv?.InvoiceNumber ?? inv?.BcInvoiceNumber;
            }
            await _db.SaveChangesAsync();
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { soldCount = results.Count, invoiceId, pdfUrl, bcError }, JsonOptions));
        return response;
    }

    private async Task<(int? InvoiceId, string? BcError)> CreateAndPushInvoiceAsync(List<AuctionResult> results, int buyerId, Buyer buyer)
    {
        try
        {
            var brokerId = results.First().BrokerId;
            var auctionFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "AuctionFee");
            var handlingFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "HandlingFee");
            var auctionFeePercent = auctionFeeParam != null ? decimal.Parse(auctionFeeParam.Value, CultureInfo.InvariantCulture) : 0m;
            var handlingFeePerSkin = handlingFeeParam != null ? decimal.Parse(handlingFeeParam.Value, CultureInfo.InvariantCulture) : 0m;

            var invoice = new Invoice
            {
                InvoiceNumber = "",
                InvoiceDate = DateTime.UtcNow,
                BrokerId = brokerId,
                BuyerId = buyerId,
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
            invoice.Buyer = buyer;

            _db.Invoices.Add(invoice);
            await _db.SaveChangesAsync();

            var outcome = await _bcSyncService!.PushInvoiceToBcAsync(invoice);
            invoice.InvoiceNumber = invoice.BcInvoiceNumber ?? "";
            await _db.SaveChangesAsync();

            // The invoice exists locally regardless — return its id so sales history still links.
            // If BC didn't actually take it (e.g. buyer not in BC), surface the reason instead of
            // a silent success so it can be corrected and re-pushed.
            if (outcome.Outcome == BcPushOutcome.NotPushed)
            {
                _logger.LogWarning("Invoice {Id} not pushed to BC: {Reason}", invoice.Id, outcome.Reason);
                return (invoice.Id, outcome.Reason);
            }

            _logger.LogInformation("Invoice {Number} created and posted in BC", invoice.InvoiceNumber);
            return (invoice.Id, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create BC invoice for {Count} lots", results.Count);
            return (null, ex.Message);
        }
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
                r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
                r.LastModifiedBy, r.LastModifiedAt
            })
            .ToListAsync();

        var buyerLotNumbers = results.Select(r => r.LotNumber).Distinct().ToList();
        var buyerLotShipmentStatus = await GetLotShipmentStatusAsync(buyerLotNumbers);

        var enrichedBuyer = results.Select(r => new
        {
            r.Id, r.LotNumber, r.BrokerId,
            r.brokerName, r.brokerNumber,
            r.PriceEur, r.SalesType, r.Gender, r.Group, r.Color, r.Quality,
            r.Size, r.Clarity, r.HairLength, r.TotalSkins, r.BoxCount,
            r.Processed, r.ReceivedAt, r.ProcessedAt,
            r.SoldToBuyerId, r.soldToBuyerName, r.soldToBuyerNumber,
            r.SoldAt, r.CommissionType, r.CommissionValue, r.CommissionAmount,
            r.LastModifiedBy, r.LastModifiedAt,
            ShippingStatus = buyerLotShipmentStatus.GetValueOrDefault(r.LotNumber)
        });

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(enrichedBuyer, JsonOptions));
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

        var shippingError = await CheckShippedLotsAsync(results, req, "Cannot take back");
        if (shippingError != null) return shippingError;

        // Record initials on auction results
        foreach (var r in results)
        {
            r.LastModifiedBy = body.Initials;
            r.LastModifiedAt = DateTime.UtcNow;
        }

        var (created, takenBackResultIds, takebackBuyerId) = await ProcessBrokerTakebacksAsync(results);
        await _db.SaveChangesAsync();

        var (creditNoteId, creditNotePdfUrl) = await TryGenerateCreditNoteForTakebackAsync(takenBackResultIds, takebackBuyerId);

        // Record re-invoice history
        string? creditNoteNumber = null;
        if (creditNoteId != null)
        {
            var cn = await _db.Invoices.FindAsync(creditNoteId);
            creditNoteNumber = cn?.InvoiceNumber ?? cn?.BcInvoiceNumber;
        }
        foreach (var r in results)
        {
            _db.LotSalesHistories.Add(new LotSalesHistory
            {
                LotNumber = r.LotNumber,
                AuctionResultId = r.Id,
                ActionType = "Re-Invoice",
                Initials = body.Initials,
                BuyerId = takebackBuyerId,
                InvoiceId = creditNoteId,
                InvoiceNumber = creditNoteNumber,
                Amount = r.TotalSkins * r.PriceEur,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { requestCount = created.Count, creditNoteId, creditNotePdfUrl }, JsonOptions));
        return response;
    }

    [Function("GetLotSalesHistory")]
    public async Task<HttpResponseData> GetLotSalesHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auction-results/lot-history/{lotNumber:int}")] HttpRequestData req, int lotNumber)
    {
        var history = await _db.LotSalesHistories
            .Where(h => h.LotNumber == lotNumber)
            .OrderByDescending(h => h.CreatedAt)
            .Select(h => new
            {
                h.Id, h.LotNumber, h.ActionType, h.Initials,
                h.BuyerId, h.BuyerName,
                h.InvoiceId, h.InvoiceNumber,
                h.Amount, h.Notes, h.CreatedAt
            })
            .ToListAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(history, JsonOptions));
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

        var shippingError = await CheckShippedLotsAsync(results, req, "Cannot request return for");
        if (shippingError != null) return shippingError;

        var created = await ProcessBuyerTakebackRequestsAsync(results);
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { requestCount = created.Count }, JsonOptions));
        return response;
    }

    [Function("ReassignUnsoldLots")]
    public async Task<HttpResponseData> ReassignUnsoldLots(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/reassign-unsold")] HttpRequestData req)
    {
        var body = await req.ReadFromJsonAsync<ReassignUnsoldRequest>();
        if (body == null || body.AuctionResultIds == null || body.AuctionResultIds.Count == 0)
            return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var results = await _db.AuctionResults
            .Where(r => body.AuctionResultIds.Contains(r.Id) && r.SoldToBuyerId == null)
            .ToListAsync();

        if (results.Count == 0)
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "No unsold lots found for given IDs" }, JsonOptions));
            return resp;
        }

        int reassigned;
        if (body.Action == "internal")
            reassigned = await ReassignToInternalBrokerAsync(results);
        else if (body.Action == "auction")
            reassigned = await ReturnLotsToAuctionAsync(results);
        else
        {
            var resp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid action. Use 'internal' or 'auction'." }, JsonOptions));
            return resp;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { action = body.Action, reassigned }, JsonOptions));
        return response;
    }

    private async Task<int> ReassignToInternalBrokerAsync(List<AuctionResult> results)
    {
        var internalBroker = await _db.Brokers.FirstOrDefaultAsync(b => b.BrokerNumber == "999");
        if (internalBroker == null)
        {
            internalBroker = new Broker { BrokerNumber = "999", CompanyName = "Internal", IsActive = true };
            _db.Brokers.Add(internalBroker);
            await _db.SaveChangesAsync();
        }

        foreach (var result in results)
            result.BrokerId = internalBroker.Id;

        return results.Count;
    }

    private async Task<int> ReturnLotsToAuctionAsync(List<AuctionResult> results)
    {
        foreach (var result in results)
        {
            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == result.LotNumber);
            if (lot != null) lot.Status = LotStatus.Unsold;
            _db.AuctionResults.Remove(result);
        }
        return results.Count;
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

    private async Task<HttpResponseData?> CheckShippedLotsAsync(
        List<AuctionResult> results, HttpRequestData req, string errorPrefix)
    {
        var lotNumbers = results.Select(r => r.LotNumber).ToList();

        // Check invoices with shipping status
        var shippedViaInvoice = await _db.Invoices
            .Where(i => i.ShippingStatus == "Released" && !i.IsCreditNote)
            .SelectMany(i => i.Lines)
            .Where(l => lotNumbers.Contains(l.LotNumber))
            .Select(l => l.LotNumber)
            .Distinct()
            .ToListAsync();

        // Also check shipment lines directly
        var shippedViaShipment = await _db.ShipmentLines
            .Where(sl => lotNumbers.Contains(sl.LotNumber))
            .Select(sl => sl.LotNumber)
            .Distinct()
            .ToListAsync();

        var shippedLotNumbers = shippedViaInvoice.Union(shippedViaShipment).Distinct().ToList();

        if (shippedLotNumbers.Count == 0) return null;

        var errorResp = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        errorResp.Headers.Add("Content-Type", "application/json");
        await errorResp.WriteStringAsync(JsonSerializer.Serialize(new
        {
            error = "Lot Sold",
            message = $"{errorPrefix} lot(s) {string.Join(", ", shippedLotNumbers)} — already on a shipment order.",
            shippedLotNumbers
        }, JsonOptions));
        return errorResp;
    }

    private async Task<(List<TakebackRequest> Created, List<int> TakenBackResultIds, int? BuyerId)> ProcessBrokerTakebacksAsync(
        List<AuctionResult> results)
    {
        var created = new List<TakebackRequest>();
        var takenBackResultIds = new List<int>();
        int? takebackBuyerId = null;

        foreach (var result in results)
        {
            var existing = await _db.TakebackRequests
                .FirstOrDefaultAsync(t => t.AuctionResultId == result.Id && t.Status == CustomerRequestStatus.Pending);
            if (existing != null) continue;

            takebackBuyerId ??= result.SoldToBuyerId;

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

            var lot = await _db.Lots.FirstOrDefaultAsync(l => l.LotNumber == result.LotNumber);
            if (lot != null) lot.Status = LotStatus.Broker;
        }

        return (created, takenBackResultIds, takebackBuyerId);
    }

    private async Task<List<TakebackRequest>> ProcessBuyerTakebackRequestsAsync(List<AuctionResult> results)
    {
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
        return created;
    }

    private async Task<(int? CreditNoteId, string? CreditNotePdfUrl)> TryGenerateCreditNoteForTakebackAsync(
        List<int> takenBackResultIds, int? buyerId)
    {
        if (takenBackResultIds.Count == 0) return (null, null);

        try
        {
            var creditNoteId = await GenerateCreditNoteAsync(takenBackResultIds, buyerId);
            if (creditNoteId != null)
            {
                var cn = await _db.Invoices.FindAsync(creditNoteId.Value);
                return (creditNoteId, cn?.PdfUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate credit note");
        }
        return (null, null);
    }

    private async Task<int?> GenerateCreditNoteAsync(List<int> auctionResultIds, int? buyerId = null)
    {
        var query = _db.Set<InvoiceLine>()
            .Include(l => l.Invoice).ThenInclude(i => i.Buyer)
            .Include(l => l.Invoice).ThenInclude(i => i.Broker)
            .Where(l => auctionResultIds.Contains(l.AuctionResultId) && !l.Invoice.IsCreditNote);

        // Only credit lines from invoices belonging to the specific buyer
        if (buyerId.HasValue)
            query = query.Where(l => l.Invoice.BuyerId == buyerId.Value);

        var allInvoiceLines = await query.ToListAsync();

        // Only use the most recent invoice line per auction result to avoid double crediting
        var invoiceLines = allInvoiceLines
            .GroupBy(l => l.AuctionResultId)
            .Select(g => g.OrderByDescending(l => l.Invoice.InvoiceDate).First())
            .ToList();

        if (invoiceLines.Count == 0) return null;

        var originalInvoice = invoiceLines.First().Invoice;

        // Use auction result's broker to ensure credit note belongs to the correct broker
        var firstResult = await _db.AuctionResults.FindAsync(invoiceLines.First().AuctionResultId);
        var brokerId = firstResult?.BrokerId ?? originalInvoice.BrokerId;

        var auctionFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "AuctionFee");
        var handlingFeeParam = await _db.SystemParameters.FirstOrDefaultAsync(p => p.Key == "HandlingFee");
        var auctionFeePercent = auctionFeeParam != null ? decimal.Parse(auctionFeeParam.Value, CultureInfo.InvariantCulture) : 0m;
        var handlingFeePerSkin = handlingFeeParam != null ? decimal.Parse(handlingFeeParam.Value, CultureInfo.InvariantCulture) : 0m;

        var creditNote = new Invoice
        {
            InvoiceNumber = "",
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

        _db.Invoices.Add(creditNote);
        await _db.SaveChangesAsync();

        // Push to BC as Sales Credit Memo
        if (_bcSyncService != null)
        {
            try
            {
                await _bcSyncService.PushCreditNoteToBcAsync(creditNote);
            }
            catch (Exception bcEx)
            {
                _logger.LogError(bcEx, "Failed to push credit note to BC");
            }
        }

        return creditNote.Id;
    }

    [Function("DiagBlobStorage")]
    public async Task<HttpResponseData> DiagBlobStorage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diag/blob")] HttpRequestData req)
    {
        var result = new Dictionary<string, object?>();
        result["blobStorageAvailable"] = _blobStorage != null;
        result["bcSyncServiceAvailable"] = _bcSyncService != null;

        if (_blobStorage != null)
        {
            try
            {
                var testData = System.Text.Encoding.UTF8.GetBytes("blob-storage-test");
                var url = await _blobStorage.UploadPdfAsync("_diag_test.txt", testData);
                result["uploadSuccess"] = true;
                result["uploadUrl"] = url;
            }
            catch (Exception ex)
            {
                result["uploadSuccess"] = false;
                result["uploadError"] = ex.ToString();
            }
        }

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
        return response;
    }

    [Function("BackfillCatalogData")]
    public async Task<HttpResponseData> BackfillCatalogData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auction-results/backfill-catalog")] HttpRequestData req)
    {
        var results = await _db.AuctionResults.Where(r => r.Size == null).ToListAsync();
        var lotNumbers = results.Select(r => r.LotNumber).Distinct().ToList();
        var catalogLots = await _catalogDb.CatalogLots
            .Where(cl => lotNumbers.Contains(cl.LotNumber))
            .ToListAsync();
        var catalogMap = catalogLots.GroupBy(cl => cl.LotNumber).ToDictionary(g => g.Key, g => g.First());

        int updated = 0;
        foreach (var r in results)
        {
            if (catalogMap.TryGetValue(r.LotNumber, out var cl))
            {
                r.Size = cl.Size;
                r.HairLength = cl.HairLength;
                r.Clarity = cl.Clarity;
                if (r.BoxCount == 0) r.BoxCount = cl.BoxCount;
                if (r.TotalSkins == 0) r.TotalSkins = cl.TotalSkins;
                if (string.IsNullOrEmpty(r.SalesType)) r.SalesType = cl.SalesType;
                if (string.IsNullOrEmpty(r.Gender)) r.Gender = cl.Gender;
                if (string.IsNullOrEmpty(r.Group)) r.Group = cl.Group;
                if (string.IsNullOrEmpty(r.Color)) r.Color = cl.Color;
                if (string.IsNullOrEmpty(r.Quality)) r.Quality = cl.Quality;
                updated++;
            }
        }
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { total = results.Count, updated, notFound = results.Count - updated }, JsonOptions));
        return response;
    }

    [Function("UnsellInvoice")]
    public async Task<HttpResponseData> UnsellInvoice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "settlements/invoices/{invoiceId:int}/unsell")] HttpRequestData req, int invoiceId)
    {
        var invoice = await _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice == null)
            return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Get auction results linked to this invoice's lot numbers
        var lotNumbers = invoice.Lines.Select(l => l.LotNumber).ToList();
        var results = await _db.AuctionResults
            .Where(r => lotNumbers.Contains(r.LotNumber) && r.SoldToBuyerId == invoice.BuyerId)
            .ToListAsync();

        // Clear sold status on results
        foreach (var r in results)
        {
            r.SoldToBuyerId = null;
            r.SoldAt = null;
            r.CommissionType = null;
            r.CommissionValue = null;
            r.CommissionAmount = null;
        }

        // Delete invoice lines and invoice
        _db.InvoiceLines.RemoveRange(invoice.Lines);
        _db.Invoices.Remove(invoice);

        // Delete related sales history
        var history = await _db.LotSalesHistories
            .Where(h => h.InvoiceId == invoiceId)
            .ToListAsync();
        _db.LotSalesHistories.RemoveRange(history);

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { deleted = true, lotsUnsold = results.Count }, JsonOptions));
        return response;
    }

    private async Task<Dictionary<int, string>> GetLotShipmentStatusAsync(List<int> lotNumbers)
    {
        if (lotNumbers.Count == 0) return new();

        var lotShipmentMap = await _db.ShipmentLines
            .Where(sl => lotNumbers.Contains(sl.LotNumber))
            .Include(sl => sl.Shipment)
            .Select(sl => new { sl.LotNumber, sl.Shipment!.Status })
            .ToListAsync();

        return lotShipmentMap
            .GroupBy(x => x.LotNumber)
            .ToDictionary(g => g.Key, g => g.First().Status);
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
    public string? Initials { get; set; }
}

public class TakebackRequestBody
{
    public List<int> AuctionResultIds { get; set; } = new();
    public string? Initials { get; set; }
}

public class TakebackResponseBody
{
    public bool Approve { get; set; }
}

public class ReassignUnsoldRequest
{
    public List<int> AuctionResultIds { get; set; } = new();
    public string Action { get; set; } = "";  // "internal" or "auction"
}
