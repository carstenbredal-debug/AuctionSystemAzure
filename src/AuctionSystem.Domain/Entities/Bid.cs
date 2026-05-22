using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class Bid
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public BidStatus Status { get; set; } = BidStatus.Active;
    public DateTime PlacedAt { get; set; } = DateTime.UtcNow;

    public int LotId { get; set; }
    public Lot Lot { get; set; } = null!;

    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;
}
