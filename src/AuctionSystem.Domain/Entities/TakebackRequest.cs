using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class TakebackRequest
{
    public int Id { get; set; }
    public int AuctionResultId { get; set; }
    public AuctionResult AuctionResult { get; set; } = null!;
    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;
    public int BuyerId { get; set; }
    public Buyer Buyer { get; set; } = null!;
    public CustomerRequestStatus Status { get; set; } = CustomerRequestStatus.Pending;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAt { get; set; }
}
