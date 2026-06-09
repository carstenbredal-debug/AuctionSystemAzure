using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuctionSystem.Domain.Entities;

[Table("SkinTable", Schema = "dbo")]
public class Skin
{
    [Key]
    public Guid UniqueID { get; set; }
    public int BoxNumber { get; set; }
    public long Barcode { get; set; }
    public string? DeliveryNote { get; set; }
    public string? Farmer { get; set; }
    public string? Farm { get; set; }
    public string? BoxType { get; set; }
    public string? BoxStatus { get; set; }
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public string? Damages { get; set; }
    public string? Size { get; set; }
    public string? HairLength { get; set; }
    public string? Color { get; set; }
    public string? Quality { get; set; }
    public string? Clarity { get; set; }
    public string? Auction { get; set; }
    public string? Location { get; set; }
    public DateTime? SourceLastModified { get; set; }
    public DateTime? FirstSeenAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastChangedAt { get; set; }
    public bool IsActive { get; set; }
}
