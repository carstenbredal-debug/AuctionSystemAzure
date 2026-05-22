namespace AuctionSystem.Domain.Entities;

public class Broker
{
    public int Id { get; set; }
    public string BrokerNumber { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyName2 { get; set; } = string.Empty;
    public string ErpAccountNumber { get; set; } = string.Empty;
    public string SearchName { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string AddressLine2 { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string MobilePhone { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string HomePage { get; set; } = string.Empty;
    public string VatRegistrationNo { get; set; } = string.Empty;
    public string RegistrationNo { get; set; } = string.Empty;
    public string CustomerGroup { get; set; } = string.Empty;
    public string SalesPerson { get; set; } = string.Empty;
    public string PaymentTerm { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    [Obsolete("Use AddressLine1 instead")]
    public string Address { get; set; } = string.Empty;
    public string? BcCustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Buyer> Buyers { get; set; } = new List<Buyer>();
    public ICollection<Bid> Bids { get; set; } = new List<Bid>();
    public ICollection<LotAllocation> Allocations { get; set; } = new List<LotAllocation>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}
