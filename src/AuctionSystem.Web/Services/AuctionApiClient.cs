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

    // Sellers
    public async Task<List<SellerDto>> GetSellersAsync()
        => await _http.GetFromJsonAsync<List<SellerDto>>("api/sellers") ?? new();

    public async Task<List<LotDto>> GetLotsBySellerAsync(int sellerId)
        => await _http.GetFromJsonAsync<List<LotDto>>($"api/sellers/{sellerId}/lots") ?? new();

    // Buyers
    public async Task<List<BuyerDto>> GetBuyersByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<BuyerDto>>($"api/brokers/{brokerId}/buyers") ?? new();

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

    // Seed
    public async Task SeedDatabaseAsync()
        => await _http.PostAsync("api/seed", null);
}
