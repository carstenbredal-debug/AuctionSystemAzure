using System.Net.Http.Json;
using AuctionSystem.Web.Models;

namespace AuctionSystem.Web.Services;

public class AuctionApiClient
{
    private readonly HttpClient _http;

    public AuctionApiClient(HttpClient http) => _http = http;

    // Dashboard
    public async Task<DashboardStats> GetDashboardAsync()
        => await _http.GetFromJsonAsync<DashboardStats>("api/dashboard") ?? new();

    // Auctions
    public async Task<List<AuctionDto>> GetAuctionsAsync()
        => await _http.GetFromJsonAsync<List<AuctionDto>>("api/auctions") ?? new();

    public async Task<AuctionDto?> GetAuctionAsync(int id)
        => await _http.GetFromJsonAsync<AuctionDto>($"api/auctions/{id}");

    public async Task<List<LotDto>> GetLotsByAuctionAsync(int auctionId)
        => await _http.GetFromJsonAsync<List<LotDto>>($"api/auctions/{auctionId}/lots") ?? new();

    public async Task UpdateAuctionStatusAsync(int id, AuctionStatus status)
        => await _http.PutAsJsonAsync($"api/auctions/{id}/status", status);

    public async Task RecordHammerPriceAsync(int lotId, decimal hammerPrice, int winningBrokerId)
        => await _http.PostAsJsonAsync($"api/lots/{lotId}/hammer", new { hammerPrice, winningBrokerId });

    // Brokers
    public async Task<List<BrokerDto>> GetBrokersAsync()
        => await _http.GetFromJsonAsync<List<BrokerDto>>("api/brokers") ?? new();

    public async Task<BrokerDto?> CreateBrokerAsync(BrokerDto broker)
    {
        var resp = await _http.PostAsJsonAsync("api/brokers", broker);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BrokerDto>();
    }

    public async Task<BrokerDto?> UpdateBrokerAsync(int id, BrokerDto broker)
    {
        var resp = await _http.PutAsJsonAsync($"api/brokers/{id}", broker);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BrokerDto>();
    }

    public async Task DeleteBrokerAsync(int id)
        => await _http.DeleteAsync($"api/brokers/{id}");

    // Sellers
    public async Task<List<SellerDto>> GetSellersAsync()
        => await _http.GetFromJsonAsync<List<SellerDto>>("api/sellers") ?? new();

    public async Task<SellerDto?> CreateSellerAsync(SellerDto seller)
    {
        var resp = await _http.PostAsJsonAsync("api/sellers", seller);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SellerDto>();
    }

    public async Task<SellerDto?> UpdateSellerAsync(int id, SellerDto seller)
    {
        var resp = await _http.PutAsJsonAsync($"api/sellers/{id}", seller);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SellerDto>();
    }

    public async Task DeleteSellerAsync(int id)
        => await _http.DeleteAsync($"api/sellers/{id}");

    public async Task<List<LotDto>> GetLotsBySellerAsync(int sellerId)
        => await _http.GetFromJsonAsync<List<LotDto>>($"api/sellers/{sellerId}/lots") ?? new();

    // Buyers
    public async Task<List<BuyerDto>> GetAllBuyersAsync()
        => await _http.GetFromJsonAsync<List<BuyerDto>>("api/buyers") ?? new();

    public async Task<BuyerDto?> GetBuyerAsync(int id)
        => await _http.GetFromJsonAsync<BuyerDto>($"api/buyers/{id}");

    public async Task<List<BuyerDto>> GetBuyersByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<BuyerDto>>($"api/brokers/{brokerId}/buyers") ?? new();

    public async Task<BuyerDto?> CreateBuyerAsync(int brokerId, BuyerDto buyer)
    {
        var resp = await _http.PostAsJsonAsync($"api/brokers/{brokerId}/buyers/add", buyer);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BuyerDto>();
    }

    public async Task<BuyerDto?> UpdateBuyerAsync(int id, BuyerDto buyer)
    {
        var resp = await _http.PutAsJsonAsync($"api/buyers/{id}", buyer);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BuyerDto>();
    }

    public async Task DeleteBuyerAsync(int id)
        => await _http.DeleteAsync($"api/buyers/{id}");

