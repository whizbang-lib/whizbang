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
  [RequiresDynamicCode()]
  [RequiresUnreferencedCode()]
  public NotificationDbContext(DbContextOptions<NotificationDbContext> options) : base(options) { }
  // DbSet properties and OnModelCreating are auto-generated in partial class
}
