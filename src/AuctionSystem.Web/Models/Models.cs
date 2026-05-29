namespace AuctionSystem.Web.Models;

public enum AuctionStatus { Draft, Active, Closed }
public enum LotStatus { Pending, Active, Sold, Unsold, Withdrawn, Broker }
public enum BidStatus { Active, Outbid, Winning, Won, Cancelled }
public enum AllocationStatus { Pending, Allocated, Delivered, Cancelled }
public enum InvoiceStatus { Draft, Issued, Sent, Paid, Overdue, Cancelled }
public enum SettlementStatus { Pending, InvoiceGenerated, PaymentReceived, SettledWithSeller, Completed, Disputed }

public class AuctionDto
{
    public int Id { get; set; }
    public string AuctionNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Location { get; set; } = "";
    public DateTime ScheduledDate { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public AuctionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<LotDto> Lots { get; set; } = new();
    public int LotCount { get; set; }
}

public class LotDto
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public int Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal StartingPrice { get; set; }
    public decimal? HammerPrice { get; set; }
    public LotStatus Status { get; set; }
    public int AuctionId { get; set; }
    public AuctionDto? Auction { get; set; }
    public int SellerId { get; set; }
    public SellerDto? Seller { get; set; }
    public List<BidDto> Bids { get; set; } = new();
}

