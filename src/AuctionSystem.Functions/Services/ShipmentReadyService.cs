using AuctionSystem.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Functions.Services;

// Decides when a shipment is fully packed and staged: every packing order Packed AND every storage
// box confirmed at the OUT location. Then the shipment becomes "Ready" and its shipping documents
// (packing list + shipping invoice) are regenerated so no stale pre-packing PDF survives.
// Called by the scanner flows (box-move confirm, showlot-pack close) whenever an order completes.
public class ShipmentReadyService
{
    private readonly AuctionDbContext _db;
    private readonly IServiceProvider _services;   // lazy — ShipmentFunctions also depends on this service
    private readonly ILogger<ShipmentReadyService> _logger;

    private static readonly string[] DoneStatuses = { "Ready", "Shipped", "Delivered", "Cancelled" };

    public ShipmentReadyService(AuctionDbContext db, IServiceProvider services, ILogger<ShipmentReadyService> logger)
    {
        _db = db;
        _services = services;
        _logger = logger;
    }

    // Returns true if the shipment was flipped to Ready (documents regenerated).
    public async Task<bool> TryCompleteAsync(int shipmentId)
    {
        var shipment = await _db.Shipments.FindAsync(shipmentId);
        if (shipment == null || DoneStatuses.Contains(shipment.Status))
            return false;

        var orders = await _db.PackingOrders
            .Include(p => p.Lines)
            .Where(p => p.ShipmentId == shipmentId)
            .ToListAsync();
        if (orders.Count == 0)
            return false;

        var allOrdersPacked = orders.All(o => o.Status == "Packed");
        var allStorageMoved = orders
            .Where(o => o.Type == "Packing")
            .SelectMany(o => o.Lines)
            .All(l => l.MovedToOutAt != null);
        if (!allOrdersPacked || !allStorageMoved)
            return false;

        shipment.Status = "Ready";
        await _db.SaveChangesAsync();
        _logger.LogInformation("Shipment {Number} is READY — all orders packed and staged at {Loc}; regenerating documents",
            shipment.ShipmentNumber, shipment.OutLocation);

        // Regenerate BOTH documents so the stored URLs reflect the final packed state. Failures are
        // logged but never break the scanner confirmation — the docs regenerate on demand anyway.
        var shipmentFunctions = _services.GetRequiredService<Functions.ShipmentFunctions>();
        try { await shipmentFunctions.GenerateAndStorePackingListPdfAsync(shipmentId, isShippingInvoice: false); }
        catch (Exception ex) { _logger.LogError(ex, "Packing list regeneration failed for shipment {Id}", shipmentId); }
        try { await shipmentFunctions.GenerateAndStorePackingListPdfAsync(shipmentId, isShippingInvoice: true); }
        catch (Exception ex) { _logger.LogError(ex, "Shipping invoice regeneration failed for shipment {Id}", shipmentId); }

        return true;
    }
}
