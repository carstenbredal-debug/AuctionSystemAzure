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

    public ICollection<Lot> Lots { get; set; } = new List<Lot>();
}