    // Bids
    public async Task<BidDto?> PlaceBidAsync(int lotId, int brokerId, decimal amount)
    {
        var resp = await _http.PostAsJsonAsync("api/bids", new { lotId, brokerId, amount });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BidDto>();
    }

    public async Task<List<BidDto>> GetBidsByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<BidDto>>($"api/bids/broker/{brokerId}") ?? new();

    public async Task AllocateLotAsync(int lotId, int brokerId, int buyerId, int quantity, decimal pricePerUnit)
        => await _http.PostAsJsonAsync("api/bids/allocate", new { lotId, brokerId, buyerId, quantity, pricePerUnit });

    public async Task<List<LotAllocationDto>> GetAllocationsByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<LotAllocationDto>>($"api/bids/allocations/broker/{brokerId}") ?? new();

    public async Task<List<LotAllocationDto>> GetAllocationsByBuyerAsync(int buyerId)
        => await _http.GetFromJsonAsync<List<LotAllocationDto>>($"api/bids/allocations/buyer/{buyerId}") ?? new();

    // Settlements & Invoices
    public async Task<InvoiceDto?> GenerateInvoiceAsync(int auctionId, int brokerId)
    {
        var resp = await _http.PostAsJsonAsync("api/settlements/invoices/generate", new { auctionId, brokerId });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<InvoiceDto>();
    }

    public async Task<List<InvoiceDto>> GetInvoicesByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<InvoiceDto>>($"api/settlements/invoices/broker/{brokerId}") ?? new();

    public async Task MarkInvoicePaidAsync(int invoiceId)
        => await _http.PutAsync($"api/settlements/invoices/{invoiceId}/paid", null);

    public async Task<SettlementDto?> CreateSettlementAsync(int lotId)
    {
        var resp = await _http.PostAsJsonAsync("api/settlements/create", lotId);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SettlementDto>();
    }

    public async Task<List<SettlementDto>> GetSettlementsBySellerAsync(int sellerId)
        => await _http.GetFromJsonAsync<List<SettlementDto>>($"api/settlements/seller/{sellerId}") ?? new();

    public async Task MarkSettlementCompleteAsync(int settlementId)
        => await _http.PutAsync($"api/settlements/{settlementId}/complete", null);

    public async Task<List<InvoiceDto>> GetAllInvoicesAsync()
        => await _http.GetFromJsonAsync<List<InvoiceDto>>("api/settlements/invoices") ?? new();

    public async Task<List<SettlementDto>> GetAllSettlementsAsync()
        => await _http.GetFromJsonAsync<List<SettlementDto>>("api/settlements") ?? new();

    // Customer Requests
    public async Task<List<BrokerCustomerRequestDto>> GetCustomerRequestsByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<BrokerCustomerRequestDto>>($"api/brokers/{brokerId}/customer-requests") ?? new();

    public async Task<List<BrokerCustomerRequestDto>> GetCustomerRequestsByBuyerAsync(int buyerId)
        => await _http.GetFromJsonAsync<List<BrokerCustomerRequestDto>>($"api/buyers/{buyerId}/customer-requests") ?? new();

    public async Task<BrokerCustomerRequestDto?> CreateCustomerRequestAsync(int brokerId, int buyerId)
    {
        var resp = await _http.PostAsJsonAsync($"api/brokers/{brokerId}/customer-requests", new { buyerId });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BrokerCustomerRequestDto>();
    }

    public async Task<BrokerCustomerRequestDto?> RespondToCustomerRequestAsync(int requestId, bool approve)
    {
        var resp = await _http.PutAsJsonAsync($"api/customer-requests/{requestId}/respond", new { approve });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BrokerCustomerRequestDto>();
    }

    public async Task DeleteCustomerRequestAsync(int requestId)
        => await _http.DeleteAsync($"api/customer-requests/{requestId}");

    public async Task<BrokerCustomerRequestDto?> CreateCustomerRequestByBuyerAsync(int buyerId, int brokerId)
    {
        var resp = await _http.PostAsJsonAsync($"api/buyers/{buyerId}/customer-requests", new { brokerId });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BrokerCustomerRequestDto>();
    }

    public async Task<BrokerCustomerRequestDto?> AdminCreateCustomerLinkAsync(int brokerId, int buyerId)
    {
        var resp = await _http.PostAsJsonAsync("api/management/customer-links", new { brokerId, buyerId });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BrokerCustomerRequestDto>();
    }

