using AuctionSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Domain.Data;

public class AuctionDbContext : DbContext
{
    public AuctionDbContext(DbContextOptions<AuctionDbContext> options) : base(options) { }

    public DbSet<Auction> Auctions => Set<Auction>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<Farmer> Farmers => Set<Farmer>();
    public DbSet<Broker> Brokers => Set<Broker>();
    public DbSet<Buyer> Buyers => Set<Buyer>();
    public DbSet<Bid> Bids => Set<Bid>();
    public DbSet<LotAllocation> LotAllocations => Set<LotAllocation>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<BrokerCustomerRequest> BrokerCustomerRequests => Set<BrokerCustomerRequest>();
    public DbSet<SystemParameter> SystemParameters => Set<SystemParameter>();
    public DbSet<AuctionResult> AuctionResults => Set<AuctionResult>();
    public DbSet<TakebackRequest> TakebackRequests => Set<TakebackRequest>();
    public DbSet<BrokerBuyer> BrokerBuyers => Set<BrokerBuyer>();
    public DbSet<TypistEntry> TypistEntries => Set<TypistEntry>();
    public DbSet<AuctionTransaction> AuctionTransactions => Set<AuctionTransaction>();
    public DbSet<LotSalesHistory> LotSalesHistories => Set<LotSalesHistory>();
    public DbSet<BoxTypeDimension> BoxTypeDimensions => Set<BoxTypeDimension>();
    public DbSet<ShippingAddress> ShippingAddresses => Set<ShippingAddress>();
    public DbSet<Shipper> Shippers => Set<Shipper>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentLine> ShipmentLines => Set<ShipmentLine>();
    public DbSet<PackingOrder> PackingOrders => Set<PackingOrder>();
    public DbSet<PackingOrderLine> PackingOrderLines => Set<PackingOrderLine>();
    public DbSet<PackedBox> PackedBoxes => Set<PackedBox>();
    public DbSet<BoxPhysicalState> BoxPhysicalStates => Set<BoxPhysicalState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("auction");

        modelBuilder.Entity<BoxPhysicalState>(e =>
        {
            e.ToTable("BoxPhysicalState");
            e.HasKey(b => b.BoxNumber);
            e.Property(b => b.BoxNumber).ValueGeneratedNever();
            e.Property(b => b.OutLocation).HasMaxLength(20);
            e.Property(b => b.PackedBoxNumber).HasMaxLength(50);
            e.Property(b => b.PackedBoxType).HasMaxLength(100);
            e.Property(b => b.PackedGrossWeight).HasColumnType("decimal(18,4)");
        });

