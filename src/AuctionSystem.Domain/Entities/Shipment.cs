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
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public int? BoxNumber { get; set; }
    public string Notes { get; set; } = string.Empty;
}
