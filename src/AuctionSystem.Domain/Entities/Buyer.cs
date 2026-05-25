namespace AuctionSystem.Domain.Entities;

public class Buyer
{
    public int Id { get; set; }
    public string BuyerNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int? BrokerId { get; set; }
    public Broker? Broker { get; set; }
    public string? BcCustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<LotAllocation> Allocations { get; set; } = new List<LotAllocation>();
}
