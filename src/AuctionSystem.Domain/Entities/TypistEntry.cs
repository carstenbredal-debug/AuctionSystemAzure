namespace AuctionSystem.Domain.Entities;

public class TypistEntry
{
    public int Id { get; set; }
    public int LotNumber { get; set; }
    public int AuctionId { get; set; }
    public int BrokerId { get; set; }
    public Broker Broker { get; set; } = null!;
    public decimal PriceEur { get; set; }
    public int TypistUserId { get; set; }
    public AppUser TypistUser { get; set; } = null!;
    public int TypistSlot { get; set; } // 1 or 2 — which typist position
    public DateTime EnteredAt { get; set; } = DateTime.UtcNow;

    // Matching status
    public bool IsMatched { get; set; }
    public bool IsDisagreement { get; set; }
    public bool IsResolved { get; set; } // true when disagreement has been re-entered and resolved
    public int? MatchedWithEntryId { get; set; }
    public TypistEntry? MatchedWithEntry { get; set; }
    public int? AuctionResultId { get; set; } // set when matched and auction result is created
    public AuctionResult? AuctionResult { get; set; }
}
