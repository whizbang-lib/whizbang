using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;

namespace ECommerce.NotificationWorker;

/// <summary>
/// DbContext for NotificationWorker - provides Inbox, Outbox, and EventStore via Whizbang EF Core driver.
/// [WhizbangDbContext] attribute triggers source generation for:
/// - EnsureWhizbangTablesCreatedAsync() extension method
/// - DbSet properties for Inbox, Outbox, EventStore
/// - Model configuration in OnModelCreating
/// </summary>
[WhizbangDbContext]
public partial class NotificationDbContext : DbContext {
#pragma warning disable IL2026 // EF Core uses reflection internally - AOT support experimental in EF10, stable in EF12
#pragma warning disable IL2046 // EF Core uses reflection internally - AOT support experimental in EF10, stable in EF12
#pragma warning disable IL3050 // EF Core uses dynamic code generation - AOT support experimental in EF10, stable in EF12
  public NotificationDbContext(DbContextOptions<NotificationDbContext> options) : base(options) { }
#pragma warning restore IL3050
#pragma warning restore IL2046
#pragma warning restore IL2026
  // DbSet properties and OnModelCreating are auto-generated in partial class
}
