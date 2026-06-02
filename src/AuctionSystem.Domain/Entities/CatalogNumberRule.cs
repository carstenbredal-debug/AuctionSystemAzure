using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuctionSystem.Domain.Entities;

[Table("catalognumberrule", Schema = "dbo")]
public class CatalogNumberRule
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int CatalogNumberRuleID { get; set; }

    [MaxLength(50)]
    public string? SalesType { get; set; }

    [MaxLength(50)]
    public string? Gender { get; set; }

    [MaxLength(50)]
    public string? Group { get; set; }

    public int StartNumber { get; set; }

    public bool IsActive { get; set; }
}
