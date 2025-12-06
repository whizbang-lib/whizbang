using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;

namespace ECommerce.InventoryWorker;

/// <summary>
/// EF Core DbContext for InventoryWorker infrastructure (Inbox/Outbox/EventStore).
/// This service doesn't have perspectives, so we use the [WhizbangDbContext] attribute
/// without any perspective models. The source generator will still configure the
/// core Inbox/Outbox/EventStore entities.
/// AOT-compatible: All configuration is done via source generators, no reflection needed.
/// </summary>
[WhizbangDbContext]
public partial class InventoryDbContext : DbContext {
  [RequiresDynamicCode()]
  [RequiresUnreferencedCode()]
  public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

  // DbSet properties and OnModelCreating are auto-generated in partial class
  // The generator will configure Inbox, Outbox, and EventStore entities
}
