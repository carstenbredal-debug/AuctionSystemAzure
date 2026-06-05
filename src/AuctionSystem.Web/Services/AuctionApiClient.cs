using System.Net.Http.Json;
using AuctionSystem.Web.Models;

namespace AuctionSystem.Web.Services;

public class AuctionApiClient
{
    private readonly HttpClient _http;

    public AuctionApiClient(HttpClient http) => _http = http;

    public string BaseUrl => _http.BaseAddress?.ToString().TrimEnd('/') ?? "";

    // Dashboard
    public async Task<DashboardStats> GetDashboardAsync()
        => await _http.GetFromJsonAsync<DashboardStats>("api/dashboard") ?? new();

    // Auctions
    public async Task<List<AuctionDto>> GetAuctionsAsync()
        => await _http.GetFromJsonAsync<List<AuctionDto>>("api/auctions") ?? new();

    public async Task<AuctionDto?> GetAuctionAsync(int id)
        => await _http.GetFromJsonAsync<AuctionDto>($"api/auctions/{id}");

    public async Task<HttpResponseMessage> CreateAuctionAsync(object auction)
        => await _http.PostAsJsonAsync("api/auctions", auction);

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

    // Farmers
    public async Task<List<FarmerDto>> GetFarmersAsync()
        => await _http.GetFromJsonAsync<List<FarmerDto>>("api/farmers") ?? new();

    public async Task<FarmerDto?> CreateFarmerAsync(FarmerDto farmer)
    {
        var resp = await _http.PostAsJsonAsync("api/farmers", farmer);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<FarmerDto>();
    }

    public async Task<FarmerDto?> UpdateFarmerAsync(int id, FarmerDto farmer)
    {
        var resp = await _http.PutAsJsonAsync($"api/farmers/{id}", farmer);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<FarmerDto>();
    }

    public async Task DeleteFarmerAsync(int id)
        => await _http.DeleteAsync($"api/farmers/{id}");

    public async Task<List<LotDto>> GetLotsByFarmerAsync(int farmerId)
        => await _http.GetFromJsonAsync<List<LotDto>>($"api/farmers/{farmerId}/lots") ?? new();

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



    public async Task MarkInvoicePaidAsync(int invoiceId)
        => await _http.PutAsync($"api/settlements/invoices/{invoiceId}/paid", null);

    public async Task<HttpResponseMessage> UpdateInvoiceStatusAsync(int invoiceId, string status, bool releaseForShipping = false)
        => await _http.PutAsJsonAsync($"api/settlements/invoices/{invoiceId}/status", new { status, releaseForShipping });

    public async Task<BcBalanceCheckDto?> CheckBcPaymentBalanceAsync(int invoiceId)
    {
        try
        {
            return await _http.GetFromJsonAsync<BcBalanceCheckDto>($"api/settlements/invoices/{invoiceId}/bc-balance");
        }
        catch { return null; }
    }

    public async Task<BcInvoiceRemainingDto?> CheckBcInvoiceRemainingAsync(int invoiceId)
    {
        try
        {
            var resp = await _http.PostAsync($"api/settlements/invoices/{invoiceId}/check-bc-remaining", null);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<BcInvoiceRemainingDto>();
        }
        catch { return null; }
    }

    public async Task<PaymentHistoryDto?> GetInvoicePaymentHistoryAsync(int invoiceId)
    {
        try
        {
            var resp = await _http.GetAsync($"api/settlements/invoices/{invoiceId}/payment-history");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<PaymentHistoryDto>();
        }
        catch { return null; }
    }

    public async Task<HttpResponseMessage> ProcessDownpaymentAsync(int invoiceId, decimal amount, bool isPercentage, bool releaseForShipping)
        => await _http.PostAsJsonAsync($"api/settlements/invoices/{invoiceId}/downpayment", new { amount, isPercentage, releaseForShipping });

    public async Task<List<ShippingBoxDto>> GetShippingBoxesAsync()
    {
        var resp = await _http.GetAsync("api/shipping/boxes");
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<ShippingBoxDto>>() ?? new();
    }

