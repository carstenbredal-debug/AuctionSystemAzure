namespace AuctionSystem.Domain.Entities;

public class BrokerBuyer
{
    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;
    public int BuyerId { get; set; }
    public Buyer Buyer { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
