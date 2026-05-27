namespace AuctionSystem.Domain.Entities;

public class AuctionResult
{
    public int Id { get; set; }
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
}
