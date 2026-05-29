namespace AuctionSystem.Domain.Entities;

public class AppUser
{
    public int Id { get; set; }
    public string AzureAdObjectId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int? BrokerId { get; set; }
    public int? FarmerId { get; set; }
    public int? BuyerId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Broker? Broker { get; set; }
    public Farmer? Farmer { get; set; }
    public Buyer? Buyer { get; set; }
}
