namespace AuctionSystem.Domain.Entities;

// A frozen catalogue draft: a point-in-time copy of the matching auction.cataloglots rows, filtered by
// SalesType / Gender / Group (any may be blank = "all"). Stored so it survives catalogue regeneration.
public class CatalogDraft
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;   // auto: "Type - Gender - Group"
    public string? SalesType { get; set; }
    public string? Gender { get; set; }
    public string? Group { get; set; }
    public int LotCount { get; set; }
    public int SkinCount { get; set; }
    // Lifecycle: Draft (editable) -> Active (frozen skins/boxes/lots, usable for an auction) -> InAuction (consumed, locked).
    public string Status { get; set; } = "Draft";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<CatalogDraftLot> Lots { get; set; } = new();
}
