using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Domain.Services;

public class BidService
{
    private readonly AuctionDbContext _db;

    public BidService(AuctionDbContext db) => _db = db;

    public async Task<Bid?> PlaceBidAsync(int lotId, int brokerId, decimal amount)
    {
        var lot = await _db.Lots.Include(l => l.Auction).Include(l => l.Bids)
            .FirstOrDefaultAsync(l => l.Id == lotId);
        if (lot == null || lot.Auction.Status != AuctionStatus.Active)
            return null;

        foreach (var existingBid in lot.Bids.Where(b => b.Status == BidStatus.Active || b.Status == BidStatus.Winning))
            existingBid.Status = BidStatus.Outbid;

        var bid = new Bid
        {
            LotId = lotId,
            BrokerId = brokerId,
            Amount = amount,
            Status = BidStatus.Winning
        };
        _db.Bids.Add(bid);
        lot.Status = LotStatus.Active;
        await _db.SaveChangesAsync();
        return bid;
    }

    public async Task<List<Bid>> GetBidsByBrokerAsync(int brokerId)
        => await _db.Bids.Where(b => b.BrokerId == brokerId)
            .Include(b => b.Lot).ThenInclude(l => l.Auction)
            .OrderByDescending(b => b.PlacedAt).ToListAsync();

    public async Task<LotAllocation?> AllocateLotAsync(int lotId, int brokerId, int buyerId, int quantity, decimal pricePerUnit)
    {
        var lot = await _db.Lots.FirstOrDefaultAsync(l => l.Id == lotId && l.Status == LotStatus.Sold);
        if (lot == null) return null;

        var allocation = new LotAllocation
        {
            LotId = lotId,
            BrokerId = brokerId,
            BuyerId = buyerId,
            Quantity = quantity,
            PricePerUnit = pricePerUnit,
            TotalPrice = quantity * pricePerUnit,
            Status = AllocationStatus.Allocated
        };
        _db.LotAllocations.Add(allocation);
        await _db.SaveChangesAsync();
        return allocation;
    }

    public async Task<List<LotAllocation>> GetAllocationsByBrokerAsync(int brokerId)
        => await _db.LotAllocations.Where(a => a.BrokerId == brokerId)
            .Include(a => a.Lot).Include(a => a.Buyer)
            .OrderByDescending(a => a.AllocatedAt).ToListAsync();

    public async Task<List<LotAllocation>> GetAllocationsByBuyerAsync(int buyerId)
        => await _db.LotAllocations.Where(a => a.BuyerId == buyerId)
            .Include(a => a.Lot).ThenInclude(l => l.Auction)
            .Include(a => a.Broker)
            .OrderByDescending(a => a.AllocatedAt).ToListAsync();
}
