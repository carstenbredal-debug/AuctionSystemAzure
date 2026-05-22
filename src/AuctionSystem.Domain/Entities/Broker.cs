namespace AuctionSystem.Domain.Entities;

public class Broker
{
    public int Id { get; set; }
    public string BrokerNumber { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? BcCustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Buyer> Buyers { get; set; } = new List<Buyer>();
    public ICollection<Bid> Bids { get; set; } = new List<Bid>();
    public ICollection<LotAllocation> Allocations { get; set; } = new List<LotAllocation>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}