    public async Task<List<BrokerCustomerRequestDto>> GetAllCustomerRequestsAsync()
        => await _http.GetFromJsonAsync<List<BrokerCustomerRequestDto>>("api/management/customer-links") ?? new();

    // Seed
    public async Task SeedDatabaseAsync()
        => await _http.PostAsync("api/seed", null);

    // Auth / Users
    public async Task<AppUserDto?> GetCurrentUserAsync(string azureAdObjectId)
    {
        try
        {
            return await _http.GetFromJsonAsync<AppUserDto>($"api/users/me/{azureAdObjectId}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<AppUserDto>> GetAllUsersAsync()
        => await _http.GetFromJsonAsync<List<AppUserDto>>("api/users") ?? new();

    public async Task<AppUserDto?> CreateUserAsync(AppUserDto user)
    {
        var resp = await _http.PostAsJsonAsync("api/users", user);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AppUserDto>();
    }

    public async Task<AppUserDto?> UpdateUserAsync(int id, AppUserDto user)
    {
        var resp = await _http.PutAsJsonAsync($"api/users/{id}", user);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AppUserDto>();
    }

    public async Task DeleteUserAsync(int id)
        => await _http.DeleteAsync($"api/users/{id}");

    // Parameters
    public async Task<List<SystemParameterDto>> GetParametersAsync()
        => await _http.GetFromJsonAsync<List<SystemParameterDto>>("api/parameters") ?? new();

    public async Task<SystemParameterDto?> CreateParameterAsync(SystemParameterDto param)
    {
        var resp = await _http.PostAsJsonAsync("api/parameters", param);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SystemParameterDto>();
    }

    public async Task<SystemParameterDto?> UpdateParameterAsync(int id, SystemParameterDto param)
    {
        var resp = await _http.PutAsJsonAsync($"api/parameters/{id}", param);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SystemParameterDto>();
    }

    public async Task DeleteParameterAsync(int id)
        => await _http.DeleteAsync($"api/parameters/{id}");

    // Catalog Lots
    public async Task<List<CatalogLotDto>> GetCatalogLotsAsync()
        => await _http.GetFromJsonAsync<List<CatalogLotDto>>("api/catalog-lots") ?? new();

    public async Task<HttpResponseMessage> ImportCatalogLotsToAuctionAsync(int auctionId, ImportCatalogLotsRequest request)
        => await _http.PostAsJsonAsync($"api/auctions/{auctionId}/import-catalog-lots", request);

    // Auction Results
    public async Task<List<AuctionResultDto>> GetAuctionResultsAsync()
        => await _http.GetFromJsonAsync<List<AuctionResultDto>>("api/auction-results") ?? new();

    public async Task<List<AuctionResultDto>> GetAuctionResultsByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<AuctionResultDto>>($"api/auction-results/broker/{brokerId}") ?? new();

    public async Task<HttpResponseMessage> SubmitAuctionResultAsync(int lotNumber, int brokerId, decimal priceEur)
        => await _http.PostAsJsonAsync("api/auction-results", new { lotNumber, brokerId, priceEur });

    public async Task<HttpResponseMessage> SellLotsToBuyerAsync(List<int> auctionResultIds, int buyerId)
        => await _http.PostAsJsonAsync("api/auction-results/sell-to-buyer", new { auctionResultIds, buyerId });

    public async Task<List<AuctionResultDto>> GetAuctionResultsByBuyerAsync(int buyerId)
        => await _http.GetFromJsonAsync<List<AuctionResultDto>>($"api/auction-results/buyer/{buyerId}") ?? new();

    // Takeback Requests
    public async Task<HttpResponseMessage> RequestTakebackAsync(List<int> auctionResultIds)
        => await _http.PostAsJsonAsync("api/takeback-requests", new { auctionResultIds });

    public async Task<List<TakebackRequestDto>> GetTakebackRequestsByBuyerAsync(int buyerId)
        => await _http.GetFromJsonAsync<List<TakebackRequestDto>>($"api/takeback-requests/buyer/{buyerId}") ?? new();

    public async Task<HttpResponseMessage> RespondTakebackAsync(int requestId, bool approve)
        => await _http.PutAsJsonAsync($"api/takeback-requests/{requestId}", new { approve });
}
