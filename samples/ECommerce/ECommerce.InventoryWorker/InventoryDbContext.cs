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
[WhizbangDbContext(Schema = "inventory")]
public partial class InventoryDbContext : DbContext {
#pragma warning disable IL2026 // EF Core uses reflection internally - AOT support experimental in EF10, stable in EF12
#pragma warning disable IL2046 // EF Core uses reflection internally - AOT support experimental in EF10, stable in EF12
#pragma warning disable IL3050 // EF Core uses dynamic code generation - AOT support experimental in EF10, stable in EF12
  public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }
#pragma warning restore IL3050
#pragma warning restore IL2046
#pragma warning restore IL2026

  // DbSet properties and OnModelCreating are auto-generated in partial class
  // The generator will configure Inbox, Outbox, and EventStore entities
}
