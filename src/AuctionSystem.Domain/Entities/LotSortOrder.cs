using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Domain.Entities;

[Table("lotsortorder", Schema = "auction")]
[PrimaryKey(nameof(ColumnName), nameof(Value))]
public class LotSortOrder
{
    [Column("ColumnName")]
    public string ColumnName { get; set; } = string.Empty;

    [Column("Value")]
    public string Value { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
