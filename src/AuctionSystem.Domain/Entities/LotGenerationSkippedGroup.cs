using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuctionSystem.Domain.Entities;

[Table("lotgenerationskippedgroup", Schema = "auction")]
public class LotGenerationSkippedGroup
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int SkippedGroupID { get; set; }

    [MaxLength(100)]
    public string? RunID { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }

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

    public int BoxCount { get; set; }

    public int ShowlotCount { get; set; }

    public int TotalSkins { get; set; }

    public string? BoxNumbers { get; set; }
}
