using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.Lenses;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Data.EFCore.Postgres.Generated;

namespace ECommerce.BFF.API;

/// <summary>
/// EF Core DbContext for BFF read models (perspectives).
/// Uses generated ConfigureWhizbang() extension method for automatic entity configuration.
/// Configures PerspectiveRow&lt;T&gt; entities + Inbox/Outbox/EventStore with JSONB columns.
/// DbSet properties are auto-generated from discovered perspective classes.
/// </summary>
[WhizbangDbContext]
public partial class BffDbContext : DbContext {
  public BffDbContext(DbContextOptions<BffDbContext> options) : base(options) { }

  // DbSet properties auto-generated in BffDbContext.Generated.g.cs
  // from discovered IPerspectiveOf<TEvent> implementations

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // Use generated ConfigureWhizbang() extension method
    // Automatically configures all PerspectiveRow<T> entities + Inbox/Outbox/EventStore
    // with JSONB columns (EF Core 10 ComplexProperty + Npgsql JSONB mapping)
    modelBuilder.ConfigureWhizbang();
  }
}
