namespace AuctionSystem.Domain.Entities;

public class SystemParameter
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DataType { get; set; } = "string";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
