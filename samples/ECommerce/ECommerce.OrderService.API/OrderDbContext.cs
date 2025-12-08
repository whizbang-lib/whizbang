using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;

namespace ECommerce.OrderService.API;

/// <summary>
/// DbContext for OrderService.API - provides Inbox, Outbox, and EventStore via Whizbang EF Core driver.
/// [WhizbangDbContext] attribute triggers source generation for:
/// - EnsureWhizbangTablesCreatedAsync() extension method
/// - DbSet properties for Inbox, Outbox, EventStore
/// - Model configuration in OnModelCreating
/// </summary>
[WhizbangDbContext]
public partial class OrderDbContext : DbContext {
  [RequiresDynamicCode()]
  [RequiresUnreferencedCode()]
  public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }
  // DbSet properties and OnModelCreating are auto-generated in partial class
}
