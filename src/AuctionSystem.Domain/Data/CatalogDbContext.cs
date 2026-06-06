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
    public DbSet<CatalogNumberRule> CatalogNumberRules => Set<CatalogNumberRule>();
    public DbSet<StringDefinition> StringDefinitions => Set<StringDefinition>();
    public DbSet<GeneratedLot> GeneratedLots => Set<GeneratedLot>();
    public DbSet<LotGenerationSkippedGroup> SkippedGroups => Set<LotGenerationSkippedGroup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogLot>(e =>
        {
            e.ToTable("cataloglots", "auction");
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
            e.ToTable("skintable", "auction");
            e.HasKey(s => s.UniqueID);
        });

        modelBuilder.Entity<LotGroupOrder>(e =>
        {
            e.ToTable("lotgrouporder", "auction");
            e.HasKey(g => g.ColumnName);
            e.Property(g => g.ColumnName).HasMaxLength(50);
        });

        modelBuilder.Entity<LotSizeRule>(e =>
        {
            e.ToTable("lotsizerule", "auction");
            e.HasKey(r => r.RuleID);
            e.Property(r => r.Gender).HasMaxLength(50);
            e.Property(r => r.Size).HasMaxLength(50);
        });

        modelBuilder.Entity<LotSortOrder>(e =>
        {
            e.ToTable("lotsortorder", "auction");
            e.HasKey(s => new { s.ColumnName, s.Value });
            e.Property(s => s.ColumnName).HasMaxLength(50);
            e.Property(s => s.Value).HasMaxLength(100);
        });

        modelBuilder.Entity<CatalogNumberRule>(e =>
        {
            e.ToTable("catalognumberrule", "auction");
            e.HasKey(r => r.CatalogNumberRuleID);
            e.Property(r => r.SalesType).HasMaxLength(50);
            e.Property(r => r.Gender).HasMaxLength(50);
            e.Property(r => r.Group).HasMaxLength(50);
        });

        modelBuilder.Entity<StringDefinition>(e =>
        {
            e.ToTable("stringdefinition", "auction");
            e.HasKey(s => s.StringDefinitionID);
            e.Property(s => s.ColumnName).HasMaxLength(50);
        });

        modelBuilder.Entity<GeneratedLot>(e =>
        {
            e.ToTable("lots", "auction");
            e.HasKey(l => l.LotID);
        });

        modelBuilder.Entity<LotGenerationSkippedGroup>(e =>
        {
            e.ToTable("lotgenerationskippedgroup", "auction");
            e.HasKey(s => s.SkippedGroupID);
        });
    }
}
