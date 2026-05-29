using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Domain.Services;

public class AuctionService
{
    private readonly AuctionDbContext _db;

    public AuctionService(AuctionDbContext db) => _db = db;

    public async Task<List<object>> GetAllAuctionsAsync()
        => await _db.Auctions.OrderByDescending(a => a.ScheduledDate)
            .Select(a => (object)new
            {
                a.Id, a.AuctionNumber, a.Title, a.Description, a.Location,
                a.ScheduledDate, a.StartedAt, a.CompletedAt, a.Status, a.CreatedAt,
                LotCount = a.Lots.Count
            }).ToListAsync();

    public async Task<Auction?> GetAuctionAsync(int id)
        => await _db.Auctions.FirstOrDefaultAsync(a => a.Id == id);

    public async Task<Auction> CreateAuctionAsync(Auction auction)
    {
        auction.AuctionNumber = $"AUC-{DateTime.UtcNow:yyyyMMdd}-{await _db.Auctions.CountAsync() + 1:D4}";
        _db.Auctions.Add(auction);
        await _db.SaveChangesAsync();
        return auction;
    }

    public async Task<Auction?> UpdateStatusAsync(int id, AuctionStatus status)
    {
        var auction = await _db.Auctions.FindAsync(id);
        if (auction == null) return null;

        auction.Status = status;
        if (status == AuctionStatus.Active) auction.StartedAt = DateTime.UtcNow;
        if (status == AuctionStatus.Closed) auction.CompletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return auction;
    }

    public async Task<Lot> AddLotAsync(int auctionId, Lot lot)
    {
        lot.AuctionId = auctionId;
        var maxLotNumber = await _db.Lots.Where(l => l.AuctionId == auctionId)
            .MaxAsync(l => (int?)l.LotNumber) ?? 0;
        lot.LotNumber = maxLotNumber + 1;
        _db.Lots.Add(lot);
        await _db.SaveChangesAsync();
        return lot;
    }

    public async Task<List<Lot>> GetLotsByAuctionAsync(int auctionId)
        => await _db.Lots.Where(l => l.AuctionId == auctionId)
            .OrderBy(l => l.LotNumber).ToListAsync();

    public async Task<Lot?> GetLotAsync(int id)
        => await _db.Lots.Include(l => l.Farmer).Include(l => l.Bids).ThenInclude(b => b.Broker)
            .Include(l => l.Allocations).ThenInclude(a => a.Buyer)
            .Include(l => l.Settlement)
            .FirstOrDefaultAsync(l => l.Id == id);

    public async Task<Lot?> RecordHammerPriceAsync(int lotId, decimal hammerPrice, int winningBrokerId)
    {
        var lot = await _db.Lots.Include(l => l.Bids).FirstOrDefaultAsync(l => l.Id == lotId);
        if (lot == null) return null;

        lot.HammerPrice = hammerPrice;
        lot.Status = LotStatus.Sold;

        foreach (var bid in lot.Bids)
            bid.Status = bid.BrokerId == winningBrokerId ? BidStatus.Won : BidStatus.Outbid;

        var winningBid = lot.Bids.FirstOrDefault(b => b.BrokerId == winningBrokerId);
        if (winningBid == null)
        {
            var newBid = new Bid
            {
                LotId = lotId,
                BrokerId = winningBrokerId,
                Amount = hammerPrice,
                Status = BidStatus.Won
            };
            _db.Bids.Add(newBid);
        }

        await _db.SaveChangesAsync();
        return lot;
    }

    public async Task<List<Lot>> GetLotsByFarmerAsync(int farmerId)
        => await _db.Lots.Where(l => l.FarmerId == farmerId)
            .Include(l => l.Auction).Include(l => l.Bids)
            .Include(l => l.Settlement)
            .OrderByDescending(l => l.Auction.ScheduledDate)
            .ToListAsync();
}
