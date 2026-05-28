using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class Lot
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int Quantity { get; set; } = 1;
    public string? Unit { get; set; }
    public decimal StartingPrice { get; set; }
    public decimal? ReservePrice { get; set; }
    public decimal? HammerPrice { get; set; }
    public LotStatus Status { get; set; } = LotStatus.Pending;

    public int AuctionId { get; set; }
    public Auction Auction { get; set; } = null!;

    public int? SellerId { get; set; }
    public Seller? Seller { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Bid> Bids { get; set; } = new List<Bid>();
    public ICollection<LotAllocation> Allocations { get; set; } = new List<LotAllocation>();
    public Settlement? Settlement { get; set; }
}