    public async Task<SettlementDto?> CreateSettlementAsync(int lotId)
    {
        var resp = await _http.PostAsJsonAsync("api/settlements/create", lotId);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SettlementDto>();
    }

    public async Task<List<SettlementDto>> GetSettlementsByFarmerAsync(int farmerId)
        => await _http.GetFromJsonAsync<List<SettlementDto>>($"api/settlements/farmer/{farmerId}") ?? new();

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

    public async Task UnlinkBrokerBuyerAsync(int brokerId, int buyerId)
        => await _http.DeleteAsync($"api/brokers/{brokerId}/buyers/{buyerId}");

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

    public async Task<NextUnsoldLotDto?> GetNextUnsoldLotAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<NextUnsoldLotDto>("api/auction-results/next-unsold-lot");
        }
        catch { return null; }
    }

    public async Task<List<AuctionResultDto>> GetAuctionResultsByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<AuctionResultDto>>($"api/auction-results/broker/{brokerId}") ?? new();

    public async Task<HttpResponseMessage> SubmitAuctionResultAsync(int lotNumber, int brokerId, decimal priceEur)
        => await _http.PostAsJsonAsync("api/auction-results", new { lotNumber, brokerId, priceEur });

    public async Task<HttpResponseMessage> SellLotsToBuyerAsync(List<int> auctionResultIds, int buyerId, string? commissionType = null, decimal? commissionValue = null, string? initials = null)
        => await _http.PostAsJsonAsync("api/auction-results/sell-to-buyer", new { auctionResultIds, buyerId, commissionType, commissionValue, initials });

    public async Task<List<AuctionResultDto>> GetAuctionResultsByBuyerAsync(int buyerId)
        => await _http.GetFromJsonAsync<List<AuctionResultDto>>($"api/auction-results/buyer/{buyerId}") ?? new();

    // Takeback Requests
    public async Task<HttpResponseMessage> RequestTakebackAsync(List<int> auctionResultIds, string? initials = null)
        => await _http.PostAsJsonAsync("api/takeback-requests", new { auctionResultIds, initials });

    public async Task<List<LotSalesHistoryDto>> GetLotSalesHistoryAsync(int lotNumber)
        => await _http.GetFromJsonAsync<List<LotSalesHistoryDto>>($"api/auction-results/lot-history/{lotNumber}") ?? new();

    public async Task<HttpResponseMessage> RequestTakebackByBuyerAsync(List<int> auctionResultIds)
        => await _http.PostAsJsonAsync("api/takeback-requests/buyer-initiated", new { auctionResultIds });

    public async Task<HttpResponseMessage> ReassignUnsoldLotsAsync(List<int> auctionResultIds, string action)
        => await _http.PostAsJsonAsync("api/auction-results/reassign-unsold", new { auctionResultIds, action });

    public async Task<List<TakebackRequestDto>> GetTakebackRequestsByBuyerAsync(int buyerId)
        => await _http.GetFromJsonAsync<List<TakebackRequestDto>>($"api/takeback-requests/buyer/{buyerId}") ?? new();

    public async Task<List<TakebackRequestDto>> GetTakebackRequestsByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<TakebackRequestDto>>($"api/takeback-requests/broker/{brokerId}") ?? new();

    public async Task<HttpResponseMessage> RespondTakebackAsync(int requestId, bool approve)
        => await _http.PutAsJsonAsync($"api/takeback-requests/{requestId}", new { approve });

    // Invoices
    public async Task<List<InvoiceSummaryDto>> GetAllInvoiceSummariesAsync()
        => await _http.GetFromJsonAsync<List<InvoiceSummaryDto>>("api/settlements/invoices") ?? new();

    public async Task<List<InvoiceSummaryDto>> GetInvoicesByBrokerAsync(int brokerId)
        => await _http.GetFromJsonAsync<List<InvoiceSummaryDto>>($"api/settlements/invoices/broker/{brokerId}") ?? new();

    public async Task<List<InvoiceSummaryDto>> GetInvoicesByBuyerAsync(int buyerId)
        => await _http.GetFromJsonAsync<List<InvoiceSummaryDto>>($"api/settlements/invoices/buyer/{buyerId}") ?? new();

    public async Task<List<InvoiceSummaryDto>> GetInvoicesByBrokerAndBuyerAsync(int brokerId, int buyerId)
        => await _http.GetFromJsonAsync<List<InvoiceSummaryDto>>($"api/settlements/invoices/broker/{brokerId}/buyer/{buyerId}") ?? new();

    public string GetInvoicePdfUrl(int invoiceId)
        => $"{_http.BaseAddress}api/settlements/invoices/{invoiceId}/pdf";

    public async Task<Dictionary<int, InvoiceLinkDto>> GetInvoiceLinksForBrokerAsync(int brokerId)
    {
        try
        {
            return await _http.GetFromJsonAsync<Dictionary<int, InvoiceLinkDto>>($"api/settlements/invoice-links/broker/{brokerId}") ?? new();
        }
        catch
        {
            return new();
        }
    }

    // Unpushed invoices
    public async Task<List<InvoiceSummaryDto>> GetUnpushedInvoicesAsync()
        => await _http.GetFromJsonAsync<List<InvoiceSummaryDto>>("api/settlements/invoices/unpushed") ?? new();

    // Unpushed credit notes
    public async Task<List<InvoiceSummaryDto>> GetUnpushedCreditNotesAsync()
        => await _http.GetFromJsonAsync<List<InvoiceSummaryDto>>("api/settlements/credit-notes/unpushed") ?? new();

    // Business Central
    public async Task<BcStatusDto> GetBcStatusAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<BcStatusDto>("api/bc/status") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<BcCompanyDto>> GetBcCompaniesAsync()
        => await _http.GetFromJsonAsync<List<BcCompanyDto>>("api/bc/companies") ?? new();

    public async Task<List<BcCustomerDto>> GetBcCustomersAsync()
        => await _http.GetFromJsonAsync<List<BcCustomerDto>>("api/bc/customers") ?? new();

    public async Task<List<BcVendorDto>> GetBcVendorsAsync()
        => await _http.GetFromJsonAsync<List<BcVendorDto>>("api/bc/vendors") ?? new();

    public async Task<BcSyncResultDto?> SyncBrokersAsync()
    {
        var resp = await _http.PostAsync("api/bc/sync/brokers", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BcSyncResultDto>();
    }

    public async Task<BcSyncResultDto?> SyncBuyersAsync()
    {
        var resp = await _http.PostAsync("api/bc/sync/buyers", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BcSyncResultDto>();
    }

    public async Task<BcSyncResultDto?> SyncInvoicesAsync()
    {
        var resp = await _http.PostAsync("api/bc/sync/invoices", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BcSyncResultDto>();
    }

    public async Task<BcSyncResultDto?> SyncCreditNotesAsync()
    {
        var resp = await _http.PostAsync("api/bc/sync/credit-notes", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BcSyncResultDto>();
    }

    public async Task<BcPaymentsResponseDto> GetBcPaymentsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<BcPaymentsResponseDto>("api/bc/payments") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<BcBuyerBalancesResponseDto> GetBcBuyerBalancesAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<BcBuyerBalancesResponseDto>("api/bc/buyer-balances") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<BcSyncResultDto>?> SyncAllAsync()
    {
        var resp = await _http.PostAsync("api/bc/sync/all", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<BcSyncResultDto>>();
    }

    public async Task<List<BcCountryRegionDto>> GetBcCountriesAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<BcCountryRegionDto>>("api/bc/countries") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<BcCurrencyDto>> GetBcCurrenciesAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<BcCurrencyDto>>("api/bc/currencies") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<BcConsistencyCheckDto> GetBcConsistencyCheckAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<BcConsistencyCheckDto>("api/bc/consistency-check") ?? new();
        }
        catch
        {
            return new() { Message = "Failed to run consistency check" };
        }
    }

    public async Task<BcPaymentConsistencyDto> GetBcPaymentConsistencyCheckAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<BcPaymentConsistencyDto>("api/bc/payment-consistency-check") ?? new();
        }
        catch (Exception ex)
        {
            return new() { Message = $"Failed to run payment consistency check: {ex.Message}" };
        }
    }

    public async Task<ConnectionsInfoDto> GetConnectionsInfoAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<ConnectionsInfoDto>("api/diag/connections") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<BcPostingGroupDto>> GetBcGenBusPostingGroupsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<BcPostingGroupDto>>("api/bc/gen-bus-posting-groups") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<BcPostingGroupDto>> GetBcVatBusPostingGroupsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<BcPostingGroupDto>>("api/bc/vat-bus-posting-groups") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<BcPostingGroupDto>> GetBcCustomerPostingGroupsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<BcPostingGroupDto>>("api/bc/customer-posting-groups") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<BcPostingGroupDto>> GetBcVendorPostingGroupsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<BcPostingGroupDto>>("api/bc/vendor-posting-groups") ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<List<BcPaymentTermDto>> GetBcPaymentTermsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<BcPaymentTermDto>>("api/bc/payment-terms") ?? new();
        }
        catch
        {
            return new();
        }
    }

    // Typist Entry methods
    public async Task<TypistSubmitResult?> SubmitTypistEntryAsync(int lotNumber, int brokerId, decimal priceEur, int typistUserId)
    {
        var resp = await _http.PostAsJsonAsync("api/typist-entries", new { lotNumber, brokerId, priceEur, typistUserId });
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<TypistSubmitResult>();
        return null;
    }

    public async Task<TypistSubmitResult?> SubmitTypistReentryAsync(int lotNumber, int brokerId, decimal priceEur, int typistUserId)
    {
        var resp = await _http.PostAsJsonAsync("api/typist-entries/reentry", new { lotNumber, brokerId, priceEur, typistUserId });
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<TypistSubmitResult>();
        return null;
    }

    public async Task<List<TypistEntryDto>> GetTypistEntriesAsync()
        => await _http.GetFromJsonAsync<List<TypistEntryDto>>("api/typist-entries") ?? new();

    public async Task<List<TypistDisagreementGroup>> GetTypistDisagreementsAsync()
        => await _http.GetFromJsonAsync<List<TypistDisagreementGroup>>("api/typist-entries/disagreements") ?? new();

    public async Task<TypistLotStatus?> GetTypistLotStatusAsync(int lotNumber)
    {
        try
        {
            return await _http.GetFromJsonAsync<TypistLotStatus>($"api/typist-entries/status/{lotNumber}");
        }
        catch { return null; }
    }

    public async Task<NextUnsoldLotDto?> GetNextUnsoldLotForTypistAsync(int? typistUserId = null)
    {
        try
        {
            var url = "api/typist-entries/next-unsold-lot";
            if (typistUserId.HasValue)
                url += $"?typistUserId={typistUserId.Value}";
            return await _http.GetFromJsonAsync<NextUnsoldLotDto>(url);
        }
        catch { return null; }
    }

    public async Task<List<TypistEntryDto>> GetRecentTypistEntriesAsync()
        => await _http.GetFromJsonAsync<List<TypistEntryDto>>("api/typist-entries/recent") ?? new();

    // Auction Transactions
    public async Task<List<AuctionTransactionDto>> GetAuctionTransactionsAsync(int? auctionId = null)
    {
        var url = "api/auction-transactions";
        if (auctionId.HasValue)
            url += $"?auctionId={auctionId.Value}";
        return await _http.GetFromJsonAsync<List<AuctionTransactionDto>>(url) ?? new();
    }

    public async Task<List<AuctionTransactionSummaryDto>> GetAuctionTransactionSummaryAsync(int? auctionId = null)
    {
        var url = "api/auction-transactions/summary";
        if (auctionId.HasValue)
            url += $"?auctionId={auctionId.Value}";
        return await _http.GetFromJsonAsync<List<AuctionTransactionSummaryDto>>(url) ?? new();
    }

    // Sales Order Setup — Lot Group Orders
    public async Task<List<LotGroupOrderDto>> GetLotGroupOrdersAsync()
        => await _http.GetFromJsonAsync<List<LotGroupOrderDto>>("api/sales-order-setup/lot-group-orders") ?? new();

    public async Task<(LotGroupOrderDto? Data, string? Error)> CreateLotGroupOrderAsync(LotGroupOrderDto dto)
    {
        var resp = await _http.PostAsJsonAsync("api/sales-order-setup/lot-group-orders", dto);
        if (!resp.IsSuccessStatusCode) return (null, await GetErrorMessage(resp));
        return (await resp.Content.ReadFromJsonAsync<LotGroupOrderDto>(), null);
    }

    public async Task<bool> ReorderLotGroupOrdersAsync(List<LotGroupOrderDto> items)
    {
        var resp = await _http.PutAsJsonAsync("api/sales-order-setup/lot-group-orders/reorder", items);
        return resp.IsSuccessStatusCode;
    }

    public async Task<(LotGroupOrderDto? Data, string? Error)> UpdateLotGroupOrderAsync(LotGroupOrderDto dto, string? originalColumnName = null)
    {
        try
        {
            var payload = new { originalColumnName, dto.ColumnName, dto.GroupOrder };
            var resp = await _http.PutAsJsonAsync("api/sales-order-setup/lot-group-orders/update", payload);
            if (!resp.IsSuccessStatusCode) return (null, await GetErrorMessage(resp));
            return (await resp.Content.ReadFromJsonAsync<LotGroupOrderDto>(), null);
        }
        catch (Exception ex) { return (null, $"Request failed: {ex.Message}"); }
    }

    public async Task<(bool Success, string? Error)> DeleteLotGroupOrderAsync(LotGroupOrderDto dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "api/sales-order-setup/lot-group-orders/remove")
        { Content = JsonContent.Create(dto) };
        var resp = await _http.SendAsync(request);
        if (!resp.IsSuccessStatusCode) return (false, await GetErrorMessage(resp));
        return (true, null);
    }

    // Sales Order Setup — Lot Size Rules
    public async Task<List<LotSizeRuleDto>> GetLotSizeRulesAsync()
        => await _http.GetFromJsonAsync<List<LotSizeRuleDto>>("api/sales-order-setup/lot-size-rules") ?? new();

    public async Task<(LotSizeRuleDto? Data, string? Error)> CreateLotSizeRuleAsync(LotSizeRuleDto dto)
    {
        var resp = await _http.PostAsJsonAsync("api/sales-order-setup/lot-size-rules", dto);
        if (!resp.IsSuccessStatusCode) return (null, await GetErrorMessage(resp));
        return (await resp.Content.ReadFromJsonAsync<LotSizeRuleDto>(), null);
    }

    public async Task<(LotSizeRuleDto? Data, string? Error)> UpdateLotSizeRuleAsync(LotSizeRuleDto dto)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("api/sales-order-setup/lot-size-rules/update", dto);
            if (!resp.IsSuccessStatusCode) return (null, await GetErrorMessage(resp));
            return (await resp.Content.ReadFromJsonAsync<LotSizeRuleDto>(), null);
        }
        catch (Exception ex) { return (null, $"Request failed: {ex.Message}"); }
    }

    public async Task<(bool Success, string? Error)> DeleteLotSizeRuleAsync(LotSizeRuleDto dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "api/sales-order-setup/lot-size-rules/remove")
        { Content = JsonContent.Create(dto) };
        var resp = await _http.SendAsync(request);
        if (!resp.IsSuccessStatusCode) return (false, await GetErrorMessage(resp));
        return (true, null);
    }

    // Sales Order Setup — Lot Sort Orders
    public async Task<List<LotSortOrderDto>> GetLotSortOrdersAsync()
        => await _http.GetFromJsonAsync<List<LotSortOrderDto>>("api/sales-order-setup/lot-sort-orders") ?? new();

    public async Task<(LotSortOrderDto? Data, string? Error)> CreateLotSortOrderAsync(LotSortOrderDto dto)
    {
        var resp = await _http.PostAsJsonAsync("api/sales-order-setup/lot-sort-orders", dto);
        if (!resp.IsSuccessStatusCode) return (null, await GetErrorMessage(resp));
        return (await resp.Content.ReadFromJsonAsync<LotSortOrderDto>(), null);
    }

    public async Task<bool> ReorderLotSortOrdersAsync(List<LotSortOrderDto> items)
    {
        var resp = await _http.PutAsJsonAsync("api/sales-order-setup/lot-sort-orders/reorder", items);
        return resp.IsSuccessStatusCode;
    }

    public async Task<(LotSortOrderDto? Data, string? Error)> UpdateLotSortOrderAsync(LotSortOrderDto dto, string? originalColumnName = null, string? originalValue = null)
    {
        try
        {
            var payload = new { originalColumnName, originalValue, dto.ColumnName, dto.Value, dto.SortOrder };
            var resp = await _http.PutAsJsonAsync("api/sales-order-setup/lot-sort-orders/update", payload);
            if (!resp.IsSuccessStatusCode) return (null, await GetErrorMessage(resp));
            return (await resp.Content.ReadFromJsonAsync<LotSortOrderDto>(), null);
        }
        catch (Exception ex) { return (null, $"Request failed: {ex.Message}"); }
    }

    public async Task<(bool Success, string? Error)> DeleteLotSortOrderAsync(LotSortOrderDto dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "api/sales-order-setup/lot-sort-orders/remove")
        { Content = JsonContent.Create(dto) };
        var resp = await _http.SendAsync(request);
        if (!resp.IsSuccessStatusCode) return (false, await GetErrorMessage(resp));
        return (true, null);
    }

    // Sales Order Setup — Catalog Number Rules
    public async Task<List<CatalogNumberRuleDto>> GetCatalogNumberRulesAsync()
        => await _http.GetFromJsonAsync<List<CatalogNumberRuleDto>>("api/sales-order-setup/catalog-number-rules") ?? new();

    public async Task<(CatalogNumberRuleDto? Data, string? Error)> CreateCatalogNumberRuleAsync(CatalogNumberRuleDto dto)
    {
        var resp = await _http.PostAsJsonAsync("api/sales-order-setup/catalog-number-rules", dto);
        if (!resp.IsSuccessStatusCode) return (null, await GetErrorMessage(resp));
        return (await resp.Content.ReadFromJsonAsync<CatalogNumberRuleDto>(), null);
    }

    public async Task<(CatalogNumberRuleDto? Data, string? Error)> UpdateCatalogNumberRuleAsync(CatalogNumberRuleDto dto)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("api/sales-order-setup/catalog-number-rules/update", dto);
            if (!resp.IsSuccessStatusCode) return (null, await GetErrorMessage(resp));
            return (await resp.Content.ReadFromJsonAsync<CatalogNumberRuleDto>(), null);
        }
        catch (Exception ex) { return (null, $"Request failed: {ex.Message}"); }
    }

    public async Task<(bool Success, string? Error)> DeleteCatalogNumberRuleAsync(CatalogNumberRuleDto dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "api/sales-order-setup/catalog-number-rules/remove")
        { Content = JsonContent.Create(dto) };
        var resp = await _http.SendAsync(request);
        if (!resp.IsSuccessStatusCode) return (false, await GetErrorMessage(resp));
        return (true, null);
    }

    // Shipping Parameters
    public async Task<List<BoxTypeDimensionDto>> GetBoxTypeDimensionsAsync()
        => await _http.GetFromJsonAsync<List<BoxTypeDimensionDto>>("api/shipping-parameters/box-types") ?? new();

    public async Task<(bool Success, string? Error)> SaveAllBoxTypeDimensionsAsync(List<BoxTypeDimensionDto> items)
    {
        var resp = await _http.PostAsJsonAsync("api/shipping-parameters/box-types/bulk", items);
        if (!resp.IsSuccessStatusCode) return (false, await GetErrorMessage(resp));
        return (true, null);
    }

    private static async Task<string> GetErrorMessage(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString() ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch { }
        return $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
    }
}
