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

    public async Task<List<Invoice>> GetInvoicesByBrokerAsync(int brokerId)
        => await _db.Invoices.Where(i => i.BrokerId == brokerId)
            .Include(i => i.Lines).Include(i => i.Buyer).Include(i => i.OriginalInvoice)
            .OrderByDescending(i => i.InvoiceDate)
            .Select(i => new Invoice
            {
                Id = i.Id, InvoiceNumber = i.InvoiceNumber, InvoiceDate = i.InvoiceDate,
                SubTotal = i.SubTotal, AuctionFee = i.AuctionFee, Commission = i.Commission,
                TotalAmount = i.TotalAmount, Currency = i.Currency, Status = i.Status,
                IsCreditNote = i.IsCreditNote, OriginalInvoiceId = i.OriginalInvoiceId,
                PdfUrl = i.PdfUrl, BrokerId = i.BrokerId, BuyerId = i.BuyerId,
                Buyer = i.Buyer, OriginalInvoice = i.OriginalInvoice,
                Lines = i.Lines
            })
            .ToListAsync();

    public async Task<List<Settlement>> GetSettlementsByFarmerAsync(int farmerId)
        => await _db.Settlements.Where(s => s.FarmerId == farmerId)
            .Include(s => s.Lot).ThenInclude(l => l.Auction)
            .OrderByDescending(s => s.CreatedAt).ToListAsync();

    public async Task<Invoice?> MarkInvoicePaidAsync(int invoiceId)
    {
        var invoice = await _db.Invoices.FindAsync(invoiceId);
        if (invoice == null) return null;

        invoice.Status = InvoiceStatus.Paid;
        await _db.SaveChangesAsync();
        return invoice;
    }

    public async Task<Invoice?> UpdateInvoiceStatusAsync(int invoiceId, InvoiceStatus status)
    {
        var invoice = await _db.Invoices.FindAsync(invoiceId);
        if (invoice == null) return null;

        invoice.Status = status;
        await _db.SaveChangesAsync();
        return invoice;
    }

    public async Task<Settlement?> CreateSettlementAsync(int lotId)
    {
        var lot = await _db.Lots.Include(l => l.Farmer)
            .FirstOrDefaultAsync(l => l.Id == lotId && l.Status == LotStatus.Sold && l.HammerPrice.HasValue);
        if (lot == null) return null;

        var existing = await _db.Settlements.FirstOrDefaultAsync(s => s.LotId == lotId);
        if (existing != null) return existing;

        var settlement = new Settlement
        {
            SettlementNumber = $"SET-{DateTime.UtcNow:yyyyMMdd}-{await _db.Settlements.CountAsync() + 1:D4}",
            LotId = lotId,
            FarmerId = lot.FarmerId ?? 0,
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
