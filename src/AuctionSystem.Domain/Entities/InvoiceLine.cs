namespace AuctionSystem.Domain.Entities;

public class InvoiceLine
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Skins { get; set; }
    public decimal PricePerSkin { get; set; }
    public decimal HammerPrice { get; set; }

    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    public int AuctionResultId { get; set; }
    public AuctionResult AuctionResult { get; set; } = null!;
}
