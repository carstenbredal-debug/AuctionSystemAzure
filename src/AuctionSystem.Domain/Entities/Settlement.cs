using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class Settlement
{
    public int Id { get; set; }
    public string SettlementNumber { get; set; } = string.Empty;
    public decimal GrossAmount { get; set; }
    public decimal Commission { get; set; }
    public decimal Fees { get; set; }
    public decimal NetAmount { get; set; }
    public SettlementStatus Status { get; set; } = SettlementStatus.Pending;
    public DateTime? SettledDate { get; set; }
    public string? BcPaymentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int LotId { get; set; }
    public Lot Lot { get; set; } = null!;

    public int FarmerId { get; set; }
    public Farmer Farmer { get; set; } = null!;
}
