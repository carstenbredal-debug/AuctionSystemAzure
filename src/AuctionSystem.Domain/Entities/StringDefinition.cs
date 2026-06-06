using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuctionSystem.Domain.Entities;

[Table("stringdefinition", Schema = "auction")]
public class StringDefinition
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int StringDefinitionID { get; set; }

    [MaxLength(50)]
    public string ColumnName { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}
