using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.Configuration;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test DbContext for WhizbangModelBuilderExtensions tests.
/// </summary>
public class WhizbangInfraDbContext : DbContext {
  public WhizbangInfraDbContext(DbContextOptions<WhizbangInfraDbContext> options) : base(options) { }

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.ConfigureWhizbangInfrastructure();
  }
}

/// <summary>
/// Tests for WhizbangModelBuilderExtensions (ConfigureWhizbangInfrastructure).
/// Verifies entity configuration for Inbox, Outbox, EventStore, ServiceInstance, and MessageDeduplication.
/// Target: 100% branch coverage.
/// </summary>
public class WhizbangModelBuilderExtensionsTests {
  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresInboxEntityAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<WhizbangInfraDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new WhizbangInfraDbContext(options);

    // Act
    var inboxEntityType = context.Model.FindEntityType(typeof(InboxRecord));

    // Assert
    await Assert.That(inboxEntityType).IsNotNull();
    await Assert.That(inboxEntityType!.GetTableName()).IsEqualTo("wh_inbox");
  }

  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresOutboxEntityAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<WhizbangInfraDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new WhizbangInfraDbContext(options);

    // Act
    var outboxEntityType = context.Model.FindEntityType(typeof(OutboxRecord));

    // Assert
    await Assert.That(outboxEntityType).IsNotNull();
    await Assert.That(outboxEntityType!.GetTableName()).IsEqualTo("wh_outbox");
  }

  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresEventStoreEntityAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<WhizbangInfraDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new WhizbangInfraDbContext(options);

    // Act
    var eventStoreEntityType = context.Model.FindEntityType(typeof(EventStoreRecord));

    // Assert
    await Assert.That(eventStoreEntityType).IsNotNull();
    await Assert.That(eventStoreEntityType!.GetTableName()).IsEqualTo("wh_events");
  }

  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresServiceInstanceEntityAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<WhizbangInfraDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new WhizbangInfraDbContext(options);

    // Act
    var serviceInstanceEntityType = context.Model.FindEntityType(typeof(ServiceInstanceRecord));

    // Assert
    await Assert.That(serviceInstanceEntityType).IsNotNull();
    await Assert.That(serviceInstanceEntityType!.GetTableName()).IsEqualTo("wh_service_instances");
  }

  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresMessageDeduplicationEntityAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<WhizbangInfraDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new WhizbangInfraDbContext(options);

    // Act
    var deduplicationEntityType = context.Model.FindEntityType(typeof(MessageDeduplicationRecord));

    // Assert
    await Assert.That(deduplicationEntityType).IsNotNull();
    await Assert.That(deduplicationEntityType!.GetTableName()).IsEqualTo("wh_message_deduplication");
  }
}
