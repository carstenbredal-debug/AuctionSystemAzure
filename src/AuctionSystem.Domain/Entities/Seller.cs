namespace AuctionSystem.Domain.Entities;

public class Seller
{
    public int Id { get; set; }
    public string SellerNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? BcCustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Lot> Lots { get; set; } = new List<Lot>();
    public ICollection<Settlement> Settlements { get; set; } = new List<Settlement>();
}
