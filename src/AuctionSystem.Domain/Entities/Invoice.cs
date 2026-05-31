using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class Invoice
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; } = DateTime.UtcNow;
    public DateTime? PromptDate { get; set; }

    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;

    public int BuyerId { get; set; }
    public Buyer Buyer { get; set; } = null!;

    public decimal SubTotal { get; set; }
    public decimal AuctionFee { get; set; }
    public decimal Commission { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "EUR";

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsCreditNote { get; set; }
    public int? OriginalInvoiceId { get; set; }
    public Invoice? OriginalInvoice { get; set; }

    public byte[]? PdfData { get; set; }
    public string? PdfUrl { get; set; }

    public string? BcInvoiceNumber { get; set; }
    public Guid? BcInvoiceId { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}
