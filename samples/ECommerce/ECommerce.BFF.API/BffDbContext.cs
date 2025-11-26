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
  [RequiresUnreferencedCode()]
  [RequiresDynamicCode()]
  public BffDbContext(DbContextOptions<BffDbContext> options) : base(options) { }

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // Configure PerspectiveRow<OrderReadModel>
    ConfigurePerspectiveRow<OrderReadModel>(modelBuilder);

    // Configure PerspectiveRow<ProductDto>
    ConfigurePerspectiveRow<ProductDto>(modelBuilder);

    // Configure PerspectiveRow<InventoryLevelDto>
    ConfigurePerspectiveRow<InventoryLevelDto>(modelBuilder);
  }

  private static void ConfigurePerspectiveRow<TModel>(ModelBuilder modelBuilder)
      where TModel : class {
    modelBuilder.Entity<PerspectiveRow<TModel>>(entity => {
      entity.HasKey(e => e.Id);

      // Use owned types for InMemory provider (InMemory doesn't support JSON queries)
      // Production PostgreSQL implementation will use .ToJson() instead
      entity.OwnsOne(e => e.Data, data => {
        data.WithOwner();
      });

      entity.OwnsOne(e => e.Metadata, metadata => {
        metadata.WithOwner();
        metadata.Property(m => m.EventType).IsRequired();
        metadata.Property(m => m.EventId).IsRequired();
        metadata.Property(m => m.Timestamp).IsRequired();
      });

      entity.OwnsOne(e => e.Scope, scope => {
        scope.WithOwner();
      });
    });
  }
}
