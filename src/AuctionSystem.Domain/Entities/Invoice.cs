using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class Invoice
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal SubTotal { get; set; }
    public decimal Commission { get; set; }
    public decimal Tax { get; set; }
    public decimal TotalAmount { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public DateTime IssuedDate { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public string? BcInvoiceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;

    public int AuctionId { get; set; }
    public Auction Auction { get; set; } = null!;

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}
