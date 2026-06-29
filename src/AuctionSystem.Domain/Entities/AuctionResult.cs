namespace AuctionSystem.Domain.Entities;

public class AuctionResult
{
    public int Id { get; set; }
    public int AuctionId { get; set; }
    public int LotNumber { get; set; }
    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;
    public decimal PriceEur { get; set; }

    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public string? Color { get; set; }
    public string? Quality { get; set; }
    public string? Size { get; set; }
    public string? Clarity { get; set; }
    public string? HairLength { get; set; }
    public int TotalSkins { get; set; }
    public int BoxCount { get; set; }

    public bool Processed { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    public int? SoldToBuyerId { get; set; }
    public Buyer? SoldToBuyer { get; set; }
    public DateTime? SoldAt { get; set; }

    public string? CommissionType { get; set; }
    public decimal? CommissionValue { get; set; }
    public decimal? CommissionAmount { get; set; }

    public string? LastModifiedBy { get; set; }
    public DateTime? LastModifiedAt { get; set; }

    // The external price feed's reference for this knock-down (clerk/clock system), null for typist-matched
    // results. Stored for traceability / reconciliation. Column added via the Program.cs startup raw-SQL block.
    public string? ExternalRef { get; set; }
}
