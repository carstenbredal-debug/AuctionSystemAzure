namespace AuctionSystem.Web.Models;

public enum AuctionStatus { Draft, Active, Closed }
public enum LotStatus { Pending, Active, Sold, Unsold, Withdrawn, Broker }
public enum BidStatus { Active, Outbid, Winning, Won, Cancelled }
public enum AllocationStatus { Pending, Allocated, Delivered, Cancelled }
public enum InvoiceStatus { Draft, Issued, Sent, Downpayment, Paid, Overdue, Cancelled, PartiallyCredited, FullyCredited, Alloted, ReleasedToShip }
public enum SettlementStatus { Pending, InvoiceGenerated, PaymentReceived, SettledWithFarmer, Completed, Disputed }

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
    public int FarmerId { get; set; }
    public FarmerDto? Farmer { get; set; }
    public List<BidDto> Bids { get; set; } = new();
}

public class FarmerDto
{
    public int Id { get; set; }
    public string FarmerNumber { get; set; } = "";
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
    public decimal CreditLimit { get; set; }
    public string Blocked { get; set; } = "";
    public string GenBusPostingGroup { get; set; } = "";
    public string VatBusPostingGroup { get; set; } = "";
    public string VendorPostingGroup { get; set; } = "";
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
    public decimal CreditLimit { get; set; }
    public string Blocked { get; set; } = "";
    public string GenBusPostingGroup { get; set; } = "";
    public string VatBusPostingGroup { get; set; } = "";
    public string CustomerPostingGroup { get; set; } = "";
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
    public string Status { get; set; } = "";
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
    public int FarmerId { get; set; }
    public FarmerDto? Farmer { get; set; }
}

