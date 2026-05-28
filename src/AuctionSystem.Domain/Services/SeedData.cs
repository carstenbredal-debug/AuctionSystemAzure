using AuctionSystem.Domain.Data;
using AuctionSystem.Domain.Entities;
using AuctionSystem.Domain.Enums;

namespace AuctionSystem.Domain.Services;

public static class SeedData
{
    public static void Initialize(AuctionDbContext db)
    {
        if (db.Auctions.Any()) return;

        var sellers = new[]
        {
            new Seller { SellerNumber = "SEL-001", Name = "Nordic Fur Farm", ContactEmail = "info@nordicfur.dk", ContactPhone = "+45 1234 5678", Address = "Copenhagen, Denmark" },
            new Seller { SellerNumber = "SEL-002", Name = "Baltic Pelts Ltd", ContactEmail = "sales@balticpelts.lv", ContactPhone = "+371 2222 3333", Address = "Riga, Latvia" },
            new Seller { SellerNumber = "SEL-003", Name = "Finnish Quality Skins", ContactEmail = "contact@fqs.fi", ContactPhone = "+358 9 1234567", Address = "Helsinki, Finland" }
        };
        db.Sellers.AddRange(sellers);

        var brokers = new[]
        {
            new Broker { BrokerNumber = "BRK-001", CompanyName = "European Fur Trading", ContactPerson = "Hans Mueller", ContactEmail = "hans@eft.de", ContactPhone = "+49 30 1234567", Address = "Berlin, Germany" },
            new Broker { BrokerNumber = "BRK-002", CompanyName = "Asian Markets Group", ContactPerson = "Li Wei", ContactEmail = "wei@amg.cn", ContactPhone = "+86 10 8765432", Address = "Shanghai, China" },
            new Broker { BrokerNumber = "BRK-003", CompanyName = "Milano Luxury Furs", ContactPerson = "Marco Rossi", ContactEmail = "marco@mlf.it", ContactPhone = "+39 02 1234567", Address = "Milan, Italy" }
        };
        db.Brokers.AddRange(brokers);
        db.SaveChanges();

        var buyers = new[]
        {
            new Buyer { BuyerNumber = "BUY-001", Name = "Berlin Fashion House", ContactEmail = "buy@bfh.de", BrokerId = brokers[0].Id },
            new Buyer { BuyerNumber = "BUY-002", Name = "Hamburg Coat Makers", ContactEmail = "info@hcm.de", BrokerId = brokers[0].Id },
            new Buyer { BuyerNumber = "BUY-003", Name = "Shanghai Garments Co", ContactEmail = "orders@sgc.cn", BrokerId = brokers[1].Id },
            new Buyer { BuyerNumber = "BUY-004", Name = "Beijing Luxury Wear", ContactEmail = "buy@blw.cn", BrokerId = brokers[1].Id },
            new Buyer { BuyerNumber = "BUY-005", Name = "Rossi Fashion Milano", ContactEmail = "orders@rfm.it", BrokerId = brokers[2].Id }
        };
        db.Buyers.AddRange(buyers);

        var auction = new Auction
        {
            AuctionNumber = "AUC-20260601-0001",
            Title = "June 2026 Fur Auction",
            Description = "Premier fur auction featuring Nordic mink and fox pelts",
            Location = "Copenhagen Auction Hall",
            ScheduledDate = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            Status = AuctionStatus.Draft
        };
        db.Auctions.Add(auction);
        db.SaveChanges();

        var lots = new[]
        {
            new Lot { LotNumber = 1, Description = "Premium Dark Mink Pelts", Category = "Mink", Quantity = 500, Unit = "skins", StartingPrice = 25000m, ReservePrice = 30000m, AuctionId = auction.Id, SellerId = sellers[0].Id },
            new Lot { LotNumber = 2, Description = "Silver Fox Pelts - Grade A", Category = "Fox", Quantity = 200, Unit = "skins", StartingPrice = 40000m, ReservePrice = 45000m, AuctionId = auction.Id, SellerId = sellers[0].Id },
            new Lot { LotNumber = 3, Description = "Baltic Brown Mink Collection", Category = "Mink", Quantity = 350, Unit = "skins", StartingPrice = 18000m, ReservePrice = 22000m, AuctionId = auction.Id, SellerId = sellers[1].Id },
            new Lot { LotNumber = 4, Description = "Finnish Blue Fox - Premium", Category = "Fox", Quantity = 150, Unit = "skins", StartingPrice = 35000m, ReservePrice = 40000m, AuctionId = auction.Id, SellerId = sellers[2].Id },
            new Lot { LotNumber = 5, Description = "Mixed Mink Lot - Various Grades", Category = "Mink", Quantity = 800, Unit = "skins", StartingPrice = 15000m, AuctionId = auction.Id, SellerId = sellers[1].Id }
        };
        db.Lots.AddRange(lots);
        db.SaveChanges();
    }
}
