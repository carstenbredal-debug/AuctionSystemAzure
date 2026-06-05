namespace AuctionSystem.Domain.Entities;

public class BoxTypeDimension
{
    public int Id { get; set; }
    public string BoxType { get; set; } = string.Empty;
    public decimal HeightM { get; set; }
    public decimal WidthM { get; set; }
    public decimal LengthM { get; set; }
    public decimal WeightKg { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
