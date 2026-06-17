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

    public ICollection<Lot> Lots { get; set; } = new List<Lot>();
}
