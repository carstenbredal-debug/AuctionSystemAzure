namespace AuctionSystem.Domain.Entities;

public class Shipment
{
    public int Id { get; set; }
    public string ShipmentNumber { get; set; } = string.Empty;
    public int ShipperId { get; set; }
    public Shipper? Shipper { get; set; }
    public int BuyerId { get; set; }
    public Buyer? Buyer { get; set; }
    public int? ShippingAddressId { get; set; }
    public ShippingAddress? ShippingAddress { get; set; }
    public string TrackingNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string Notes { get; set; } = string.Empty;
    public string? PackingListPdfUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public ICollection<ShipmentLine> Lines { get; set; } = new List<ShipmentLine>();
}

public class ShipmentLine
{
    public int Id { get; set; }
    public int ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public int LotNumber { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public class PackingOrder
{
    public int Id { get; set; }
    public string PackingOrderNumber { get; set; } = string.Empty;
    public int ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }
    public string Status { get; set; } = "Ready to Pack"; // Ready to Pack, In Production, Packed
    public string Type { get; set; } = "ShowLot"; // ShowLot or Packing
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PackingOrderLine> Lines { get; set; } = new List<PackingOrderLine>();
}

public class PackingOrderLine
{
    public int Id { get; set; }
    public int PackingOrderId { get; set; }
    public PackingOrder? PackingOrder { get; set; }
    public int BoxNumber { get; set; }
    public int LotNumber { get; set; }
    public int Skins { get; set; }
    public string BoxType { get; set; } = "";
    public string Location { get; set; } = "";
    public decimal WeightKg { get; set; }
    public int? PackedBoxId { get; set; }
    public PackedBox? PackedBox { get; set; }
}

public class PackedBox
{
    public int Id { get; set; }
    public int PackingOrderId { get; set; }
    public PackingOrder? PackingOrder { get; set; }
    public string BoxNumber { get; set; } = "";
    public string BoxType { get; set; } = ""; // Big, Small
    public decimal GrossWeight { get; set; }
    public decimal NetWeight { get; set; }
    public decimal TareWeight { get; set; }
    public decimal Weight { get; set; }
    public decimal HeightM { get; set; }
    public decimal WidthM { get; set; }
    public decimal LengthM { get; set; }
    public string Status { get; set; } = "Open"; // Open, Closed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PackingOrderLine> ShowLots { get; set; } = new List<PackingOrderLine>();
}
