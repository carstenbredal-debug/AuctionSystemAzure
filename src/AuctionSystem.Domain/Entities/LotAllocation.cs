using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class LotAllocation
{
    public int Id { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal PricePerUnit { get; set; }
    public decimal TotalPrice { get; set; }
    public AllocationStatus Status { get; set; } = AllocationStatus.Pending;
    public DateTime AllocatedAt { get; set; } = DateTime.UtcNow;

    public int LotId { get; set; }
    public Lot Lot { get; set; } = null!;

    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;

    public int BuyerId { get; set; }
    public Buyer Buyer { get; set; } = null!;
}
