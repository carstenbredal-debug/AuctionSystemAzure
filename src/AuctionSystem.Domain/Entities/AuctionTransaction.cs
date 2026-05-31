using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class AuctionTransaction
{
    public int Id { get; set; }
    public int AuctionId { get; set; }
    public Auction Auction { get; set; } = null!;
    public int LotNumber { get; set; }
    public TransactionType TransactionType { get; set; }
    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;
    public int? BuyerId { get; set; }
    public Buyer? Buyer { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string? DebitAccount { get; set; }
    public string? CreditAccount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? AuctionResultId { get; set; }
    public AuctionResult? AuctionResult { get; set; }
}
