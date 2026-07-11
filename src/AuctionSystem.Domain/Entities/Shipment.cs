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
    // Outgoing staging location (OUT-1..OUT-20) where the shipment's boxes are collected.
    // Occupied while the shipment is active; freed (derived) once Shipped/Delivered/Cancelled.
    public string? OutLocation { get; set; }
    public string? PackingListPdfUrl { get; set; }
    public string? ShippingInvoicePdfUrl { get; set; }
    // Uploaded certificate document (any file type), stored in blob storage.
    public string? CertUrl { get; set; }
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
    // Set when the scanner confirms the box was moved from storage to the shipment's OUT location.
    public DateTime? MovedToOutAt { get; set; }
}

// Physical box state that OUTLIVES shipments: where a box was staged (OUT location) and, for
// showlots, which carton it was packed into. Written by the scanner flows, re-applied when a new
// shipment claims the box (deleting a shipment must not undo physical work), cleared when the box
// actually ships.
public class BoxPhysicalState
{
    public int BoxNumber { get; set; }
    public string? OutLocation { get; set; }
    public DateTime? MovedAt { get; set; }
    public string? PackedBoxNumber { get; set; }
    public string? PackedBoxType { get; set; }
    public decimal? PackedGrossWeight { get; set; }
    public DateTime UpdatedAt { get; set; }
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
