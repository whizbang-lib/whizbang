using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Whizbang.Data.EFCore.Custom;
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

  // DbSet properties and OnModelCreating are auto-generated in the partial class
  // The generator will configure Inbox, Outbox, EventStore, and ServiceInstance entities

  /// <summary>
  /// Extends the auto-generated model configuration.
  /// ServiceInstanceRecord is now auto-configured in ConfigureWhizbang() with PascalCase.
  /// Inbox/Outbox/Events are auto-generated with PascalCase (matches SQL function).
  /// </summary>
  partial void OnModelCreatingExtended(ModelBuilder modelBuilder) {
    // Value converter for MessageId: string (C#) to Guid (database)
    var stringToGuidConverter = new ValueConverter<string, Guid>(
      v => Guid.Parse(v),
      v => v.ToString());

    // Add MessageId converters for Outbox and Inbox (MessageId is string in C#, UUID in DB)
    modelBuilder.Entity<OutboxRecord>(entity => {
      entity.Property(e => e.MessageId).HasConversion(stringToGuidConverter);
    });

    modelBuilder.Entity<InboxRecord>(entity => {
      entity.Property(e => e.MessageId).HasConversion(stringToGuidConverter);
    });
  }
}
