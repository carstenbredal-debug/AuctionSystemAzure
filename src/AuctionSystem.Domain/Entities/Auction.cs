using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Entities;

public class Auction
{
    public int Id { get; set; }
    public string AuctionNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Location { get; set; } = string.Empty;
    public DateTime ScheduledDate { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public AuctionStatus Status { get; set; } = AuctionStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Background snapshot-build status. The per-auction Lots/Boxes/Skins snapshot tables are built off
    // the request thread (a large import takes ~50s and would time out the HTTP request). Null = not
    // started; otherwise "Queued" / "Building" / "Done: N lots, N boxes, N skins" / "Failed: <reason>".
    // Columns added via the Program.cs startup raw-SQL block (this project patches schema there).
    public string? SnapshotStatus { get; set; }
    public DateTime? SnapshotBuiltAt { get; set; }

    // Background typist-simulator status (paced, runs over time). Null = idle; otherwise
    // "Queued" / "Typing N/total (matched M, disagreements D)" / "Done: …" / "Failed: …".
    public string? TypistSimStatus { get; set; }

    // Set by the Stop endpoint; the paced typist-sim worker checks it each lot and at the start of each
    // batch, halts without re-enqueuing, then clears it. Reset to false when a new sim is started.
    public bool TypistSimStopRequested { get; set; }

    // Background broker-robot simulator status (paced, runs over time like the typist). Null = idle;
    // otherwise "Queued" / "Pass N — sold S, invoices I, credits C, errors E" / "Stopped: …" / "Done: …".
    public string? BrokerSimStatus { get; set; }

    // Set by the broker Stop endpoint; the paced broker-sim worker checks it between passes, halts
    // without re-enqueuing, then clears it. Reset to false when a new broker sim is started.
    public bool BrokerSimStopRequested { get; set; }

    public ICollection<Lot> Lots { get; set; } = new List<Lot>();
}
