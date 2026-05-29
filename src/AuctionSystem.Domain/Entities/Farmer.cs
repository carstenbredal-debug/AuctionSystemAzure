namespace AuctionSystem.Domain.Entities;

public class Farmer
{
    public int Id { get; set; }
    public string FarmerNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Name2 { get; set; } = string.Empty;
    public string SearchName { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
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
    public string BankName { get; set; } = string.Empty;
    public string BankAddress { get; set; } = string.Empty;
    public string BankIbanNumber { get; set; } = string.Empty;
    public string SwiftCode { get; set; } = string.Empty;
    public string BankCountry { get; set; } = string.Empty;
    public string Assignee { get; set; } = string.Empty;
    public bool AssignmentOfReceivable { get; set; }
    public decimal CreditLimit { get; set; }
    public string Blocked { get; set; } = string.Empty;
    public string GenBusPostingGroup { get; set; } = string.Empty;
    public string VatBusPostingGroup { get; set; } = string.Empty;
    public string VendorPostingGroup { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    [Obsolete("Use AddressLine1 instead")]
    public string Address { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Lot> Lots { get; set; } = new List<Lot>();
    public ICollection<Settlement> Settlements { get; set; } = new List<Settlement>();
}
