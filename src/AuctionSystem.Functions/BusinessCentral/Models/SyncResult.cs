namespace AuctionSystem.Functions.BusinessCentral.Models;

public class SyncResult
{
    public string Direction { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public int TotalProcessed { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
