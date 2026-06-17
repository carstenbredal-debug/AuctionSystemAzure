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

    // Claim timestamp: set atomically before a BC push so two concurrent pushes of the same
    // invoice can't both create a document in BC. A stale claim (older than a few minutes) is
    // reclaimable so a crashed push can be retried.
    public DateTime? BcPushStartedAt { get; set; }

    // Last BC push failure for this document: the reason and when. Set when a push does not post;
    // cleared on a successful post. Lets the admin UI explain a stuck invoice without re-running the
    // sync. Column added via the startup raw-SQL block (this project patches post-baseline schema
    // there, not via EF migrations) — see Program.cs "Invoice BC columns".
    public string? BcSyncError { get; set; }
    public DateTime? BcSyncErrorAt { get; set; }

    public string? ShippingStatus { get; set; }
    public decimal? DownpaymentAmount { get; set; }
    public decimal? DownpaymentPercentage { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}