        modelBuilder.Entity<Auction>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.AuctionNumber).IsUnique();
            e.Property(a => a.Title).HasMaxLength(200).IsRequired();
            e.Property(a => a.AuctionNumber).HasMaxLength(50).IsRequired();
        });

        modelBuilder.Entity<Lot>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasOne(l => l.Auction).WithMany(a => a.Lots).HasForeignKey(l => l.AuctionId);
            e.HasOne(l => l.Farmer).WithMany(s => s.Lots).HasForeignKey(l => l.FarmerId).IsRequired(false);
            e.Property(l => l.StartingPrice).HasColumnType("decimal(18,2)");
            e.Property(l => l.ReservePrice).HasColumnType("decimal(18,2)");
            e.Property(l => l.HammerPrice).HasColumnType("decimal(18,2)");
            e.HasIndex(l => l.Status);
            e.HasIndex(l => new { l.AuctionId, l.LotNumber });
        });

        modelBuilder.Entity<Farmer>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.FarmerNumber).IsUnique();
            e.Property(s => s.FarmerNumber).HasMaxLength(50).IsRequired();
            e.Property(s => s.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Broker>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasIndex(b => b.BrokerNumber).IsUnique();
            e.Property(b => b.BrokerNumber).HasMaxLength(50).IsRequired();
            e.Property(b => b.CompanyName).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Buyer>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasIndex(b => b.BuyerNumber).IsUnique();
            e.Property(b => b.BuyerNumber).HasMaxLength(50).IsRequired();
            e.HasOne(b => b.Broker).WithMany().HasForeignKey(b => b.BrokerId).IsRequired(false);
        });

        modelBuilder.Entity<BrokerBuyer>(e =>
        {
            e.HasKey(bb => new { bb.BrokerId, bb.BuyerId });
            e.HasOne(bb => bb.Broker).WithMany(b => b.BrokerBuyers).HasForeignKey(bb => bb.BrokerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(bb => bb.Buyer).WithMany(b => b.BrokerBuyers).HasForeignKey(bb => bb.BuyerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Bid>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasOne(b => b.Lot).WithMany(l => l.Bids).HasForeignKey(b => b.LotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.Broker).WithMany(br => br.Bids).HasForeignKey(b => b.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(b => b.Amount).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<LotAllocation>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasOne(a => a.Lot).WithMany(l => l.Allocations).HasForeignKey(a => a.LotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Broker).WithMany(b => b.Allocations).HasForeignKey(a => a.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Buyer).WithMany(b => b.Allocations).HasForeignKey(a => a.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(a => a.PricePerUnit).HasColumnType("decimal(18,2)");
            e.Property(a => a.TotalPrice).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.HasKey(i => i.Id);
            // Filtered: invoices/credit notes are created with InvoiceNumber = "" until BC assigns
            // the real number. A plain unique index allows only ONE empty row, so concurrent
            // invoicing collides. Enforce uniqueness only on real (non-empty) numbers.
            e.HasIndex(i => i.InvoiceNumber).IsUnique().HasFilter("[InvoiceNumber] <> ''");
            e.HasOne(i => i.Broker).WithMany(b => b.Invoices).HasForeignKey(i => i.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.Buyer).WithMany().HasForeignKey(i => i.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(i => i.BrokerId);
            e.HasIndex(i => i.BuyerId);
            e.Property(i => i.SubTotal).HasColumnType("decimal(18,2)");
            e.Property(i => i.AuctionFee).HasColumnType("decimal(18,2)");
            e.Property(i => i.Commission).HasColumnType("decimal(18,2)");
            e.Property(i => i.TotalAmount).HasColumnType("decimal(18,2)");
            e.Property(i => i.Currency).HasMaxLength(10);
            e.HasOne(i => i.OriginalInvoice).WithMany().HasForeignKey(i => i.OriginalInvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InvoiceLine>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasOne(l => l.Invoice).WithMany(i => i.Lines).HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.AuctionResult).WithMany().HasForeignKey(l => l.AuctionResultId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => l.AuctionResultId);
            e.Property(l => l.PricePerSkin).HasColumnType("decimal(18,2)");
            e.Property(l => l.HammerPrice).HasColumnType("decimal(18,2)");
            e.Property(l => l.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<Settlement>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.SettlementNumber).IsUnique();
            e.HasOne(s => s.Lot).WithOne(l => l.Settlement).HasForeignKey<Settlement>(s => s.LotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Farmer).WithMany(se => se.Settlements).HasForeignKey(s => s.FarmerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(s => s.GrossAmount).HasColumnType("decimal(18,2)");
            e.Property(s => s.Commission).HasColumnType("decimal(18,2)");
            e.Property(s => s.Fees).HasColumnType("decimal(18,2)");
            e.Property(s => s.NetAmount).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.AzureAdObjectId).IsUnique();
            e.HasIndex(u => u.Email);
            e.Property(u => u.AzureAdObjectId).HasMaxLength(100).IsRequired();
            e.Property(u => u.Email).HasMaxLength(200).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(u => u.Role).HasMaxLength(50).IsRequired();
            e.HasOne(u => u.Broker).WithMany().HasForeignKey(u => u.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(u => u.Farmer).WithMany().HasForeignKey(u => u.FarmerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(u => u.Buyer).WithMany().HasForeignKey(u => u.BuyerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BrokerCustomerRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.BrokerId, r.BuyerId }).IsUnique();
            e.HasOne(r => r.Broker).WithMany().HasForeignKey(r => r.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Buyer).WithMany().HasForeignKey(r => r.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(r => r.InitiatedBy).HasMaxLength(10).HasDefaultValue("Broker");
        });

        modelBuilder.Entity<SystemParameter>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Key).IsUnique();
            e.Property(p => p.Key).HasMaxLength(100).IsRequired();
            e.Property(p => p.Value).HasMaxLength(500).IsRequired();
            e.Property(p => p.Description).HasMaxLength(500);
            e.Property(p => p.DataType).HasMaxLength(50);
        });

        modelBuilder.Entity<AuctionResult>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasOne(r => r.Broker).WithMany().HasForeignKey(r => r.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.SoldToBuyer).WithMany().HasForeignKey(r => r.SoldToBuyerId).OnDelete(DeleteBehavior.Restrict);
            // Composite (owner + auction) indexes — the broker/buyer grids filter by both. These supersede
            // the old standalone BrokerId / SoldToBuyerId indexes (a composite on (Col, AuctionId) also
            // serves lookups on Col alone via the leftmost-prefix rule). Actually created via startup SQL
            // (Program.cs) since the EF migration snapshot is frozen; declared here for model accuracy.
            e.HasIndex(r => new { r.BrokerId, r.AuctionId });
            e.HasIndex(r => new { r.SoldToBuyerId, r.AuctionId });
            e.HasIndex(r => r.LotNumber);
            e.Property(r => r.PriceEur).HasColumnType("decimal(18,2)");
            e.Property(r => r.SalesType).HasMaxLength(50);
            e.Property(r => r.Gender).HasMaxLength(50);
            e.Property(r => r.Group).HasMaxLength(50);
            e.Property(r => r.Color).HasMaxLength(50);
            e.Property(r => r.Quality).HasMaxLength(50);
            e.Property(r => r.Size).HasMaxLength(50);
            e.Property(r => r.Clarity).HasMaxLength(50);
            e.Property(r => r.HairLength).HasMaxLength(50);
            e.Property(r => r.CommissionType).HasMaxLength(20);
            e.Property(r => r.CommissionValue).HasColumnType("decimal(18,4)");
            e.Property(r => r.CommissionAmount).HasColumnType("decimal(18,2)");
            e.Property(r => r.LastModifiedBy).HasMaxLength(10);
        });

        modelBuilder.Entity<LotSalesHistory>(e =>
        {
            e.HasKey(h => h.Id);
            e.HasOne(h => h.AuctionResult).WithMany().HasForeignKey(h => h.AuctionResultId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(h => h.Buyer).WithMany().HasForeignKey(h => h.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(h => h.Invoice).WithMany().HasForeignKey(h => h.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(h => h.LotNumber);
            e.HasIndex(h => h.AuctionResultId);
            e.Property(h => h.ActionType).HasMaxLength(50).IsRequired();
            e.Property(h => h.Initials).HasMaxLength(10);
            e.Property(h => h.BuyerName).HasMaxLength(200);
            e.Property(h => h.InvoiceNumber).HasMaxLength(100);
            e.Property(h => h.Amount).HasColumnType("decimal(18,2)");
            e.Property(h => h.Notes).HasMaxLength(500);
        });

        modelBuilder.Entity<TakebackRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasOne(r => r.AuctionResult).WithMany().HasForeignKey(r => r.AuctionResultId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Broker).WithMany().HasForeignKey(r => r.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Buyer).WithMany().HasForeignKey(r => r.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(r => r.InitiatedBy).HasMaxLength(10).HasDefaultValue("Broker");
        });

        modelBuilder.Entity<AuctionTransaction>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasOne(t => t.Auction).WithMany().HasForeignKey(t => t.AuctionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Broker).WithMany().HasForeignKey(t => t.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Buyer).WithMany().HasForeignKey(t => t.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.AuctionResult).WithMany().HasForeignKey(t => t.AuctionResultId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => new { t.AuctionId, t.LotNumber });
            e.HasIndex(t => t.TransactionType);
            e.HasIndex(t => t.BrokerId);
            e.Property(t => t.UnitPrice).HasColumnType("decimal(18,4)");
            e.Property(t => t.Amount).HasColumnType("decimal(18,2)");
            e.Property(t => t.Description).HasMaxLength(500);
            e.Property(t => t.DebitAccount).HasMaxLength(50);
            e.Property(t => t.CreditAccount).HasMaxLength(50);
        });

        modelBuilder.Entity<BoxTypeDimension>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.BoxType).IsUnique();
            e.Property(d => d.BoxType).HasMaxLength(100).IsRequired();
            e.Property(d => d.HeightM).HasColumnType("decimal(10,4)");
            e.Property(d => d.WidthM).HasColumnType("decimal(10,4)");
            e.Property(d => d.LengthM).HasColumnType("decimal(10,4)");
            e.Property(d => d.WeightKg).HasColumnType("decimal(10,4)");
        });

        modelBuilder.Entity<TypistEntry>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasOne(t => t.Broker).WithMany().HasForeignKey(t => t.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.TypistUser).WithMany().HasForeignKey(t => t.TypistUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.MatchedWithEntry).WithMany().HasForeignKey(t => t.MatchedWithEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.AuctionResult).WithMany().HasForeignKey(t => t.AuctionResultId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => new { t.LotNumber, t.TypistSlot });
            e.HasIndex(t => t.TypistUserId);
            e.Property(t => t.PriceEur).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<ShippingAddress>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).HasMaxLength(200);
            e.Property(s => s.ContactName).HasMaxLength(200);
            e.Property(s => s.AddressLine1).HasMaxLength(200);
            e.Property(s => s.AddressLine2).HasMaxLength(200);
            e.Property(s => s.Country).HasMaxLength(100);
            e.Property(s => s.PostalCode).HasMaxLength(20);
            e.Property(s => s.City).HasMaxLength(100);
            e.Property(s => s.ContactPhone).HasMaxLength(50);
            e.Property(s => s.MobilePhone).HasMaxLength(50);
            e.Property(s => s.ContactEmail).HasMaxLength(200);
        });

        modelBuilder.Entity<Shipper>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Code).IsUnique();
            e.Property(s => s.Name).HasMaxLength(200);
            e.Property(s => s.Code).HasMaxLength(50);
            e.Property(s => s.ContactName).HasMaxLength(200);
            e.Property(s => s.Phone).HasMaxLength(50);
            e.Property(s => s.Email).HasMaxLength(200);
            e.Property(s => s.AddressLine1).HasMaxLength(200);
            e.Property(s => s.AddressLine2).HasMaxLength(200);
            e.Property(s => s.City).HasMaxLength(100);
            e.Property(s => s.PostalCode).HasMaxLength(20);
            e.Property(s => s.Country).HasMaxLength(100);
            e.Property(s => s.Website).HasMaxLength(500);
            e.Property(s => s.TrackingUrlTemplate).HasMaxLength(500);
        });

        modelBuilder.Entity<Shipment>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.ShipmentNumber).IsUnique();
            e.Property(s => s.ShipmentNumber).HasMaxLength(50);
            e.Property(s => s.TrackingNumber).HasMaxLength(200);
            e.Property(s => s.Status).HasMaxLength(50);
            e.Property(s => s.Notes).HasMaxLength(1000);
            e.HasOne(s => s.Shipper).WithMany().HasForeignKey(s => s.ShipperId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Buyer).WithMany().HasForeignKey(s => s.BuyerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.ShippingAddress).WithMany().HasForeignKey(s => s.ShippingAddressId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ShipmentLine>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Notes).HasMaxLength(500);
            e.HasOne(l => l.Shipment).WithMany(s => s.Lines).HasForeignKey(l => l.ShipmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Invoice).WithMany().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.SetNull).IsRequired(false);
        });

        modelBuilder.Entity<PackingOrder>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.PackingOrderNumber).HasMaxLength(50);
            e.Property(p => p.Status).HasMaxLength(50);
            e.HasOne(p => p.Shipment).WithMany().HasForeignKey(p => p.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PackingOrderLine>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.BoxType).HasMaxLength(100);
            e.HasOne(l => l.PackingOrder).WithMany(p => p.Lines).HasForeignKey(l => l.PackingOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.PackedBox).WithMany(b => b.ShowLots).HasForeignKey(l => l.PackedBoxId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PackedBox>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.BoxType).HasMaxLength(100);
            e.Property(b => b.Status).HasMaxLength(50);
            e.Property(b => b.Weight).HasColumnType("decimal(18,4)");
            e.Property(b => b.HeightM).HasColumnType("decimal(18,4)");
            e.Property(b => b.WidthM).HasColumnType("decimal(18,4)");
            e.Property(b => b.LengthM).HasColumnType("decimal(18,4)");
            e.HasOne(b => b.PackingOrder).WithMany().HasForeignKey(b => b.PackingOrderId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
