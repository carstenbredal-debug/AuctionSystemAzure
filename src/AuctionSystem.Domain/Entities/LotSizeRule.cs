using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuctionSystem.Domain.Entities;

[Table("lotsizerule", Schema = "auction")]
public class LotSizeRule
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int RuleID { get; set; }

    [MaxLength(50)]
    public string? Gender { get; set; }

    [MaxLength(50)]
    public string? Size { get; set; }

    public int MaxBoxes { get; set; }
    public int MaxSkinsPerBox { get; set; }
    public int ShowlotSkins { get; set; }
    public int MaxLotSizeExclShowlot { get; set; }
    public int MaxLotSizeInclShowlot { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; }
}