public class AppUserDto
{
    public int Id { get; set; }
    public string AzureAdObjectId { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public int? BrokerId { get; set; }
    public int? FarmerId { get; set; }
    public int? BuyerId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class BulkCreateResult
{
    public int CreatedCount { get; set; }
    public int Brokers { get; set; }
    public int Buyers { get; set; }
    public int Farmers { get; set; }
}

public enum CustomerRequestStatus { Pending, Approved, Declined }

public class BrokerCustomerRequestDto
{
    public int Id { get; set; }
    public int BrokerId { get; set; }
    public BrokerDto? Broker { get; set; }
    public int BuyerId { get; set; }
    public BuyerDto? Buyer { get; set; }
    public string InitiatedBy { get; set; } = "Broker";
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
    public int FarmerId { get; set; }
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
    public string? LastModifiedBy { get; set; }
    public DateTime? LastModifiedAt { get; set; }
}

public class LotSalesHistoryDto
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public string ActionType { get; set; } = "";
    public string? Initials { get; set; }
    public int? BuyerId { get; set; }
    public string? BuyerName { get; set; }
    public int? InvoiceId { get; set; }
    public string? InvoiceNumber { get; set; }
    public decimal? Amount { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class DashboardStats
{
    public int TotalAuctions { get; set; }
    public int ActiveAuctions { get; set; }
    public int TotalLots { get; set; }
    public int SoldLots { get; set; }
    public int TotalBrokers { get; set; }
    public int TotalBuyers { get; set; }
    public int TotalFarmers { get; set; }
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
    public string? BuyerNumber { get; set; }
    public int LinesCount { get; set; }
    public bool IsCreditNote { get; set; }
    public string? OriginalInvoiceNumber { get; set; }
    public string? PdfUrl { get; set; }
    public string? ShippingStatus { get; set; }
    public decimal? DownpaymentAmount { get; set; }
    public decimal? DownpaymentPercentage { get; set; }
    public decimal? RemainingBalance { get; set; }
    public decimal CreditedAmount { get; set; }
    public int CreditedLots { get; set; }
    public List<int> UncreditedLotNumbers { get; set; } = new();
    public List<InvoiceLotDto> Lots { get; set; } = new();
}

public class InvoiceLotDto
{
    public int LotNumber { get; set; }
    public string Description { get; set; } = "";
    public int Skins { get; set; }
    public decimal PricePerSkin { get; set; }
    public decimal SubTotal { get; set; }
    public bool IsCredited { get; set; }
}

public class ShippingBoxDto
{
    public int InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public string? BrokerName { get; set; }
    public string? BuyerName { get; set; }
    public int LotNumber { get; set; }
    public int BoxNumber { get; set; }
    public string BoxType { get; set; } = "";
    public int Skins { get; set; }
    public decimal PricePerSkin { get; set; }
    public decimal HammerPrice { get; set; }
    public decimal? VolumeM3 { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? GrossWeight { get; set; }
    public decimal? NetWeight { get; set; }
    public string? BoxLocation { get; set; }
}

public class ShippingAddressListDto
{
    public int Id { get; set; }
    public int BuyerId { get; set; }
    public string BuyerName { get; set; } = "";
    public string BuyerNumber { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string Country { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string City { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string MobilePhone { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public bool IsDefault { get; set; }
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
    public decimal CreditLimit { get; set; }
    public string Blocked { get; set; } = "";
    public string GenBusPostingGroup { get; set; } = "";
    public string VatBusPostingGroup { get; set; } = "";
    public string CustomerPostingGroup { get; set; } = "";
    public string PaymentTermsCode { get; set; } = "";
    public string PaymentMethodCode { get; set; } = "";
    public string VatRegistrationNo { get; set; } = "";
}

public class BcVendorDto
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
    public string Email { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
    public string Blocked { get; set; } = "";
    public string GenBusPostingGroup { get; set; } = "";
    public string VatBusPostingGroup { get; set; } = "";
    public string VendorPostingGroup { get; set; } = "";
    public string PaymentTermsCode { get; set; } = "";
    public string PaymentMethodCode { get; set; } = "";
    public string VatRegistrationNo { get; set; } = "";
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

// Typist entry models
public class TypistEntryDto
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public int BrokerId { get; set; }
    public string BrokerNumber { get; set; } = "";
    public string BrokerName { get; set; } = "";
    public decimal PriceEur { get; set; }
    public int TypistUserId { get; set; }
    public string TypistName { get; set; } = "";
    public int TypistSlot { get; set; }
    public DateTime EnteredAt { get; set; }
    public bool IsMatched { get; set; }
    public bool IsDisagreement { get; set; }
    public bool IsResolved { get; set; }
    public int? MatchedWithEntryId { get; set; }
    public int? AuctionResultId { get; set; }
}

public class TypistSubmitResult
{
    public int EntryId { get; set; }
    public int LotNumber { get; set; }
    public int Slot { get; set; }
    public bool IsMatched { get; set; }
    public bool IsDisagreement { get; set; }
    public bool WaitingForOtherTypist { get; set; }
}

public class TypistDisagreementGroup
{
    public int LotNumber { get; set; }
    public List<TypistEntryDto> Entries { get; set; } = new();
}

public class TypistLotStatus
{
    public int LotNumber { get; set; }
    public int EntriesCount { get; set; }
    public bool IsComplete { get; set; }
    public bool IsMatched { get; set; }
    public bool IsDisagreement { get; set; }
    public List<TypistEntryDto> Entries { get; set; } = new();
}

// Auction Transaction models
public class AuctionTransactionDto
{
    public int Id { get; set; }
    public int AuctionId { get; set; }
    public int LotNumber { get; set; }
    public string TransactionType { get; set; } = "";
    public int BrokerId { get; set; }
    public string BrokerNumber { get; set; } = "";
    public string BrokerName { get; set; } = "";
    public int? BuyerId { get; set; }
    public string? BuyerNumber { get; set; }
    public string? BuyerName { get; set; }
    public string Description { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string? DebitAccount { get; set; }
    public string? CreditAccount { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? AuctionResultId { get; set; }
}

public class AuctionTransactionSummaryDto
{
    public string TransactionType { get; set; } = "";
    public int Count { get; set; }
    public decimal TotalAmount { get; set; }
}

public class BcConsistencyCheckDto
{
    public string Message { get; set; } = "";
    public BcConsistencyCheckedDto? Checked_ { get; set; }
    public List<BcConsistencyMismatchDto> Mismatches { get; set; } = new();
}

public class BcConsistencyCheckedDto
{
    public int Brokers { get; set; }
    public int Buyers { get; set; }
    public int Farmers { get; set; }
}

public class BcConsistencyMismatchDto
{
    public string Entity { get; set; } = "";
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string Field { get; set; } = "";
    public string Auction { get; set; } = "";
    public string Bc { get; set; } = "";
}

public class BcBalanceCheckDto
{
    public bool Available { get; set; }
    public string? Reason { get; set; }
    public decimal AvailableAmount { get; set; }
    public decimal InvoiceAmount { get; set; }
    public bool Sufficient { get; set; }
    public string BuyerNumber { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public int OpenPaymentCount { get; set; }
}

public class BcInvoiceRemainingDto
{
    public bool FullyPaidInBc { get; set; }
    public decimal RemainingAmount { get; set; }
    public string? Status { get; set; }
    public string? Reason { get; set; }
}

public class PaymentHistoryDto
{
    public string InvoiceNumber { get; set; } = "";
    public string? BcInvoiceNumber { get; set; }
    public string? CustomerNo { get; set; }
    public string? CustomerName { get; set; }
    public decimal InvoiceTotal { get; set; }
    public decimal? BcOriginalAmount { get; set; }
    public decimal BcAmountApplied { get; set; }
    public decimal? BcRemainingAmount { get; set; }
    public bool? BcOpen { get; set; }
    public string? Message { get; set; }
    public List<PaymentHistoryEntryDto> Entries { get; set; } = new();
}

public class PaymentHistoryEntryDto
{
    public string Type { get; set; } = "";
    public string DocumentNo { get; set; } = "";
    public string PostingDate { get; set; } = "";
    public decimal OriginalAmount { get; set; }
    public decimal AmountApplied { get; set; }
    public decimal RemainingAmount { get; set; }
    public bool Open { get; set; }
    public string? Description { get; set; }
}

public class BcPaymentConsistencyDto
{
    public string Message { get; set; } = "";
    public int TotalChecked { get; set; }
    public int Mismatches { get; set; }
    public List<BcPaymentInvoiceStatusDto> Invoices { get; set; } = new();
}

public class BcPaymentInvoiceStatusDto
{
    public int InvoiceId { get; set; }
    public string BcInvoiceNumber { get; set; } = "";
    public string BuyerNumber { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public string LocalStatus { get; set; } = "";
    public string BcStatus { get; set; } = "";
    public decimal InvoiceAmount { get; set; }
    public decimal BcRemainingAmount { get; set; }
    public decimal OpenPaymentAvailable { get; set; }
    public string? Issue { get; set; }
}

public class ConnectionsInfoDto
{
    public BcConnectionInfoDto BusinessCentral { get; set; } = new();
    public AzureConnectionInfoDto Azure { get; set; } = new();
}

public class BcConnectionInfoDto
{
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public string TenantId { get; set; } = "";
    public string Environment { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public string Error { get; set; } = "";
}

public class AzureConnectionInfoDto
{
    public SqlConnectionInfoDto Sql { get; set; } = new();
    public BlobConnectionInfoDto BlobStorage { get; set; } = new();
}

public class SqlConnectionInfoDto
{
    public bool Connected { get; set; }
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Error { get; set; } = "";
}

public class BlobConnectionInfoDto
{
    public bool Configured { get; set; }
}

public class BcPaymentsResponseDto
{
    public List<BcCustomerBalanceDto> CustomerBalances { get; set; } = new();
    public List<BcPaymentEntryDto> JournalPayments { get; set; } = new();
    public List<BcPaidInvoiceDto> PaidInvoices { get; set; } = new();
    public List<BcPaidInvoiceDto> AllInvoiceStatuses { get; set; } = new();
    public List<BcCustomerLedgerEntryDto> CustomerLedgerEntries { get; set; } = new();
    public string? LedgerError { get; set; }
    public List<BcGlEntryDto> GlPaymentEntries { get; set; } = new();
    public string? GlError { get; set; }
    public int TotalCustomers { get; set; }
    public int TotalJournalPayments { get; set; }
    public int TotalPaidInvoices { get; set; }
    public int TotalCustomerLedgerEntries { get; set; }
    public int TotalGlPaymentEntries { get; set; }
}

public class BcCustomerBalanceDto
{
    public string Number { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public decimal Balance { get; set; }
    public decimal OverdueAmount { get; set; }
    public string CurrencyCode { get; set; } = "";
}

public class BcPaidInvoiceDto
{
    public string Number { get; set; } = "";
    public string ExternalDocumentNumber { get; set; } = "";
    public string CustomerNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public decimal TotalAmountIncludingTax { get; set; }
    public decimal RemainingAmount { get; set; }
    public string InvoiceDate { get; set; } = "";
    public string Status { get; set; } = "";
}

public class BcPaymentEntryDto
{
    public string Journal { get; set; } = "";
    public string CustomerNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string PostingDate { get; set; } = "";
    public string DocumentNumber { get; set; } = "";
    public string ExternalDocumentNumber { get; set; } = "";
    public decimal Amount { get; set; }
    public string AppliesToInvoiceNumber { get; set; } = "";
    public string Description { get; set; } = "";
}

public class BcCustomerLedgerEntryDto
{
    public int EntryNo { get; set; }
    public string PostingDate { get; set; } = "";
    public string DocumentNo { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string CustomerNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal RemainingAmount { get; set; }
    public bool Open { get; set; }
    public string ExternalDocumentNo { get; set; } = "";
}

public class BcBuyerBalancesResponseDto
{
    public List<BcBuyerBalanceDto> Buyers { get; set; } = new();
    public string LastChecked { get; set; } = "";
}

public class BcBuyerBalanceDto
{
    public string CustomerNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string? BuyerName { get; set; }
    public decimal TotalInvoiced { get; set; }
    public decimal TotalCredited { get; set; }
    public decimal TotalPayments { get; set; }
    public decimal OpenInvoiceRemaining { get; set; }
    public decimal UnallocatedPayment { get; set; }
    public int OpenInvoiceCount { get; set; }
    public int OpenPaymentCount { get; set; }
}

public class BcGlEntryDto
{
    public int EntryNumber { get; set; }
    public string PostingDate { get; set; } = "";
    public string DocumentNumber { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
}

// Sales Order Setup
public class LotGroupOrderDto
{
    public string ColumnName { get; set; } = string.Empty;
    public int GroupOrder { get; set; }
}

public class LotSizeRuleDto
{
    public int RuleID { get; set; }
    public string? Gender { get; set; }
    public string? Size { get; set; }
    public int MaxBoxes { get; set; }
    public int MaxSkinsPerBox { get; set; }
    public int ShowlotSkins { get; set; }
    public int MaxLotSizeExclShowlot { get; set; }
    public int MaxLotSizeInclShowlot { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; }
}

public class LotSortOrderDto
{
    public string ColumnName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class CatalogNumberRuleDto
{
    public int CatalogNumberRuleID { get; set; }
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public int StartNumber { get; set; }
    public bool IsActive { get; set; }
}

public class BoxTypeDimensionDto
{
    public string BoxType { get; set; } = "";
    public decimal HeightM { get; set; }
    public decimal WidthM { get; set; }
    public decimal LengthM { get; set; }
    public decimal WeightKg { get; set; }
    public int? Id { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ShipmentListDto
{
    public int Id { get; set; }
    public string ShipmentNumber { get; set; } = "";
    public int ShipperId { get; set; }
    public string ShipperName { get; set; } = "";
    public string ShipperCode { get; set; } = "";
    public int BuyerId { get; set; }
    public string BuyerName { get; set; } = "";
    public string BuyerNumber { get; set; } = "";
    public int? ShippingAddressId { get; set; }
    public string ShippingAddressSummary { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public string Status { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public int LotCount { get; set; }
    public List<ShipmentLotDto> Lots { get; set; } = new();
}

public class ShipmentLotDto
{
    public int LotNumber { get; set; }
    public int? InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public string? PdfUrl { get; set; }
    public string Notes { get; set; } = "";
}

public class ReleasedLotDto
{
    public int LotNumber { get; set; }
    public int InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public int BuyerId { get; set; }
    public string BuyerName { get; set; } = "";
    public string BuyerNumber { get; set; } = "";
    public string BrokerName { get; set; } = "";
    public int Skins { get; set; }
    public decimal PricePerSkin { get; set; }
    public decimal HammerPrice { get; set; }
}

public class ShipmentPackingListDto
{
    public string ShipmentNumber { get; set; } = "";
    public string ShipperName { get; set; } = "";
    public string ShipperCode { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public string BuyerNumber { get; set; } = "";
    public ShipmentAddressDto? ShippingAddress { get; set; }
    public string TrackingNumber { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public PackingTotalsDto Totals { get; set; } = new();
    public List<PackingLineDto> PackingLines { get; set; } = new();
}

public class ShipmentAddressDto
{
    public string ContactName { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
}

public class PackingTotalsDto
{
    public int Boxes { get; set; }
    public int Skins { get; set; }
    public decimal GrossWeightKg { get; set; }
    public decimal NetWeightKg { get; set; }
    public decimal VolumeM3 { get; set; }
}

public class PackingLineDto
{
    public int LotNumber { get; set; }
    public int BoxNumber { get; set; }
    public string BoxType { get; set; } = "";
    public int Skins { get; set; }
    public decimal GrossWeightKg { get; set; }
    public decimal NetWeightKg { get; set; }
    public decimal VolumeM3 { get; set; }
    public decimal TareWeightKg { get; set; }
    public string Location { get; set; } = "";
}

public class ShipperListDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
    public string Website { get; set; } = "";
    public string TrackingUrlTemplate { get; set; } = "";
    public bool IsActive { get; set; }
}
