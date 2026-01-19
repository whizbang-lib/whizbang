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
#pragma warning disable IL2026 // EF Core uses reflection internally - AOT support experimental in EF10, stable in EF12
#pragma warning disable IL2046 // EF Core uses reflection internally - AOT support experimental in EF10, stable in EF12
#pragma warning disable IL3050 // EF Core uses dynamic code generation - AOT support experimental in EF10, stable in EF12
  public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }
#pragma warning restore IL3050
#pragma warning restore IL2046
#pragma warning restore IL2026
  // DbSet properties and OnModelCreating are auto-generated in partial class
}
