namespace AuctionSystem.Domain.Entities;

public class LotSalesHistory
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public int AuctionResultId { get; set; }
    public AuctionResult AuctionResult { get; set; } = null!;

    public string ActionType { get; set; } = string.Empty; // "Sold", "Re-Invoice", "CreditNote"
    public string? Initials { get; set; }

    public int? BuyerId { get; set; }
    public Buyer? Buyer { get; set; }
    public string? BuyerName { get; set; }

    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public string? InvoiceNumber { get; set; }

    public decimal? Amount { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