public class SellerDto
{
    public int Id { get; set; }
    public string SellerNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string Name2 { get; set; } = "";
    public string SearchName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string Country { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string MobilePhone { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string HomePage { get; set; } = "";
    public string VatRegistrationNo { get; set; } = "";
    public string RegistrationNo { get; set; } = "";
    public string CustomerGroup { get; set; } = "";
    public string SalesPerson { get; set; } = "";
    public string PaymentTerm { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Language { get; set; } = "";
    public string BankName { get; set; } = "";
    public string BankAddress { get; set; } = "";
    public string BankIbanNumber { get; set; } = "";
    public string SwiftCode { get; set; } = "";
    public string BankCountry { get; set; } = "";
    public string Assignee { get; set; } = "";
    public bool AssignmentOfReceivable { get; set; }
    public bool IsActive { get; set; } = true;
    public string Address { get; set; } = "";
}

public class BrokerDto
{
    public int Id { get; set; }
    public string BrokerNumber { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string CompanyName2 { get; set; } = "";
    public string SearchName { get; set; } = "";
    public string ContactPerson { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string Country { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string MobilePhone { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string HomePage { get; set; } = "";
    public string VatRegistrationNo { get; set; } = "";
    public string RegistrationNo { get; set; } = "";
    public string CustomerGroup { get; set; } = "";
    public string SalesPerson { get; set; } = "";
    public string PaymentTerm { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Language { get; set; } = "";
    public string GenBusPostingGroup { get; set; } = "";
    public string VatBusPostingGroup { get; set; } = "";
    public string CustomerPostingGroup { get; set; } = "";
    public decimal CreditLimit { get; set; }
    public string Blocked { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class BuyerDto
{
    public int Id { get; set; }
    public string BuyerNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string Name2 { get; set; } = "";
    public string SearchName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string Country { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string MobilePhone { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string HomePage { get; set; } = "";
    public string VatRegistrationNo { get; set; } = "";
    public string RegistrationNo { get; set; } = "";
    public string CustomerGroup { get; set; } = "";
    public string SalesPerson { get; set; } = "";
    public string PaymentTerm { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Language { get; set; } = "";
    public string BankName { get; set; } = "";
    public string BankAddress { get; set; } = "";
    public string BankIbanNumber { get; set; } = "";
    public string SwiftCode { get; set; } = "";
    public string BankCountry { get; set; } = "";
    public string Assignee { get; set; } = "";
    public bool AssignmentOfReceivable { get; set; }
    public bool IsActive { get; set; } = true;
    public string Address { get; set; } = "";
    public int BrokerId { get; set; }
    public BrokerDto? Broker { get; set; }
    public List<BrokerDto> Brokers { get; set; } = new();
}

public class BidDto
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public BidStatus Status { get; set; }
    public DateTime PlacedAt { get; set; }
    public int LotId { get; set; }
    public LotDto? Lot { get; set; }
    public int BrokerId { get; set; }
    public BrokerDto? Broker { get; set; }
}

public class LotAllocationDto
{
    public int Id { get; set; }
    public int Quantity { get; set; }
    public decimal PricePerUnit { get; set; }
    public decimal TotalPrice { get; set; }
    public AllocationStatus Status { get; set; }
    public DateTime AllocatedAt { get; set; }
    public int LotId { get; set; }
    public LotDto? Lot { get; set; }
    public int BrokerId { get; set; }
    public BrokerDto? Broker { get; set; }
    public int BuyerId { get; set; }
    public BuyerDto? Buyer { get; set; }
}

public class InvoiceDto
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public decimal SubTotal { get; set; }
    public decimal Commission { get; set; }
    public decimal Tax { get; set; }
    public decimal TotalAmount { get; set; }
    public InvoiceStatus Status { get; set; }
    public DateTime IssuedDate { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public int BrokerId { get; set; }
    public BrokerDto? Broker { get; set; }
    public int AuctionId { get; set; }
    public AuctionDto? Auction { get; set; }
    public List<InvoiceLineDto> Lines { get; set; } = new();
}

public class InvoiceLineDto
{
    public int Id { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public int InvoiceId { get; set; }
    public int LotId { get; set; }
    public LotDto? Lot { get; set; }
}

public class SettlementDto
{
    public int Id { get; set; }
    public string SettlementNumber { get; set; } = "";
    public decimal GrossAmount { get; set; }
    public decimal Commission { get; set; }
    public decimal Fees { get; set; }
    public decimal NetAmount { get; set; }
    public SettlementStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public int LotId { get; set; }
    public LotDto? Lot { get; set; }
    public int SellerId { get; set; }
    public SellerDto? Seller { get; set; }
}

public class AppUserDto
{
    public int Id { get; set; }
    public string AzureAdObjectId { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public int? BrokerId { get; set; }
    public int? SellerId { get; set; }
    public int? BuyerId { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum CustomerRequestStatus { Pending, Approved, Declined }

public class BrokerCustomerRequestDto
{
    public int Id { get; set; }
    public int BrokerId { get; set; }
    public BrokerDto? Broker { get; set; }
    public int BuyerId { get; set; }
    public BuyerDto? Buyer { get; set; }
    public CustomerRequestStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
}

public class SystemParameterDto
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Description { get; set; } = "";
    public string DataType { get; set; } = "string";
    public DateTime UpdatedAt { get; set; }
}

public class CatalogLotDto
{
    public int CatalogLotID { get; set; }
    public Guid LotUniqueID { get; set; }
    public int StringNumber { get; set; }
    public int LotNumber { get; set; }
    public int CatalogSortOrder { get; set; }
    public string IsShow { get; set; } = "";
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public string? HairLength { get; set; }
    public string? Size { get; set; }
    public string? Quality { get; set; }
    public string? Color { get; set; }
    public string? Clarity { get; set; }
    public string? Damages { get; set; }
    public string? IncludedBoxNumbers { get; set; }
    public int BoxCount { get; set; }
    public int TotalSkins { get; set; }
}

public class ImportCatalogLotsRequest
{
    public List<int> CatalogLotIds { get; set; } = new();
    public int SellerId { get; set; }
}

public class NextUnsoldLotDto
{
    public int LotNumber { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public int Quantity { get; set; }
    public string? Unit { get; set; }
}

public class AuctionResultDto
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public int BrokerId { get; set; }
    public string BrokerName { get; set; } = "";
    public string BrokerNumber { get; set; } = "";
    public decimal PriceEur { get; set; }
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public string? Color { get; set; }
    public string? Quality { get; set; }
    public string? Size { get; set; }
    public string? Clarity { get; set; }
    public string? HairLength { get; set; }
    public int TotalSkins { get; set; }
    public int BoxCount { get; set; }
    public bool Processed { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int? SoldToBuyerId { get; set; }
    public string? SoldToBuyerName { get; set; }
    public string? SoldToBuyerNumber { get; set; }
    public DateTime? SoldAt { get; set; }
    public string? CommissionType { get; set; }
    public decimal? CommissionValue { get; set; }
    public decimal? CommissionAmount { get; set; }
}

public class DashboardStats
{
    public int TotalAuctions { get; set; }
    public int ActiveAuctions { get; set; }
    public int TotalLots { get; set; }
    public int SoldLots { get; set; }
    public int TotalBrokers { get; set; }
    public int TotalBuyers { get; set; }
    public int TotalSellers { get; set; }
    public int TotalBids { get; set; }
    public int PendingInvoices { get; set; }
    public int PendingSettlements { get; set; }
    public List<AuctionDto> RecentAuctions { get; set; } = new();
}

public class TakebackRequestDto
{
    public int Id { get; set; }
    public int AuctionResultId { get; set; }
    public int LotNumber { get; set; }
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Color { get; set; }
    public string? Quality { get; set; }
    public int TotalSkins { get; set; }
    public decimal PriceEur { get; set; }
    public string? BrokerName { get; set; }
    public string? BrokerNumber { get; set; }
    public string? BuyerName { get; set; }
    public string? BuyerNumber { get; set; }
    public string InitiatedBy { get; set; } = "Broker";
    public CustomerRequestStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
}

public class InvoiceSummaryDto
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public DateTime InvoiceDate { get; set; }
    public decimal SubTotal { get; set; }
    public decimal AuctionFee { get; set; }
    public decimal Commission { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "";
    public string? BrokerName { get; set; }
    public string? BuyerName { get; set; }
    public int LinesCount { get; set; }
    public bool IsCreditNote { get; set; }
    public string? OriginalInvoiceNumber { get; set; }
    public string? PdfUrl { get; set; }
}

public class InvoiceLinkDto
{
    public List<InvoiceDocDto> Documents { get; set; } = new();
}

public class InvoiceDocDto
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string? PdfUrl { get; set; }
    public bool IsCreditNote { get; set; }
}

// Business Central integration models
public class BcStatusDto
{
    public bool Configured { get; set; }
    public string? Message { get; set; }
    public BcSyncStatusDto? Status { get; set; }
}

public class BcSyncStatusDto
{
    public BcEntityCountDto Brokers { get; set; } = new();
    public BcEntityCountDto Buyers { get; set; } = new();
    public int Invoices { get; set; }
    public int CreditNotes { get; set; }
}

public class BcEntityCountDto
{
    public int Total { get; set; }
    public int Synced { get; set; }
    public int Unsynced { get; set; }
}

public class BcSyncResultDto
{
    public string Direction { get; set; } = "";
    public string EntityType { get; set; } = "";
    public int TotalProcessed { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTimeOffset Timestamp { get; set; }
}

public class BcCompanyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class BcCustomerDto
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
    public string Email { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
}

public class BcCountryRegionDto
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class BcCurrencyDto
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Symbol { get; set; } = "";
}

public class BcPostingGroupDto
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
}

public class BcPaymentTermDto
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
}
