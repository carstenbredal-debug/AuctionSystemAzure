namespace AuctionSystem.Domain.Entities;

public class InvoiceLine
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Skins { get; set; }
    public decimal PricePerSkin { get; set; }
    public decimal HammerPrice { get; set; }

    // The auction fee actually CHARGED for this lot at invoice creation (negative on credit-note lines).
    // Credit notes credit this stored amount — never a recomputation — so a formula/parameter change can
    // never make a credit differ from its charge. Null on lines created before the column existed.
    public decimal? AuctionFee { get; set; }

    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    public int AuctionResultId { get; set; }
    public AuctionResult AuctionResult { get; set; } = null!;
}
