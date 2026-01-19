using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.Configuration;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for WhizbangModelBuilderExtensions (ConfigureWhizbangInfrastructure).
/// Verifies entity configuration for Inbox, Outbox, EventStore, ServiceInstance, and MessageDeduplication.
/// Uses PostgreSQL Testcontainers for real database testing with JsonDocument support.
/// Target: 100% branch coverage.
/// </summary>
public class WhizbangModelBuilderExtensionsTests : EFCoreTestBase {
  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresInboxEntityAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Act
    var inboxEntityType = context.Model.FindEntityType(typeof(InboxRecord));

    // Assert
    await Assert.That(inboxEntityType).IsNotNull();
    await Assert.That(inboxEntityType!.GetTableName()).IsEqualTo("wh_inbox");
  }

  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresOutboxEntityAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Act
    var outboxEntityType = context.Model.FindEntityType(typeof(OutboxRecord));

    // Assert
    await Assert.That(outboxEntityType).IsNotNull();
    await Assert.That(outboxEntityType!.GetTableName()).IsEqualTo("wh_outbox");
  }

  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresEventStoreEntityAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Act
    var eventStoreEntityType = context.Model.FindEntityType(typeof(EventStoreRecord));

    // Assert
    await Assert.That(eventStoreEntityType).IsNotNull();
    await Assert.That(eventStoreEntityType!.GetTableName()).IsEqualTo("wh_event_store");
  }

  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresServiceInstanceEntityAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Act
    var serviceInstanceEntityType = context.Model.FindEntityType(typeof(ServiceInstanceRecord));

    // Assert
    await Assert.That(serviceInstanceEntityType).IsNotNull();
    await Assert.That(serviceInstanceEntityType!.GetTableName()).IsEqualTo("wh_service_instances");
  }

  [Test]
  public async Task ConfigureWhizbangInfrastructure_ConfiguresMessageDeduplicationEntityAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Act
    var deduplicationEntityType = context.Model.FindEntityType(typeof(MessageDeduplicationRecord));

    // Assert
    await Assert.That(deduplicationEntityType).IsNotNull();
    await Assert.That(deduplicationEntityType!.GetTableName()).IsEqualTo("wh_message_deduplication");
  }
}
