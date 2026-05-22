using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Domain.Services;

public class SettlementService
{
    private readonly AuctionDbContext _db;
    private const decimal CommissionRate = 0.10m;
    private const decimal FeeRate = 0.02m;

    public SettlementService(AuctionDbContext db) => _db = db;

    public async Task<Invoice?> GenerateInvoiceAsync(int auctionId, int brokerId)
    {
        var wonBids = await _db.Bids
            .Where(b => b.BrokerId == brokerId && b.Status == BidStatus.Won && b.Lot.AuctionId == auctionId)
            .Include(b => b.Lot)
            .ToListAsync();

        if (!wonBids.Any()) return null;

        var invoice = new Invoice
        {
            InvoiceNumber = $"INV-{DateTime.UtcNow:yyyyMMdd}-{await _db.Invoices.CountAsync() + 1:D4}",
            BrokerId = brokerId,
            AuctionId = auctionId,
            IssuedDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(30),
            Status = InvoiceStatus.Issued
        };

        decimal subTotal = 0;
        foreach (var bid in wonBids)
        {
            var line = new InvoiceLine
            {
                Description = $"Lot #{bid.Lot.LotNumber}: {bid.Lot.Description}",
                Quantity = bid.Lot.Quantity,
                UnitPrice = bid.Amount,
                LineTotal = bid.Amount,
                LotId = bid.LotId
            };
            invoice.Lines.Add(line);
            subTotal += bid.Amount;
        }

        invoice.SubTotal = subTotal;
        invoice.Commission = subTotal * CommissionRate;
        invoice.Tax = (subTotal + invoice.Commission) * 0.25m;
        invoice.TotalAmount = subTotal + invoice.Commission + invoice.Tax;

        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync();
        return invoice;
    }

    public async Task<Settlement?> CreateSettlementAsync(int lotId)
    {
        var lot = await _db.Lots.Include(l => l.Seller)
            .FirstOrDefaultAsync(l => l.Id == lotId && l.Status == LotStatus.Sold && l.HammerPrice.HasValue);
        if (lot == null) return null;

        var existing = await _db.Settlements.FirstOrDefaultAsync(s => s.LotId == lotId);
        if (existing != null) return existing;

        var settlement = new Settlement
        {
            SettlementNumber = $"SET-{DateTime.UtcNow:yyyyMMdd}-{await _db.Settlements.CountAsync() + 1:D4}",
            LotId = lotId,
            SellerId = lot.SellerId,
            GrossAmount = lot.HammerPrice!.Value,
            Commission = lot.HammerPrice.Value * CommissionRate,
            Fees = lot.HammerPrice.Value * FeeRate,
            NetAmount = lot.HammerPrice.Value * (1 - CommissionRate - FeeRate),
            Status = SettlementStatus.Pending
        };

        _db.Settlements.Add(settlement);
        await _db.SaveChangesAsync();
        return settlement;
    }

    public async Task<List<Invoice>> GetInvoicesByBrokerAsync(int brokerId)
        => await _db.Invoices.Where(i => i.BrokerId == brokerId)
            .Include(i => i.Lines).Include(i => i.Auction)
            .OrderByDescending(i => i.IssuedDate).ToListAsync();

    public async Task<List<Settlement>> GetSettlementsBySellerAsync(int sellerId)
        => await _db.Settlements.Where(s => s.SellerId == sellerId)
            .Include(s => s.Lot).ThenInclude(l => l.Auction)
            .OrderByDescending(s => s.CreatedAt).ToListAsync();

    public async Task<Invoice?> MarkInvoicePaidAsync(int invoiceId)
    {
        var invoice = await _db.Invoices.FindAsync(invoiceId);
        if (invoice == null) return null;

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return invoice;
    }

    public async Task<Settlement?> MarkSettlementCompletedAsync(int settlementId)
    {
        var settlement = await _db.Settlements.FindAsync(settlementId);
        if (settlement == null) return null;

        settlement.Status = SettlementStatus.Completed;
        settlement.SettledDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return settlement;
    }
}
