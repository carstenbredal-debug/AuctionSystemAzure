using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuctionSystem.Domain.Entities;

[Table("GeneratedLots", Schema = "auction")]
public class GeneratedLot
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int LotID { get; set; }

    [MaxLength(100)]
    public string UniqueID { get; set; } = string.Empty;

    [MaxLength(10)]
    public string IsShow { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? ShowlotBoxNumber { get; set; }

    [MaxLength(50)]
    public string? SalesType { get; set; }

    [MaxLength(50)]
    public string? Group { get; set; }

    [MaxLength(50)]
    public string? Gender { get; set; }

    [MaxLength(50)]
    public string? Size { get; set; }

    [MaxLength(50)]
    public string? Color { get; set; }

    [MaxLength(50)]
    public string? Quality { get; set; }

    [MaxLength(50)]
    public string? Clarity { get; set; }

    [MaxLength(50)]
    public string? HairLength { get; set; }

    [MaxLength(50)]
    public string? Damages { get; set; }

    public string? IncludedBoxNumbers { get; set; }

    public int BoxCount { get; set; }

    public int TotalSkins { get; set; }
}
