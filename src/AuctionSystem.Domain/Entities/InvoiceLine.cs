namespace AuctionSystem.Domain.Entities;

public class InvoiceLine
{
    public int Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }

    public int InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    public int LotId { get; set; }
    public Lot Lot { get; set; } = null!;
}
