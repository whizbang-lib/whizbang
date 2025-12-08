using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;

namespace ECommerce.PaymentWorker;

/// <summary>
/// DbContext for PaymentWorker - provides Inbox, Outbox, and EventStore via Whizbang EF Core driver.
/// [WhizbangDbContext] attribute triggers source generation for:
/// - EnsureWhizbangTablesCreatedAsync() extension method
/// - DbSet properties for Inbox, Outbox, EventStore
/// - Model configuration in OnModelCreating
/// </summary>
[WhizbangDbContext]
public partial class PaymentDbContext : DbContext {
  public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options) { }
  // DbSet properties and OnModelCreating are auto-generated in partial class
}
