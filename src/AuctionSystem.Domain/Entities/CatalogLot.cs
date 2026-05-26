using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuctionSystem.Domain.Entities;

[Table("cataloglots", Schema = "dbo")]
public class CatalogLot
{
    [Key]
    public int CatalogLotID { get; set; }
    public Guid LotUniqueID { get; set; }
    public int StringNumber { get; set; }
    public int LotNumber { get; set; }
    public int CatalogSortOrder { get; set; }
    public string IsShow { get; set; } = string.Empty;
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public string? HairLength { get; set; }
    public string? Size { get; set; }
    public string? Quality { get; set; }
    public string? Color { get; set; }
    public string? Clarity { get; set; }
    public string? Damages { get; set; }
    public string? IncludedBoxNumbers { get; set; }
    public int BoxCount { get; set; }
    public int TotalSkins { get; set; }
}
