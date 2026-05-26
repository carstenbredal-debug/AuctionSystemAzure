using AuctionSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Domain.Data;

public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }

    public DbSet<CatalogLot> CatalogLots => Set<CatalogLot>();

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
    }
}
