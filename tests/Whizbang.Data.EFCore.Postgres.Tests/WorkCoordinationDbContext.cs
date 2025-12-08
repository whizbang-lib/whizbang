using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.EFCore.Postgres.Entities;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Minimal DbContext for testing work coordination (Inbox, Outbox, EventStore, ServiceInstances).
/// Uses WhizbangDbContext attribute to trigger source generation of core entity configurations.
/// No perspectives needed - just the core infrastructure entities.
/// </summary>
[WhizbangDbContext]
public partial class WorkCoordinationDbContext : DbContext {
  public WorkCoordinationDbContext(DbContextOptions<WorkCoordinationDbContext> options) : base(options) { }

  // DbSet properties for infrastructure entities not auto-generated
  // These are needed so EF Core creates the tables (otherwise only migrations create them)
  public DbSet<ServiceInstanceRecord> ServiceInstances => Set<ServiceInstanceRecord>();
  public DbSet<MessageDeduplicationRecord> MessageDeduplication => Set<MessageDeduplicationRecord>();
  public DbSet<InboxRecord> Inbox => Set<InboxRecord>();
  public DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();
  public DbSet<EventStoreRecord> Events => Set<EventStoreRecord>();

  // DbSet properties and OnModelCreating are auto-generated in the partial class
  // The generator will configure Inbox, Outbox, EventStore, and ServiceInstance entities

  /// <summary>
  /// Extends the auto-generated model configuration.
  /// Note: ConfigureWhizbangInfrastructure() is already called by the generated ConfigureWhizbang() method,
  /// so we don't need to call it here. This method is available for any additional custom configurations.
  /// </summary>
  partial void OnModelCreatingExtended(ModelBuilder modelBuilder) {
    // No additional configuration needed - ConfigureWhizbang() already handles infrastructure entities
  }
}
