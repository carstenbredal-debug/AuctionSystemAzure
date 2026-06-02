using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuctionSystem.Domain.Entities;

[Table("lotgrouporder", Schema = "dbo")]
public class LotGroupOrder
{
    [Key]
    [Column("ColumnName")]
    [MaxLength(50)]
    public string ColumnName { get; set; } = string.Empty;

    public int GroupOrder { get; set; }
}
