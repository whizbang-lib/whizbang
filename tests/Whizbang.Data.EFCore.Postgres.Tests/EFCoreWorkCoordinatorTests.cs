using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.Entities;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for EFCoreWorkCoordinator.
/// Tests the process_work_batch PostgreSQL function and lease-based work coordination.
/// Uses UUIDv7 for all message IDs to ensure proper time-ordered database indexing.
/// </summary>
public class EFCoreWorkCoordinatorTests : EFCoreTestBase {
  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _sut = null!;
  private Guid _instanceId;
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

  [Before(Test)]
  public async Task TestSetupAsync() {
    _instanceId = _idProvider.NewGuid();
    var dbContext = CreateDbContext();
    _sut = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext);
    await Task.CompletedTask;
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [],
      [],
      [],
      leaseSeconds: 300);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(0);
    await Assert.That(result.InboxWork).HasCount().EqualTo(0);

    // Verify heartbeat was updated
    var heartbeat = await GetInstanceHeartbeatAsync(_instanceId);
    await Assert.That(heartbeat).IsNotNull();
    await Assert.That(heartbeat!.Value > DateTimeOffset.UtcNow.AddSeconds(-5)).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatchAsync_WithMetadata_StoresMetadataCorrectlyAsync() {
    // Arrange
    var metadata = new Dictionary<string, object> {
      { "version", "1.0.0" },
      { "environment", "test" },
      { "enabled", true }
    };

    // Act
    await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata,
      [],
      [],
      [],
      [],
      leaseSeconds: 300);

    // Assert
    var storedMetadata = await GetInstanceMetadataAsync(_instanceId);
    await Assert.That(storedMetadata).IsNotNull();
    await Assert.That(storedMetadata).Contains("version");
    await Assert.That(storedMetadata).Contains("1.0.0");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(messageId1, "topic1", "TestEvent", "{}", status: "Publishing", instanceId: _instanceId);
    await InsertOutboxMessageAsync(messageId2, "topic2", "TestEvent", "{}", status: "Publishing", instanceId: _instanceId);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [messageId1, messageId2],
      [],
      [],
      []);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(0);

    // Verify messages marked as Published
    var status1 = await GetOutboxStatusAsync(messageId1);
    var status2 = await GetOutboxStatusAsync(messageId2);
    await Assert.That(status1).IsEqualTo("Published");
    await Assert.That(status2).IsEqualTo("Published");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(messageId, "topic1", "TestEvent", "{}", status: "Publishing", instanceId: _instanceId);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [new FailedMessage { MessageId = messageId, Error = "Network timeout" }],
      [],
      []);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(0);

    // Verify message marked as Failed
    var status = await GetOutboxStatusAsync(messageId);
    await Assert.That(status).IsEqualTo("Failed");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_FailedMessageWithSpecialCharacters_EscapesJsonCorrectlyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(messageId, "topic1", "TestEvent", "{}", status: "Publishing", instanceId: _instanceId);

    // Error message with special characters that need JSON escaping
    var errorMessage = "Error with \"quotes\", \nnewlines\n, and \\backslashes\\";

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [new FailedMessage { MessageId = messageId, Error = errorMessage }],
      [],
      []);

    // Assert - Should not throw, and status should be Failed
    await Assert.That(result.OutboxWork).HasCount().EqualTo(0);
    var status = await GetOutboxStatusAsync(messageId);
    await Assert.That(status).IsEqualTo("Failed");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();

    await InsertInboxMessageAsync(messageId1, "Handler1", "TestEvent", "{}", status: "Processing", instanceId: _instanceId);
    await InsertInboxMessageAsync(messageId2, "Handler2", "TestEvent", "{}", status: "Processing", instanceId: _instanceId);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [],
      [messageId1, messageId2],
      []);

    // Assert
    await Assert.That(result.InboxWork).HasCount().EqualTo(0);

    // Verify messages marked as Completed
    var status1 = await GetInboxStatusAsync(messageId1);
    var status2 = await GetInboxStatusAsync(messageId2);
    await Assert.That(status1).IsEqualTo("Completed");
    await Assert.That(status2).IsEqualTo("Completed");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertInboxMessageAsync(messageId, "Handler1", "TestEvent", "{}", status: "Processing", instanceId: _instanceId);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [],
      [],
      [new FailedMessage { MessageId = messageId, Error = "Handler exception" }]);

    // Assert
    await Assert.That(result.InboxWork).HasCount().EqualTo(0);

    // Verify message marked as Failed
    var status = await GetInboxStatusAsync(messageId);
    await Assert.That(status).IsEqualTo("Failed");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var orphanedId1 = _idProvider.NewGuid();
    var orphanedId2 = _idProvider.NewGuid();
    var activeId = _idProvider.NewGuid();

    // Orphaned messages (expired leases)
    await InsertOutboxMessageAsync(
      orphanedId1,
      "topic1",
      "OrphanedEvent1",
      "{\"data\":1}",
      status: "Publishing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10));

    await InsertOutboxMessageAsync(
      orphanedId2,
      "topic2",
      "OrphanedEvent2",
      "{\"data\":2}",
      status: "Publishing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-5));

    // Active message (not expired)
    await InsertOutboxMessageAsync(
      activeId,
      "topic3",
      "ActiveEvent",
      "{\"data\":3}",
      status: "Publishing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5));

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [],
      [],
      []);

    // Assert - Should return 2 work items, not the active one
    await Assert.That(result.OutboxWork).HasCount().EqualTo(2);
    await Assert.That(result.InboxWork).HasCount().EqualTo(0);

    var work1 = result.OutboxWork.First(m => m.MessageId == orphanedId1);
    var work2 = result.OutboxWork.First(m => m.MessageId == orphanedId2);

    await Assert.That(work1.Destination).IsEqualTo("topic1");
    await Assert.That(work1.MessageType).IsEqualTo("OrphanedEvent1");
    await Assert.That(work2.Destination).IsEqualTo("topic2");
    await Assert.That(work2.MessageType).IsEqualTo("OrphanedEvent2");

    // Verify orphaned messages now have new lease
    var newInstanceId = await GetOutboxInstanceIdAsync(orphanedId1);
    var newLeaseExpiry = await GetOutboxLeaseExpiryAsync(orphanedId1);
    await Assert.That(newInstanceId).IsEqualTo(_instanceId);
    await Assert.That(newLeaseExpiry).IsNotNull();
    await Assert.That(newLeaseExpiry!.Value > DateTimeOffset.UtcNow.AddMinutes(4)).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var orphanedId1 = _idProvider.NewGuid();
    var orphanedId2 = _idProvider.NewGuid();

    // Orphaned messages (expired leases)
    await InsertInboxMessageAsync(
      orphanedId1,
      "Handler1",
      "OrphanedEvent1",
      "{\"data\":1}",
      status: "Processing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10));

    await InsertInboxMessageAsync(
      orphanedId2,
      "Handler2",
      "OrphanedEvent2",
      "{\"data\":2}",
      status: "Processing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-5));

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [],
      [],
      []);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(0);
    await Assert.That(result.InboxWork).HasCount().EqualTo(2);

    var work1 = result.InboxWork.First(m => m.MessageId == orphanedId1);
    var work2 = result.InboxWork.First(m => m.MessageId == orphanedId2);

    await Assert.That(work1.MessageType).IsEqualTo("OrphanedEvent1");
    await Assert.That(work2.MessageType).IsEqualTo("OrphanedEvent2");

    // Verify orphaned messages now have new lease
    var newInstanceId = await GetInboxInstanceIdAsync(orphanedId1);
    await Assert.That(newInstanceId).IsEqualTo(_instanceId);
  }

  [Test]
  public async Task ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    // Completed messages
    var completedOutboxId = _idProvider.NewGuid();
    var completedInboxId = _idProvider.NewGuid();
    await InsertOutboxMessageAsync(completedOutboxId, "topic1", "Event1", "{}", status: "Publishing", instanceId: _instanceId);
    await InsertInboxMessageAsync(completedInboxId, "Handler1", "Event2", "{}", status: "Processing", instanceId: _instanceId);

    // Failed messages
    var failedOutboxId = _idProvider.NewGuid();
    var failedInboxId = _idProvider.NewGuid();
    await InsertOutboxMessageAsync(failedOutboxId, "topic2", "Event3", "{}", status: "Publishing", instanceId: _instanceId);
    await InsertInboxMessageAsync(failedInboxId, "Handler2", "Event4", "{}", status: "Processing", instanceId: _instanceId);

    // Orphaned messages
    var orphanedOutboxId = _idProvider.NewGuid();
    var orphanedInboxId = _idProvider.NewGuid();
    await InsertOutboxMessageAsync(
      orphanedOutboxId,
      "topic3",
      "OrphanedEvent1",
      "{}",
      status: "Publishing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10));
    await InsertInboxMessageAsync(
      orphanedInboxId,
      "Handler3",
      "OrphanedEvent2",
      "{}",
      status: "Processing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10));

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [completedOutboxId],
      [new FailedMessage { MessageId = failedOutboxId, Error = "Outbox error" }],
      [completedInboxId],
      [new FailedMessage { MessageId = failedInboxId, Error = "Inbox error" }]);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(1);
    await Assert.That(result.InboxWork).HasCount().EqualTo(1);

    // Verify completed
    await Assert.That(await GetOutboxStatusAsync(completedOutboxId)).IsEqualTo("Published");
    await Assert.That(await GetInboxStatusAsync(completedInboxId)).IsEqualTo("Completed");

    // Verify failed
    await Assert.That(await GetOutboxStatusAsync(failedOutboxId)).IsEqualTo("Failed");
    await Assert.That(await GetInboxStatusAsync(failedInboxId)).IsEqualTo("Failed");

    // Verify work returned and claimed
    await Assert.That(result.OutboxWork[0].MessageId).IsEqualTo(orphanedOutboxId);
    await Assert.That(result.InboxWork[0].MessageId).IsEqualTo(orphanedInboxId);
    await Assert.That(await GetOutboxInstanceIdAsync(orphanedOutboxId)).IsEqualTo(_instanceId);
    await Assert.That(await GetInboxInstanceIdAsync(orphanedInboxId)).IsEqualTo(_instanceId);
  }

  [Test]
  public async Task ProcessWorkBatchAsync_ReturnedWork_HasCorrectPascalCaseColumnMappingAsync() {
    // Arrange - This test specifically validates the PascalCase column name mapping fix
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(
      messageId,
      "test-topic",
      "TestEventType",
      "{\"key\":\"value\"}",
      status: "Pending");

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [],
      [],
      []);

    // Assert - All fields should be populated correctly (not null/default)
    await Assert.That(result.OutboxWork).HasCount().EqualTo(1);
    var work = result.OutboxWork[0];

    await Assert.That(work.MessageId).IsEqualTo(messageId);
    await Assert.That(work.Destination).IsEqualTo("test-topic");
    await Assert.That(work.MessageType).IsEqualTo("TestEventType");
    // PostgreSQL JSONB normalizes JSON by adding spaces after colons
    await Assert.That(work.MessageData).IsEqualTo("{\"key\": \"value\"}");
    await Assert.That(work.Metadata).IsNotNull();
    await Assert.That(work.Attempts).IsGreaterThan(0);  // Should be 1 after claiming
  }

  [Test]
  public async Task ProcessWorkBatchAsync_JsonbColumns_ReturnAsTextCorrectlyAsync() {
    // Arrange - This test validates JSONB→TEXT casting works correctly
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var complexJson = "{\"nested\":{\"key\":\"value\"},\"array\":[1,2,3]}";
    var complexMetadata = "{\"correlation_id\":\"abc-123\",\"user\":\"test\"}";

    await InsertOutboxMessageAsync(
      messageId,
      "test-topic",
      "ComplexEvent",
      complexJson,
      status: "Pending",
      metadata: complexMetadata);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      [],
      [],
      [],
      []);

    // Assert - JSON should be returned as text strings
    await Assert.That(result.OutboxWork).HasCount().EqualTo(1);
    var work = result.OutboxWork[0];

    await Assert.That(work.MessageData).Contains("nested");
    await Assert.That(work.MessageData).Contains("array");
    await Assert.That(work.Metadata).Contains("correlation_id");
    await Assert.That(work.Metadata).Contains("abc-123");
  }

  // Helper methods for test data setup and verification

  private async Task InsertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    await using var dbContext = CreateDbContext();
    var now = DateTimeOffset.UtcNow;

    dbContext.Set<ServiceInstanceRecord>().Add(new ServiceInstanceRecord {
      InstanceId = instanceId,
      ServiceName = serviceName,
      HostName = hostName,
      ProcessId = processId,
      StartedAt = now,
      LastHeartbeatAt = now,
      Metadata = null
    });

    await dbContext.SaveChangesAsync();
  }

  private async Task<DateTimeOffset?> GetInstanceHeartbeatAsync(Guid instanceId) {
    await using var dbContext = CreateDbContext();
    var instance = await dbContext.Set<ServiceInstanceRecord>()
      .FirstOrDefaultAsync(i => i.InstanceId == instanceId);
    return instance?.LastHeartbeatAt;
  }

  private async Task<string?> GetInstanceMetadataAsync(Guid instanceId) {
    await using var dbContext = CreateDbContext();
    var instance = await dbContext.Set<ServiceInstanceRecord>()
      .FirstOrDefaultAsync(i => i.InstanceId == instanceId);
    return instance?.Metadata?.RootElement.GetRawText();
  }

  private async Task InsertOutboxMessageAsync(
    Guid messageId,
    string destination,
    string messageType,
    string messageData,
    string status = "Pending",
    Guid? instanceId = null,
    DateTimeOffset? leaseExpiry = null,
    string? metadata = null) {
    await using var dbContext = CreateDbContext();

    dbContext.Set<OutboxRecord>().Add(new OutboxRecord {
      MessageId = messageId.ToString(),
      Destination = destination,
      MessageType = messageType,
      MessageData = JsonDocument.Parse(messageData),
      Metadata = JsonDocument.Parse(metadata ?? "{}"),
      Scope = null,
      Status = status,
      Attempts = 0,
      Error = null,
      CreatedAt = DateTimeOffset.UtcNow,
      PublishedAt = null,
      InstanceId = instanceId,
      LeaseExpiry = leaseExpiry
    });

    await dbContext.SaveChangesAsync();
  }

  private async Task<string?> GetOutboxStatusAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var messageIdStr = messageId.ToString();
    var record = await dbContext.Set<OutboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageIdStr);
    return record?.Status;
  }

  private async Task<Guid?> GetOutboxInstanceIdAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var messageIdStr = messageId.ToString();
    var record = await dbContext.Set<OutboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageIdStr);
    return record?.InstanceId;
  }

  private async Task<DateTimeOffset?> GetOutboxLeaseExpiryAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var messageIdStr = messageId.ToString();
    var record = await dbContext.Set<OutboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageIdStr);
    return record?.LeaseExpiry;
  }

  private async Task InsertInboxMessageAsync(
    Guid messageId,
    string handlerName,
    string messageType,
    string messageData,
    string status = "Pending",
    Guid? instanceId = null,
    DateTimeOffset? leaseExpiry = null) {
    await using var dbContext = CreateDbContext();

    dbContext.Set<InboxRecord>().Add(new InboxRecord {
      MessageId = messageId.ToString(),
      HandlerName = handlerName,
      MessageType = messageType,
      MessageData = JsonDocument.Parse(messageData),
      Metadata = JsonDocument.Parse("{}"),
      Scope = null,
      Status = status,
      ReceivedAt = DateTimeOffset.UtcNow,
      ProcessedAt = null,
      InstanceId = instanceId,
      LeaseExpiry = leaseExpiry
    });

    await dbContext.SaveChangesAsync();
  }

  private async Task<string?> GetInboxStatusAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var messageIdStr = messageId.ToString();
    var record = await dbContext.Set<InboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageIdStr);
    return record?.Status;
  }

  private async Task<Guid?> GetInboxInstanceIdAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var messageIdStr = messageId.ToString();
    var record = await dbContext.Set<InboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageIdStr);
    return record?.InstanceId;
  }
}
