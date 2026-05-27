using AuctionSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Domain.Data;

public class AuctionDbContext : DbContext
{
    public AuctionDbContext(DbContextOptions<AuctionDbContext> options) : base(options) { }

    public DbSet<Auction> Auctions => Set<Auction>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<Seller> Sellers => Set<Seller>();
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("auction");

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
            e.HasOne(l => l.Seller).WithMany(s => s.Lots).HasForeignKey(l => l.SellerId);
            e.Property(l => l.StartingPrice).HasColumnType("decimal(18,2)");
            e.Property(l => l.ReservePrice).HasColumnType("decimal(18,2)");
            e.Property(l => l.HammerPrice).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<Seller>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.SellerNumber).IsUnique();
            e.Property(s => s.SellerNumber).HasMaxLength(50).IsRequired();
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
            e.HasOne(b => b.Broker).WithMany(br => br.Buyers).HasForeignKey(b => b.BrokerId).IsRequired(false);
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
            e.HasIndex(i => i.InvoiceNumber).IsUnique();
            e.HasOne(i => i.Broker).WithMany(b => b.Invoices).HasForeignKey(i => i.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.Auction).WithMany().HasForeignKey(i => i.AuctionId).OnDelete(DeleteBehavior.Restrict);
            e.Property(i => i.SubTotal).HasColumnType("decimal(18,2)");
            e.Property(i => i.Commission).HasColumnType("decimal(18,2)");
            e.Property(i => i.Tax).HasColumnType("decimal(18,2)");
            e.Property(i => i.TotalAmount).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<InvoiceLine>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasOne(l => l.Invoice).WithMany(i => i.Lines).HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Lot).WithMany().HasForeignKey(l => l.LotId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.UnitPrice).HasColumnType("decimal(18,2)");
            e.Property(l => l.LineTotal).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<Settlement>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.SettlementNumber).IsUnique();
            e.HasOne(s => s.Lot).WithOne(l => l.Settlement).HasForeignKey<Settlement>(s => s.LotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Seller).WithMany(se => se.Settlements).HasForeignKey(s => s.SellerId).OnDelete(DeleteBehavior.Restrict);
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
            e.HasOne(u => u.Seller).WithMany().HasForeignKey(u => u.SellerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(u => u.Buyer).WithMany().HasForeignKey(u => u.BuyerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BrokerCustomerRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.BrokerId, r.BuyerId }).IsUnique();
            e.HasOne(r => r.Broker).WithMany().HasForeignKey(r => r.BrokerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Buyer).WithMany().HasForeignKey(r => r.BuyerId).OnDelete(DeleteBehavior.Restrict);
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
            e.Property(r => r.PriceEur).HasColumnType("decimal(18,2)");
            e.Property(r => r.SalesType).HasMaxLength(50);
            e.Property(r => r.Gender).HasMaxLength(50);
            e.Property(r => r.Group).HasMaxLength(50);
            e.Property(r => r.Color).HasMaxLength(50);
            e.Property(r => r.Quality).HasMaxLength(50);
            e.Property(r => r.Size).HasMaxLength(50);
            e.Property(r => r.Clarity).HasMaxLength(50);
            e.Property(r => r.HairLength).HasMaxLength(50);
        });
    }
}
