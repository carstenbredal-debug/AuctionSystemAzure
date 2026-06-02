using AuctionSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Domain.Data;

public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<CatalogLot> CatalogLots => Set<CatalogLot>();
    public DbSet<Skin> Skins => Set<Skin>();
    public DbSet<LotGroupOrder> LotGroupOrders => Set<LotGroupOrder>();
    public DbSet<LotSizeRule> LotSizeRules => Set<LotSizeRule>();
    public DbSet<LotSortOrder> LotSortOrders => Set<LotSortOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogLot>(e =>
        {
            e.ToTable("cataloglots", "dbo");
            e.HasKey(c => c.CatalogLotID);
            e.Property(c => c.IsShow).HasMaxLength(10);
            e.Property(c => c.SalesType).HasMaxLength(50);
            e.Property(c => c.Gender).HasMaxLength(50);
            e.Property(c => c.Group).HasMaxLength(50);
            e.Property(c => c.HairLength).HasMaxLength(50);
            e.Property(c => c.Size).HasMaxLength(50);
            e.Property(c => c.Quality).HasMaxLength(50);
            e.Property(c => c.Color).HasMaxLength(50);
            e.Property(c => c.Clarity).HasMaxLength(50);
            e.Property(c => c.Damages).HasMaxLength(50);
        });

        modelBuilder.Entity<Skin>(e =>
        {
            e.ToTable("skintable", "dbo");
            e.HasKey(s => s.UniqueID);
        });

        modelBuilder.Entity<LotGroupOrder>(e =>
        {
            e.ToTable("lotgrouporder", "dbo");
            e.HasKey(g => g.ColumnName);
            e.Property(g => g.ColumnName).HasMaxLength(50);
        });

        modelBuilder.Entity<LotSizeRule>(e =>
        {
            e.ToTable("lotsizerule", "dbo");
            e.HasKey(r => r.RuleID);
            e.Property(r => r.Gender).HasMaxLength(50);
            e.Property(r => r.Size).HasMaxLength(50);
        });

        modelBuilder.Entity<LotSortOrder>(e =>
        {
            e.ToTable("lotsortorder", "dbo");
            e.HasKey(s => new { s.ColumnName, s.Value });
            e.Property(s => s.ColumnName).HasMaxLength(50);
            e.Property(s => s.Value).HasMaxLength(100);
        });
    }
}
