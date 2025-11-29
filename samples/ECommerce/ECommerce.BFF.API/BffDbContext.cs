using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.Lenses;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace ECommerce.BFF.API;

/// <summary>
/// EF Core DbContext for BFF read models (perspectives).
/// Configures PerspectiveRow&lt;T&gt; entities for OrderReadModel, ProductDto, and InventoryLevelDto.
/// Uses owned types for InMemory provider compatibility (production will use JSONB columns).
/// </summary>
public class BffDbContext : DbContext {
  public BffDbContext(DbContextOptions<BffDbContext> options) : base(options) { }

  // DbSet properties for each perspective model
  public DbSet<PerspectiveRow<OrderReadModel>> OrderReadModels => Set<PerspectiveRow<OrderReadModel>>();
  public DbSet<PerspectiveRow<ProductDto>> ProductDtos => Set<PerspectiveRow<ProductDto>>();
  public DbSet<PerspectiveRow<InventoryLevelDto>> InventoryLevelDtos => Set<PerspectiveRow<InventoryLevelDto>>();

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // Configure PerspectiveRow<OrderReadModel>
    modelBuilder.Entity<PerspectiveRow<OrderReadModel>>(entity => {
      entity.HasKey(e => e.Id);
      entity.OwnsOne(e => e.Data, data => {
        data.OwnsMany(d => d.LineItems, lineItems => {
          // Shadow property as composite key part
          lineItems.WithOwner().HasForeignKey("OrderReadModelId");
          lineItems.Property<int>("Id");
          lineItems.HasKey("OrderReadModelId", "Id");
        });
      });
      entity.OwnsOne(e => e.Metadata);
      entity.OwnsOne(e => e.Scope);
    });

    // Configure PerspectiveRow<ProductDto>
    modelBuilder.Entity<PerspectiveRow<ProductDto>>(entity => {
      entity.HasKey(e => e.Id);
      entity.OwnsOne(e => e.Data);
      entity.OwnsOne(e => e.Metadata);
      entity.OwnsOne(e => e.Scope);
    });

    // Configure PerspectiveRow<InventoryLevelDto>
    modelBuilder.Entity<PerspectiveRow<InventoryLevelDto>>(entity => {
      entity.HasKey(e => e.Id);
      entity.OwnsOne(e => e.Data);
      entity.OwnsOne(e => e.Metadata);
      entity.OwnsOne(e => e.Scope);
    });
  }
}
