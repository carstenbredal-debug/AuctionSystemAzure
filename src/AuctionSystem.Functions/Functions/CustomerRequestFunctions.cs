using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using AuctionSystem.Functions.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Functions;

public class CustomerRequestFunctions
{
    private readonly AuctionDbContext _db;
    private readonly ILogger<CustomerRequestFunctions> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CustomerRequestFunctions(AuctionDbContext db, ILogger<CustomerRequestFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    [Function("GetCustomerRequestsByBroker")]
    public async Task<HttpResponseData> GetByBroker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "brokers/{brokerId:int}/customer-requests")] HttpRequestData req, int brokerId)
    {
        if (!req.FunctionContext.CanAccessBroker(brokerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var requests = await _db.BrokerCustomerRequests
            .Include(r => r.Buyer)
            .Where(r => r.BrokerId == brokerId)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync();
        return await CreateJsonResponse(req, requests);
    }

    [Function("GetCustomerRequestsByBuyer")]
    public async Task<HttpResponseData> GetByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "buyers/{buyerId:int}/customer-requests")] HttpRequestData req, int buyerId)
    {
        if (!req.FunctionContext.CanAccessBuyer(buyerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var requests = await _db.BrokerCustomerRequests
            .Include(r => r.Broker)
            .Where(r => r.BuyerId == buyerId)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync();
        return await CreateJsonResponse(req, requests);
    }

    [Function("CreateCustomerRequest")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "brokers/{brokerId:int}/customer-requests")] HttpRequestData req, int brokerId)
    {
        if (!req.FunctionContext.CanAccessBroker(brokerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var dto = await req.ReadFromJsonAsync<CustomerRequestDto>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var existing = await _db.BrokerCustomerRequests
            .FirstOrDefaultAsync(r => r.BrokerId == brokerId && r.BuyerId == dto.BuyerId);
        if (existing != null)
        {
            if (existing.Status == CustomerRequestStatus.Declined)
            {
                existing.Status = CustomerRequestStatus.Pending;
                existing.InitiatedBy = "Broker";
                existing.RequestedAt = DateTime.UtcNow;
                existing.RespondedAt = null;
                await _db.SaveChangesAsync();
                var refreshed = await _db.BrokerCustomerRequests.Include(r => r.Buyer).FirstAsync(r => r.Id == existing.Id);
                return await CreateJsonResponse(req, refreshed, System.Net.HttpStatusCode.Created);
            }
            var errorResp = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
            await errorResp.WriteStringAsync("A request for this customer already exists.");
            return errorResp;
        }

        var request = new BrokerCustomerRequest
        {
            BrokerId = brokerId,
            BuyerId = dto.BuyerId,
            InitiatedBy = "Broker",
            Status = CustomerRequestStatus.Pending
        };

        _db.BrokerCustomerRequests.Add(request);
        await _db.SaveChangesAsync();

        var saved = await _db.BrokerCustomerRequests
            .Include(r => r.Buyer)
            .FirstAsync(r => r.Id == request.Id);

        return await CreateJsonResponse(req, saved, System.Net.HttpStatusCode.Created);
    }

    [Function("RespondToCustomerRequest")]
    public async Task<HttpResponseData> Respond(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "customer-requests/{id:int}/respond")] HttpRequestData req, int id)
    {
        var dto = await req.ReadFromJsonAsync<CustomerRequestResponseDto>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var request = await _db.BrokerCustomerRequests
            .Include(r => r.Buyer)
            .Include(r => r.Broker)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (request == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        request.Status = dto.Approve ? CustomerRequestStatus.Approved : CustomerRequestStatus.Declined;
        request.RespondedAt = DateTime.UtcNow;

        if (dto.Approve)
        {
            var buyer = await _db.Buyers.FindAsync(request.BuyerId);
            if (buyer != null)
            {
                buyer.BrokerId = request.BrokerId;
                _logger.LogInformation("Buyer {BuyerId} approved and linked to Broker {BrokerId}", buyer.Id, request.BrokerId);
            }

            // Also add to junction table
            var existingLink = await _db.BrokerBuyers
                .FirstOrDefaultAsync(bb => bb.BrokerId == request.BrokerId && bb.BuyerId == request.BuyerId);
            if (existingLink == null)
            {
                _db.BrokerBuyers.Add(new BrokerBuyer
                {
                    BrokerId = request.BrokerId,
                    BuyerId = request.BuyerId
                });
            }
        }

        await _db.SaveChangesAsync();
        return await CreateJsonResponse(req, request);
    }

    [Function("CreateCustomerRequestByBuyer")]
    public async Task<HttpResponseData> CreateByBuyer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "buyers/{buyerId:int}/customer-requests")] HttpRequestData req, int buyerId)
    {
        if (!req.FunctionContext.CanAccessBuyer(buyerId))
            return req.CreateResponse(System.Net.HttpStatusCode.Forbidden);

        var dto = await req.ReadFromJsonAsync<BuyerRequestDto>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var existing = await _db.BrokerCustomerRequests
            .FirstOrDefaultAsync(r => r.BrokerId == dto.BrokerId && r.BuyerId == buyerId);
        if (existing != null)
        {
            if (existing.Status == CustomerRequestStatus.Declined)
            {
                existing.Status = CustomerRequestStatus.Pending;
                existing.InitiatedBy = "Buyer";
                existing.RequestedAt = DateTime.UtcNow;
                existing.RespondedAt = null;
                await _db.SaveChangesAsync();
                var refreshed = await _db.BrokerCustomerRequests.Include(r => r.Broker).FirstAsync(r => r.Id == existing.Id);
                return await CreateJsonResponse(req, refreshed, System.Net.HttpStatusCode.Created);
            }
            var errorResp = req.CreateResponse(System.Net.HttpStatusCode.Conflict);
            await errorResp.WriteStringAsync("A request for this broker already exists.");
            return errorResp;
        }

        var request = new BrokerCustomerRequest
        {
            BrokerId = dto.BrokerId,
            BuyerId = buyerId,
            InitiatedBy = "Buyer",
            Status = CustomerRequestStatus.Pending
        };

        _db.BrokerCustomerRequests.Add(request);
        await _db.SaveChangesAsync();

        var saved = await _db.BrokerCustomerRequests
            .Include(r => r.Broker)
            .FirstAsync(r => r.Id == request.Id);

        return await CreateJsonResponse(req, saved, System.Net.HttpStatusCode.Created);
    }

    [Function("AdminCreateCustomerLink")]
    public async Task<HttpResponseData> AdminLink(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "management/customer-links")] HttpRequestData req)
    {
        var dto = await req.ReadFromJsonAsync<AdminLinkDto>();
        if (dto == null) return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

        var existing = await _db.BrokerCustomerRequests
            .FirstOrDefaultAsync(r => r.BrokerId == dto.BrokerId && r.BuyerId == dto.BuyerId);
        if (existing != null)
        {
            existing.Status = CustomerRequestStatus.Approved;
            existing.RespondedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new BrokerCustomerRequest
            {
                BrokerId = dto.BrokerId,
                BuyerId = dto.BuyerId,
                Status = CustomerRequestStatus.Approved,
                RespondedAt = DateTime.UtcNow
            };
            _db.BrokerCustomerRequests.Add(existing);
        }

        var buyer = await _db.Buyers.FindAsync(dto.BuyerId);
        if (buyer != null)
            buyer.BrokerId = dto.BrokerId;

        // Also add to junction table
        var existingLink = await _db.BrokerBuyers
            .FirstOrDefaultAsync(bb => bb.BrokerId == dto.BrokerId && bb.BuyerId == dto.BuyerId);
        if (existingLink == null)
        {
            _db.BrokerBuyers.Add(new BrokerBuyer
            {
                BrokerId = dto.BrokerId,
                BuyerId = dto.BuyerId
            });
        }

        await _db.SaveChangesAsync();

        var saved = await _db.BrokerCustomerRequests
            .Include(r => r.Broker)
            .Include(r => r.Buyer)
            .FirstAsync(r => r.Id == existing.Id);

        return await CreateJsonResponse(req, saved, System.Net.HttpStatusCode.Created);
    }

    [Function("GetAllCustomerRequests")]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "management/customer-links")] HttpRequestData req)
    {
        // Get all actual broker-buyer links from the junction table
        var brokerBuyers = await _db.BrokerBuyers
            .Include(bb => bb.Broker)
            .Include(bb => bb.Buyer)
            .ToListAsync();

        // Get request records for status info
        var requests = await _db.BrokerCustomerRequests.ToListAsync();
        var requestLookup = requests.ToDictionary(r => (r.BrokerId, r.BuyerId));

        // Build combined list — every BrokerBuyer link is a relationship
        var results = brokerBuyers.Select(bb =>
        {
            requestLookup.TryGetValue((bb.BrokerId, bb.BuyerId), out var reqRecord);
            return new
            {
                id = reqRecord?.Id ?? 0,
                brokerId = bb.BrokerId,
                broker = new { bb.Broker.Id, bb.Broker.BrokerNumber, bb.Broker.CompanyName },
                buyerId = bb.BuyerId,
                buyer = new { bb.Buyer.Id, bb.Buyer.BuyerNumber, bb.Buyer.Name },
                status = reqRecord?.Status ?? Domain.Enums.CustomerRequestStatus.Approved,
                initiatedBy = reqRecord?.InitiatedBy ?? "Admin",
                requestedAt = reqRecord?.RequestedAt ?? bb.CreatedAt,
                respondedAt = reqRecord?.RespondedAt
            };
        }).OrderBy(r => r.broker.BrokerNumber).ThenBy(r => r.buyer.BuyerNumber).ToList();

        return await CreateJsonResponse(req, results);
    }

    [Function("DeleteCustomerRequest")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "customer-requests/{id:int}")] HttpRequestData req, int id)
    {
        var request = await _db.BrokerCustomerRequests.FindAsync(id);
        if (request == null) return req.CreateResponse(System.Net.HttpStatusCode.NotFound);

        // Also remove from junction table
        var link = await _db.BrokerBuyers
            .FirstOrDefaultAsync(bb => bb.BrokerId == request.BrokerId && bb.BuyerId == request.BuyerId);
        if (link != null)
            _db.BrokerBuyers.Remove(link);

        _db.BrokerCustomerRequests.Remove(request);
        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
    }

    [Function("UnlinkBrokerBuyer")]
    public async Task<HttpResponseData> Unlink(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "brokers/{brokerId:int}/buyers/{buyerId:int}")] HttpRequestData req, int brokerId, int buyerId)
    {
        var link = await _db.BrokerBuyers
            .FirstOrDefaultAsync(bb => bb.BrokerId == brokerId && bb.BuyerId == buyerId);
        if (link != null)
            _db.BrokerBuyers.Remove(link);

        var request = await _db.BrokerCustomerRequests
            .FirstOrDefaultAsync(r => r.BrokerId == brokerId && r.BuyerId == buyerId);
        if (request != null)
            _db.BrokerCustomerRequests.Remove(request);

        await _db.SaveChangesAsync();
        return req.CreateResponse(System.Net.HttpStatusCode.NoContent);
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

public class CustomerRequestDto
{
    public int BuyerId { get; set; }
}

public class BuyerRequestDto
{
    public int BrokerId { get; set; }
}

public class AdminLinkDto
{
    public int BrokerId { get; set; }
    public int BuyerId { get; set; }
}

public class CustomerRequestResponseDto
{
    public bool Approve { get; set; }
}
