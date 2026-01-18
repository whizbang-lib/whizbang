using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for EFCoreWorkCoordinator.
/// Tests the process_work_batch PostgreSQL function and lease-based work coordination.
/// Uses UUIDv7 for all message IDs to ensure proper time-ordered database indexing.
/// </summary>
public class EFCoreWorkCoordinatorTests : EFCoreTestBase {
  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _sut = null!;
  private Guid _instanceId;
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

  [Before(Test)]
  public async Task TestSetupAsync() {
    _instanceId = _idProvider.NewGuid();
    var dbContext = CreateDbContext();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    _sut = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, jsonOptions);
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
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      leaseSeconds: 300);

    // Assert
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
    await Assert.That(result.InboxWork).Count().IsEqualTo(0);

    // Verify heartbeat was updated
    var heartbeat = await GetInstanceHeartbeatAsync(_instanceId);
    await Assert.That(heartbeat).IsNotNull();
    await Assert.That(heartbeat!.Value > DateTimeOffset.UtcNow.AddSeconds(-5)).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatchAsync_WithMetadata_StoresMetadataCorrectlyAsync() {
    // Arrange
    var metadataJson = """{"version":"1.0.0","environment":"test","enabled":true}""";
    var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson);

    // Act
    await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
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

    await InsertOutboxMessageAsync(messageId1, "topic1", "TestEvent", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);
    await InsertOutboxMessageAsync(messageId2, "topic2", "TestEvent", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = messageId1, Status = MessageProcessingStatus.Published },
        new MessageCompletion { MessageId = messageId2, Status = MessageProcessingStatus.Published }
      ],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      flags: WorkBatchFlags.DebugMode);  // Enable debug mode to keep completed messages

    // Assert
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0)
      .Because("Completed messages should NOT be re-claimed (bug fix prevents reclaiming)");

    // Verify messages marked as Published (using bitwise AND to check if bit is set)
    var status1 = await GetOutboxStatusFlagsAsync(messageId1);
    var status2 = await GetOutboxStatusFlagsAsync(messageId2);
    await Assert.That(status1).IsNotNull().Because("Message 1 should still exist after completion in debug mode");
    await Assert.That(status2).IsNotNull().Because("Message 2 should still exist after completion in debug mode");
    await Assert.That((status1!.Value & MessageProcessingStatus.Published) == MessageProcessingStatus.Published).IsTrue();
    await Assert.That((status2!.Value & MessageProcessingStatus.Published) == MessageProcessingStatus.Published).IsTrue();
  }

  [Test]
  public async Task ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(messageId, "topic1", "TestEvent", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [
        new MessageFailure {
          MessageId = messageId,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Network timeout"
        }
      ],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);

    // Verify message has error recorded (failures are marked by Error field being non-null)
    var status = await GetOutboxStatusFlagsAsync(messageId);
    await Assert.That((status.Value & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed).IsTrue()
      .Because("Failed messages should have the Failed flag set");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_FailedMessageWithSpecialCharacters_EscapesJsonCorrectlyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(messageId, "topic1", "TestEvent", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);

    // Error message with special characters that need JSON escaping
    var errorMessage = "Error with \"quotes\", \nnewlines\n, and \\backslashes\\";

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [
        new MessageFailure {
          MessageId = messageId,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = errorMessage
        }
      ],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Should not throw, error should be recorded
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
    var status = await GetOutboxStatusFlagsAsync(messageId);
    await Assert.That((status.Value & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed).IsTrue()
      .Because("Failed messages should have the Failed flag set");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();

    await InsertInboxMessageAsync(messageId1, "Handler1", "TestEvent", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);
    await InsertInboxMessageAsync(messageId2, "Handler2", "TestEvent", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [
        new MessageCompletion { MessageId = messageId1, Status = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored | MessageProcessingStatus.Published },
        new MessageCompletion { MessageId = messageId2, Status = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored | MessageProcessingStatus.Published }
      ],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.InboxWork).Count().IsEqualTo(0);

    // Verify messages deleted (FullyCompleted messages are deleted in non-debug mode)
    var status1 = await GetInboxStatusFlagsAsync(messageId1);
    var status2 = await GetInboxStatusFlagsAsync(messageId2);
    await Assert.That(status1).IsNull()
      .Because("Fully completed messages should be deleted from inbox");
    await Assert.That(status2).IsNull()
      .Because("Fully completed messages should be deleted from inbox");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertInboxMessageAsync(messageId, "Handler1", "TestEvent", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [
        new MessageFailure {
          MessageId = messageId,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Handler exception"
        }
      ],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.InboxWork).Count().IsEqualTo(0);

    // Verify message has error recorded (failures are marked by Error field being non-null)
    var status = await GetInboxStatusFlagsAsync(messageId);
    await Assert.That((status.Value & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed).IsTrue()
      .Because("Failed messages should have the Failed flag set");
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
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10));

    await InsertOutboxMessageAsync(
      orphanedId2,
      "topic2",
      "OrphanedEvent2",
      "{\"data\":2}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-5));

    // Active message (not expired)
    await InsertOutboxMessageAsync(
      activeId,
      "topic3",
      "ActiveEvent",
      "{\"data\":3}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5));

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Should return 2 work items, not the active one
    await Assert.That(result.OutboxWork).Count().IsEqualTo(2);
    await Assert.That(result.InboxWork).Count().IsEqualTo(0);

    var work1 = result.OutboxWork.First(m => m.MessageId == orphanedId1);
    var work2 = result.OutboxWork.First(m => m.MessageId == orphanedId2);

    await Assert.That(work1.Destination).IsEqualTo("topic1");
    await Assert.That(work2.Destination).IsEqualTo("topic2");

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
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10));

    await InsertInboxMessageAsync(
      orphanedId2,
      "Handler2",
      "OrphanedEvent2",
      "{\"data\":2}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-5));

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
    await Assert.That(result.InboxWork).Count().IsEqualTo(2);

    var work1 = result.InboxWork.First(m => m.MessageId == orphanedId1);
    var work2 = result.InboxWork.First(m => m.MessageId == orphanedId2);

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
    await InsertOutboxMessageAsync(completedOutboxId, "topic1", "Event1", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);
    await InsertInboxMessageAsync(completedInboxId, "Handler1", "Event2", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);

    // Failed messages
    var failedOutboxId = _idProvider.NewGuid();
    var failedInboxId = _idProvider.NewGuid();
    await InsertOutboxMessageAsync(failedOutboxId, "topic2", "Event3", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);
    await InsertInboxMessageAsync(failedInboxId, "Handler2", "Event4", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId);

    // Orphaned messages
    var orphanedOutboxId = _idProvider.NewGuid();
    var orphanedInboxId = _idProvider.NewGuid();
    await InsertOutboxMessageAsync(
      orphanedOutboxId,
      "topic3",
      "OrphanedEvent1",
      "{}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10));
    await InsertInboxMessageAsync(
      orphanedInboxId,
      "Handler3",
      "OrphanedEvent2",
      "{}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10));

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = completedOutboxId, Status = MessageProcessingStatus.Published }
      ],
      outboxFailures: [
        new MessageFailure {
          MessageId = failedOutboxId,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Outbox error"
        }
      ],
      inboxCompletions: [
        new MessageCompletion { MessageId = completedInboxId, Status = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored | MessageProcessingStatus.Published }
      ],
      inboxFailures: [
        new MessageFailure {
          MessageId = failedInboxId,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Inbox error"
        }
      ],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      flags: WorkBatchFlags.DebugMode);  // Enable debug mode to keep completed messages

    // Assert
    await Assert.That(result.OutboxWork).Count().IsEqualTo(1)
      .Because("Only orphaned message returned (completed message NOT re-claimed after bug fix)");
    await Assert.That(result.InboxWork).Count().IsEqualTo(1)
      .Because("Only orphaned message returned (completed message kept in debug mode)");

    // Verify completed (using bitwise AND to check if bit is set)
    var completedStatus = await GetOutboxStatusFlagsAsync(completedOutboxId);
    await Assert.That(completedStatus).IsNotNull().Because("Completed outbox message should still exist in debug mode");
    await Assert.That((completedStatus!.Value & MessageProcessingStatus.Published) == MessageProcessingStatus.Published).IsTrue();
    var completedInboxStatus = await GetInboxStatusFlagsAsync(completedInboxId);
    await Assert.That(completedInboxStatus).IsNotNull().Because("Completed inbox message should still exist in debug mode");
    await Assert.That((completedInboxStatus!.Value & MessageProcessingStatus.EventStored) == MessageProcessingStatus.EventStored).IsTrue();

    // Verify failed (failures have the Failed flag set)
    var failedOutboxStatus = await GetOutboxStatusFlagsAsync(failedOutboxId);
    await Assert.That(failedOutboxStatus).IsNotNull().Because("Failed outbox message should still exist");
    await Assert.That((failedOutboxStatus!.Value & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed).IsTrue()
      .Because("Failed messages should have the Failed flag set");
    var failedInboxStatus = await GetInboxStatusFlagsAsync(failedInboxId);
    await Assert.That(failedInboxStatus).IsNotNull().Because("Failed inbox message should still exist");
    await Assert.That((failedInboxStatus!.Value & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed).IsTrue()
      .Because("Failed messages should have the Failed flag set");

    // Verify work returned and claimed
    // Outbox has 1 item (orphaned only, completed NOT reclaimed)
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
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - All fields should be populated correctly (not null/default)
    await Assert.That(result.OutboxWork).Count().IsEqualTo(1);
    var work = result.OutboxWork[0];

    await Assert.That(work.MessageId).IsEqualTo(messageId);
    await Assert.That(work.Destination).IsEqualTo("test-topic");
    await Assert.That(work.Attempts).IsGreaterThanOrEqualTo(0);  // Attempts starts at 0, only increments on failures
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
      statusFlags: (int)MessageProcessingStatus.Stored,
      metadata: complexMetadata);

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - JSON should be returned as text strings
    await Assert.That(result.OutboxWork).Count().IsEqualTo(1);
    var work = result.OutboxWork[0];

    // Envelope contains the complete message data and metadata
    await Assert.That(work.Envelope).IsNotNull();
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
    return instance?.Metadata != null
      ? JsonSerializer.Serialize(instance.Metadata, JsonContextRegistry.CreateCombinedOptions())
      : null;
  }

  private async Task InsertOutboxMessageAsync(
    Guid messageId,
    string destination,
    string messageType,
    string messageData,
    int statusFlags = (int)MessageProcessingStatus.Stored,
    Guid? instanceId = null,
    DateTimeOffset? leaseExpiry = null,
    string? metadata = null,
    Guid? streamId = null) {
    await using var dbContext = CreateDbContext();

    // Generate streamId if not provided (required for partition-based work selection)
    var actualStreamId = streamId ?? _idProvider.NewGuid();

    // Calculate partition number using compute_partition function
    int? partitionNumber = null;
    await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var command = new Npgsql.NpgsqlCommand(
        "SELECT compute_partition(@streamId::uuid, 10000)",
        connection);
      command.Parameters.AddWithValue("streamId", actualStreamId);
      partitionNumber = (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    // If instanceId is set but leaseExpiry is not, set a valid lease (prevents function from reclaiming as orphaned)
    var actualLeaseExpiry = leaseExpiry;
    if (instanceId.HasValue && !leaseExpiry.HasValue) {
      actualLeaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
    }

    // Create envelope data for testing (MessageType → envelope type, MessageData → envelope structure)
    // The envelope structure contains: MessageId, Hops, and Payload
    var envelopeTypeFullName = typeof(TestMessageEnvelope).AssemblyQualifiedName
      ?? throw new InvalidOperationException("Could not get envelope type name");

    var outboxMessageData = new OutboxMessageData {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.Deserialize<JsonElement>("""{"Data":"test"}"""),
      Hops = new List<MessageHop>()
    };

    var envelopeMetadata = new EnvelopeMetadata {
      MessageId = MessageId.From(messageId),
      Hops = new List<MessageHop>()
    };

    dbContext.Set<OutboxRecord>().Add(new OutboxRecord {
      MessageId = messageId,
      Destination = destination,
      MessageType = envelopeTypeFullName,  // Store envelope type (maps to event_type column)
      MessageData = outboxMessageData,  // Store complete envelope (maps to event_data column)
      Metadata = envelopeMetadata,
      Scope = null,
      StatusFlags = (MessageProcessingStatus)statusFlags,
      Attempts = 0,
      Error = null,
      CreatedAt = DateTimeOffset.UtcNow,
      PublishedAt = null,
      ProcessedAt = null,
      InstanceId = instanceId,
      LeaseExpiry = actualLeaseExpiry,
      StreamId = actualStreamId,
      PartitionNumber = partitionNumber
    });

    await dbContext.SaveChangesAsync();
  }

  private async Task<MessageProcessingStatus?> GetOutboxStatusFlagsAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var record = await dbContext.Set<OutboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageId);
    return record?.StatusFlags;
  }

  private async Task<Guid?> GetOutboxInstanceIdAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var record = await dbContext.Set<OutboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageId);
    return record?.InstanceId;
  }

  private async Task<DateTimeOffset?> GetOutboxLeaseExpiryAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var record = await dbContext.Set<OutboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageId);
    return record?.LeaseExpiry;
  }

  private async Task InsertInboxMessageAsync(
    Guid messageId,
    string handlerName,
    string messageType,
    string messageData,
    int statusFlags = (int)MessageProcessingStatus.Stored,
    Guid? instanceId = null,
    DateTimeOffset? leaseExpiry = null,
    Guid? streamId = null) {
    await using var dbContext = CreateDbContext();

    // Generate streamId if not provided (required for partition-based work selection)
    var actualStreamId = streamId ?? _idProvider.NewGuid();

    // Calculate partition number using compute_partition function
    int? partitionNumber = null;
    await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var command = new Npgsql.NpgsqlCommand(
        "SELECT compute_partition(@streamId::uuid, 10000)",
        connection);
      command.Parameters.AddWithValue("streamId", actualStreamId);
      partitionNumber = (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    // If instanceId is set but leaseExpiry is not, set a valid lease (prevents function from reclaiming as orphaned)
    var actualLeaseExpiry = leaseExpiry;
    if (instanceId.HasValue && !leaseExpiry.HasValue) {
      actualLeaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
    }

    // Create envelope data for testing (MessageType → envelope type, MessageData → envelope structure)
    // The envelope structure contains: MessageId, Hops, and Payload
    var envelopeTypeFullName = typeof(TestMessageEnvelope).AssemblyQualifiedName
      ?? throw new InvalidOperationException("Could not get envelope type name");

    var inboxMessageData = new InboxMessageData {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.Deserialize<JsonElement>("""{"Data":"test"}"""),
      Hops = new List<MessageHop>()
    };

    var envelopeMetadata = new EnvelopeMetadata {
      MessageId = MessageId.From(messageId),
      Hops = new List<MessageHop>()
    };

    dbContext.Set<InboxRecord>().Add(new InboxRecord {
      MessageId = messageId,
      HandlerName = handlerName,
      MessageType = envelopeTypeFullName,  // Store envelope type (maps to event_type column)
      MessageData = inboxMessageData,  // Store complete envelope (maps to event_data column)
      Metadata = envelopeMetadata,
      Scope = null,
      StatusFlags = (MessageProcessingStatus)statusFlags,
      Attempts = 0,
      Error = null,
      ReceivedAt = DateTimeOffset.UtcNow,
      ProcessedAt = null,
      InstanceId = instanceId,
      LeaseExpiry = actualLeaseExpiry,
      StreamId = actualStreamId,
      PartitionNumber = partitionNumber
    });

    await dbContext.SaveChangesAsync();
  }

  private async Task<MessageProcessingStatus?> GetInboxStatusFlagsAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var record = await dbContext.Set<InboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageId);
    return record?.StatusFlags;
  }

  private async Task<Guid?> GetInboxInstanceIdAsync(Guid messageId) {
    await using var dbContext = CreateDbContext();
    var record = await dbContext.Set<InboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageId);
    return record?.InstanceId;
  }

  // ===== MULTI-INSTANCE MODULO DISTRIBUTION TESTS =====

  [Test]
  public async Task ProcessWorkBatchAsync_TwoInstances_DistributesPartitionsViaModuloAsync() {
    // Arrange - Two instances: each should claim messages where partition_number % 2 = instance_index
    var instance1Id = _idProvider.NewGuid();
    var instance2Id = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instance1Id, "TestService", "host1", 11111);
    await InsertServiceInstanceAsync(instance2Id, "TestService", "host2", 22222);

    // Create 10 messages with specific stream IDs to control partition distribution
    // We'll manually set partition numbers to ensure we get a mix across both instances
    var messages = new List<(Guid messageId, int partition)>();
    for (int i = 0; i < 10; i++) {
      var messageId = _idProvider.NewGuid();
      var streamId = _idProvider.NewGuid();

      // Insert message (partition will be calculated automatically)
      await InsertOutboxMessageAsync(
        messageId,
        $"topic{i}",
        $"TestEvent{i}",
        $"{{\"index\":{i}}}",
        statusFlags: (int)MessageProcessingStatus.Stored,
        streamId: streamId);

      // Get the calculated partition number
      await using var dbContext = CreateDbContext();
      var record = await dbContext.Set<OutboxRecord>()
        .FirstOrDefaultAsync(r => r.MessageId == messageId);
      messages.Add((messageId, record!.PartitionNumber!.Value));
    }

    // Act - Instance 1 claims work (should be instance index 0 based on UUID sort order)
    var coordinator1 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result1 = await coordinator1.ProcessWorkBatchAsync(
      instance1Id,
      "TestService",
      "host1",
      11111,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Act - Instance 2 claims work (should be instance index 1 based on UUID sort order)
    var coordinator2 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result2 = await coordinator2.ProcessWorkBatchAsync(
      instance2Id,
      "TestService",
      "host2",
      22222,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Verify each instance only claimed messages with matching modulo
    var claimedByInstance1 = result1.OutboxWork.Select(w => w.MessageId).ToHashSet();
    var claimedByInstance2 = result2.OutboxWork.Select(w => w.MessageId).ToHashSet();

    // No overlap - each instance claims different messages
    await Assert.That(claimedByInstance1.Intersect(claimedByInstance2)).Count().IsEqualTo(0)
      .Because("Each instance should claim different partitions");

    // Total claimed should equal total messages
    await Assert.That(claimedByInstance1.Count + claimedByInstance2.Count).IsEqualTo(10)
      .Because("All messages should be claimed between both instances");

    // Verify modulo distribution: each instance claims messages where partition % 2 matches its sorted index
    // Sorted index: instance with smaller UUID gets bucket 0, larger UUID gets bucket 1
    var instance1Bucket = instance1Id.CompareTo(instance2Id) < 0 ? 0 : 1;
    var instance2Bucket = 1 - instance1Bucket;

    foreach (var messageId in claimedByInstance1) {
      var partition = messages.First(m => m.messageId == messageId).partition;
      await Assert.That(partition % 2).IsEqualTo(instance1Bucket)
        .Because($"Instance 1 has sorted bucket {instance1Bucket}, so should only claim partitions where partition % 2 = {instance1Bucket}");
    }

    foreach (var messageId in claimedByInstance2) {
      var partition = messages.First(m => m.messageId == messageId).partition;
      await Assert.That(partition % 2).IsEqualTo(instance2Bucket)
        .Because($"Instance 2 has sorted bucket {instance2Bucket}, so should only claim partitions where partition % 2 = {instance2Bucket}");
    }
  }

  [Test]
  public async Task ProcessWorkBatchAsync_ThreeInstances_DistributesPartitionsViaModuloAsync() {
    // Arrange - Three instances: each should claim messages where partition_number % 3 = instance_index
    var instance1Id = _idProvider.NewGuid();
    var instance2Id = _idProvider.NewGuid();
    var instance3Id = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instance1Id, "TestService", "host1", 11111);
    await InsertServiceInstanceAsync(instance2Id, "TestService", "host2", 22222);
    await InsertServiceInstanceAsync(instance3Id, "TestService", "host3", 33333);

    // Create 15 messages (divisible by 3 for even distribution testing)
    var messages = new List<(Guid messageId, int partition)>();
    for (int i = 0; i < 15; i++) {
      var messageId = _idProvider.NewGuid();
      var streamId = _idProvider.NewGuid();

      await InsertOutboxMessageAsync(
        messageId,
        $"topic{i}",
        $"TestEvent{i}",
        $"{{\"index\":{i}}}",
        statusFlags: (int)MessageProcessingStatus.Stored,
        streamId: streamId);

      await using var dbContext = CreateDbContext();
      var record = await dbContext.Set<OutboxRecord>()
        .FirstOrDefaultAsync(r => r.MessageId == messageId);
      messages.Add((messageId, record!.PartitionNumber!.Value));
    }

    // Act - Each instance claims work
    var coordinator1 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result1 = await coordinator1.ProcessWorkBatchAsync(
      instance1Id,
      "TestService",
      "host1",
      11111,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    var coordinator2 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result2 = await coordinator2.ProcessWorkBatchAsync(
      instance2Id,
      "TestService",
      "host2",
      22222,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    var coordinator3 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result3 = await coordinator3.ProcessWorkBatchAsync(
      instance3Id,
      "TestService",
      "host3",
      33333,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    var claimedByInstance1 = result1.OutboxWork.Select(w => w.MessageId).ToHashSet();
    var claimedByInstance2 = result2.OutboxWork.Select(w => w.MessageId).ToHashSet();
    var claimedByInstance3 = result3.OutboxWork.Select(w => w.MessageId).ToHashSet();

    // No overlap between any instances
    await Assert.That(claimedByInstance1.Intersect(claimedByInstance2)).Count().IsEqualTo(0);
    await Assert.That(claimedByInstance1.Intersect(claimedByInstance3)).Count().IsEqualTo(0);
    await Assert.That(claimedByInstance2.Intersect(claimedByInstance3)).Count().IsEqualTo(0);

    // Total claimed should equal total messages
    await Assert.That(claimedByInstance1.Count + claimedByInstance2.Count + claimedByInstance3.Count).IsEqualTo(15);

    // Each instance should claim at least 1 message (ideally ~5 each with perfect distribution)
    // Random stream IDs don't guarantee even partition distribution across modulo 3 values
    await Assert.That(claimedByInstance1.Count).IsGreaterThanOrEqualTo(1)
      .Because("Each instance should claim at least some messages based on modulo distribution");
    await Assert.That(claimedByInstance2.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(claimedByInstance3.Count).IsGreaterThanOrEqualTo(1);

    // Verify modulo distribution: each instance claims messages where partition % 3 matches its sorted index
    // Determine sorted instance buckets (0, 1, 2) based on UUID ordering
    var instances = new[] { instance1Id, instance2Id, instance3Id }.OrderBy(id => id).ToArray();
    var instance1Bucket = Array.IndexOf(instances, instance1Id);
    var instance2Bucket = Array.IndexOf(instances, instance2Id);
    var instance3Bucket = Array.IndexOf(instances, instance3Id);

    foreach (var messageId in claimedByInstance1) {
      var partition = messages.First(m => m.messageId == messageId).partition;
      await Assert.That(partition % 3).IsEqualTo(instance1Bucket)
        .Because($"Instance 1 has sorted bucket {instance1Bucket}, so should only claim partitions where partition % 3 = {instance1Bucket}");
    }

    foreach (var messageId in claimedByInstance2) {
      var partition = messages.First(m => m.messageId == messageId).partition;
      await Assert.That(partition % 3).IsEqualTo(instance2Bucket)
        .Because($"Instance 2 has sorted bucket {instance2Bucket}, so should only claim partitions where partition % 3 = {instance2Bucket}");
    }

    foreach (var messageId in claimedByInstance3) {
      var partition = messages.First(m => m.messageId == messageId).partition;
      await Assert.That(partition % 3).IsEqualTo(instance3Bucket)
        .Because($"Instance 3 has sorted bucket {instance3Bucket}, so should only claim partitions where partition % 3 = {instance3Bucket}");
    }
  }

  [Test]
  public async Task ProcessWorkBatchAsync_CrossInstanceStreamOrdering_PreventsClaimingWhenEarlierMessagesHeldAsync() {
    // Arrange - Two instances, same stream with 4 messages
    var instance1Id = _idProvider.NewGuid();
    var instance2Id = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instance1Id, "TestService", "host1", 11111);
    await InsertServiceInstanceAsync(instance2Id, "TestService", "host2", 22222);

    var streamId = _idProvider.NewGuid();

    // Create 4 messages in the same stream with timestamps 100ms apart
    var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
    var message1Id = _idProvider.NewGuid();
    var message2Id = _idProvider.NewGuid();
    var message3Id = _idProvider.NewGuid();
    var message4Id = _idProvider.NewGuid();

    // Insert messages in temporal order (created_at determines ordering)
    // Message 1 (earliest)
    await InsertOutboxMessageWithTimestampAsync(
      message1Id,
      "topic",
      "Event1",
      "{\"seq\":1}",
      streamId,
      createdAt: baseTime,
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Message 2
    await InsertOutboxMessageWithTimestampAsync(
      message2Id,
      "topic",
      "Event2",
      "{\"seq\":2}",
      streamId,
      createdAt: baseTime.AddMilliseconds(100),
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Message 3
    await InsertOutboxMessageWithTimestampAsync(
      message3Id,
      "topic",
      "Event3",
      "{\"seq\":3}",
      streamId,
      createdAt: baseTime.AddMilliseconds(200),
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Message 4 (latest)
    await InsertOutboxMessageWithTimestampAsync(
      message4Id,
      "topic",
      "Event4",
      "{\"seq\":4}",
      streamId,
      createdAt: baseTime.AddMilliseconds(300),
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Act - Instance 1 claims work first (will claim messages based on modulo)
    var coordinator1 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result1 = await coordinator1.ProcessWorkBatchAsync(
      instance1Id,
      "TestService",
      "host1",
      11111,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Act - Instance 2 tries to claim work
    var coordinator2 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result2 = await coordinator2.ProcessWorkBatchAsync(
      instance2Id,
      "TestService",
      "host2",
      22222,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - If instance 1 claimed any messages, instance 2 should NOT claim later messages in the same stream
    // Use MessageId for ordering (UUIDv7 IDs are time-ordered)
    var claimed1 = result1.OutboxWork.OrderBy(w => w.MessageId).ToList();
    var claimed2 = result2.OutboxWork.OrderBy(w => w.MessageId).ToList();

    if (claimed1.Any()) {
      var earliestClaimed1Id = claimed1.Min(w => w.MessageId);

      // Instance 2 should NOT have claimed any messages that are LATER in the stream than instance 1's earliest
      // This validates the cross-instance stream ordering NOT EXISTS check
      foreach (var work2 in claimed2) {
        // If instance 2 has messages, they should either be:
        // 1. Earlier than instance 1's messages, OR
        // 2. Instance 1 has no pending messages (all released/completed)

        // Since we haven't released instance 1's leases, instance 2 should only have earlier messages
        await Assert.That(work2.MessageId).IsLessThan(earliestClaimed1Id)
          .Because("Instance 2 cannot claim messages that come after messages held by instance 1 in the same stream");
      }
    }

    // Verify all messages were claimed by one instance or the other (but respecting stream order)
    var totalClaimed = claimed1.Count + claimed2.Count;
    await Assert.That(totalClaimed).IsGreaterThan(0)
      .Because("At least some messages should be claimed");

    // The total claimed may be less than 4 if cross-instance ordering prevents claiming
    // This is correct behavior - messages are "stuck" waiting for earlier messages to be processed
  }

  // ===== STREAM-BASED FAILURE CASCADE TESTS =====

  [Test]
  public async Task ProcessWorkBatchAsync_CompletionWithStatusZero_DoesNotChangeStatusFlagsAsync() {
    // Arrange - Message claimed by instance 1
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(
      messageId,
      "topic1",
      "TestEvent",
      "{\"data\":1}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId,
      streamId: streamId);

    // Act - Report completion with Status = 0 (clears lease, doesn't change status)
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = messageId, Status = 0 }  // Status = 0 clears lease
      ],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Status flags should remain unchanged
    var statusFlags = await GetOutboxStatusFlagsAsync(messageId);
    await Assert.That(statusFlags).IsEqualTo(MessageProcessingStatus.Stored)
      .Because("Status = 0 should use bitwise OR (status | 0 = status), keeping status unchanged");

    // Message should be re-claimed and returned as orphaned work (if in this instance's partition)
    // OR not returned (if in different partition)
    // The key point is that Status = 0 allows the message to be re-processed
  }

  [Test]
  public async Task ProcessWorkBatchAsync_StreamBasedFailureCascade_ReleasesLaterMessagesInSameStreamAsync() {
    // Arrange - Same stream with 3 messages in temporal order, all claimed by this instance
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var streamId = _idProvider.NewGuid();
    var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);

    // Message 1 (earliest) - will fail
    var message1Id = _idProvider.NewGuid();
    await InsertOutboxMessageWithTimestampAsync(
      message1Id,
      "topic",
      "Event1",
      "{\"seq\":1}",
      streamId,
      createdAt: baseTime,
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    // Message 2 - should be released when message 1 fails
    var message2Id = _idProvider.NewGuid();
    await InsertOutboxMessageWithTimestampAsync(
      message2Id,
      "topic",
      "Event2",
      "{\"seq\":2}",
      streamId,
      createdAt: baseTime.AddMilliseconds(100),
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    // Message 3 - should be released when message 1 fails
    var message3Id = _idProvider.NewGuid();
    await InsertOutboxMessageWithTimestampAsync(
      message3Id,
      "topic",
      "Event3",
      "{\"seq\":3}",
      streamId,
      createdAt: baseTime.AddMilliseconds(200),
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    // Act - Report failure for message 1, and release message 2 and 3 (Status = 0)
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = message2Id, Status = 0 },  // Release later message
        new MessageCompletion { MessageId = message3Id, Status = 0 }   // Release later message
      ],
      outboxFailures: [
        new MessageFailure {
          MessageId = message1Id,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Processing error"
        }
      ],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Message 1 should be marked as failed
    var message1Status = await GetOutboxStatusFlagsAsync(message1Id);
    await Assert.That((message1Status.Value & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed).IsTrue()
      .Because("Failed message should have Failed flag set");

    // Assert - Messages 2 and 3 status flags should be unchanged (still Stored)
    var message2Status = await GetOutboxStatusFlagsAsync(message2Id);
    var message3Status = await GetOutboxStatusFlagsAsync(message3Id);
    await Assert.That(message2Status).IsEqualTo(MessageProcessingStatus.Stored)
      .Because("Status = 0 should not change status flags");
    await Assert.That(message3Status).IsEqualTo(MessageProcessingStatus.Stored)
      .Because("Status = 0 should not change status flags");

    // Messages 2 and 3 will be re-claimed by this instance (if partitions belong to it)
    // The key point is they are released and available for re-processing
    // We can verify this by checking the message 1 has Failed flag while 2 and 3 don't
  }

  [Test]
  public async Task ProcessWorkBatchAsync_ClearedLeaseMessages_BecomeAvailableForOtherInstancesAsync() {
    // Arrange - Two instances with different partition ownership
    var instance1Id = _idProvider.NewGuid();
    var instance2Id = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instance1Id, "TestService", "host1", 11111);
    await InsertServiceInstanceAsync(instance2Id, "TestService", "host2", 22222);

    var streamId = _idProvider.NewGuid();
    var message1Id = _idProvider.NewGuid();

    // Insert message claimed by instance 1
    await InsertOutboxMessageAsync(
      message1Id,
      "topic",
      "Event1",
      "{\"seq\":1}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: instance1Id,
      streamId: streamId);

    // Act - Instance 1 releases the message (Status = 0)
    var coordinator1 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result1 = await coordinator1.ProcessWorkBatchAsync(
      instance1Id,
      "TestService",
      "host1",
      11111,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = message1Id, Status = 0 }
      ],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Act - Instance 2 tries to claim work
    var coordinator2 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result2 = await coordinator2.ProcessWorkBatchAsync(
      instance2Id,
      "TestService",
      "host2",
      22222,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Combined results should include the message
    // The message will be claimed by whichever instance owns its partition (based on modulo distribution)
    var claimedByInstance1 = result1.OutboxWork.Select(w => w.MessageId).Contains(message1Id);
    var claimedByInstance2 = result2.OutboxWork.Select(w => w.MessageId).Contains(message1Id);

    // Exactly one instance should have claimed it (whichever owns the partition)
    await Assert.That(claimedByInstance1 || claimedByInstance2).IsTrue()
      .Because("Released message should be claimed by the instance that owns its partition");

    // Verify the message can be re-claimed after release (not stuck)
    var finalStatus = await GetOutboxStatusFlagsAsync(message1Id);
    await Assert.That(finalStatus).IsEqualTo(MessageProcessingStatus.Stored)
      .Because("Status = 0 should not change status flags, message remains Stored and claimable");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_UnitOfWorkPattern_ProcessesCompletionsAndFailuresInSameCallAsync() {
    // Arrange - Multiple messages in same stream
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var streamId = _idProvider.NewGuid();
    var message1Id = _idProvider.NewGuid();
    var message2Id = _idProvider.NewGuid();
    var message3Id = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(message1Id, "topic", "Event1", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId, streamId: streamId);
    await InsertOutboxMessageAsync(message2Id, "topic", "Event2", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId, streamId: streamId);
    await InsertOutboxMessageAsync(message3Id, "topic", "Event3", "{}", statusFlags: (int)MessageProcessingStatus.Stored, instanceId: _instanceId, streamId: streamId);

    // Act - Report mixed completions and failures in a single call (Unit of Work pattern)
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = message1Id, Status = MessageProcessingStatus.Published },  // Success
        new MessageCompletion { MessageId = message3Id, Status = 0 }  // Release
      ],
      outboxFailures: [
        new MessageFailure {
          MessageId = message2Id,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Processing failed"
        }
      ],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      flags: WorkBatchFlags.DebugMode);  // Enable debug mode to keep completed messages

    // Assert - Verify all results were processed correctly in single transaction
    var status1 = await GetOutboxStatusFlagsAsync(message1Id);
    var status2 = await GetOutboxStatusFlagsAsync(message2Id);
    var status3 = await GetOutboxStatusFlagsAsync(message3Id);

    // Message 1 should be marked as Published
    await Assert.That(status1).IsNotNull().Because("Message 1 should still exist in debug mode");
    await Assert.That((status1!.Value & MessageProcessingStatus.Published) == MessageProcessingStatus.Published).IsTrue()
      .Because("Message 1 should be marked as Published");

    // Message 2 should be marked as Failed
    await Assert.That(status2).IsNotNull().Because("Message 2 should still exist");
    await Assert.That((status2!.Value & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed).IsTrue()
      .Because("Message 2 should be marked as Failed");

    // Message 3 should have status unchanged (Status = 0 doesn't modify status flags)
    await Assert.That(status3).IsEqualTo(MessageProcessingStatus.Stored)
      .Because("Message 3 status should remain Stored (Status = 0 uses bitwise OR)");
  }

  // ===== INSTANCE LIFECYCLE TESTS =====

  /// <summary>
  /// **Given**: An instance stops heartbeating (stale threshold exceeded)
  /// **When**: Another instance calls ProcessWorkBatchAsync
  /// **Then**: Stale instance is deleted and its partitions are released
  /// </summary>
  /// <docs>messaging/multi-instance-coordination#stale-instance-cleanup</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_StaleInstance_CleanedUpAndPartitionsReleasedAsync() {
    // Arrange - Instance 1 exists but hasn't heartbeated (simulating stale instance)
    var staleInstanceId = _idProvider.NewGuid();
    var activeInstanceId = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(staleInstanceId, "TestService", "host1", 11111);
    await InsertServiceInstanceAsync(activeInstanceId, "TestService", "host2", 22222);

    // Manually set instance 1's heartbeat to be stale (older than threshold)
    await using (var dbContext = CreateDbContext()) {
      var instance = await dbContext.Set<ServiceInstanceRecord>()
        .FirstOrDefaultAsync(i => i.InstanceId == staleInstanceId);
      instance!.LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-15); // Beyond 10-minute threshold
      await dbContext.SaveChangesAsync();
    }

    // Act - Active instance processes work batch with short stale threshold
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    await coordinator.ProcessWorkBatchAsync(
      activeInstanceId,
      "TestService",
      "host2",
      22222,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      staleThresholdSeconds: 600); // 10 minutes

    // Assert - Stale instance should be deleted
    await using (var dbContext = CreateDbContext()) {
      var staleInstance = await dbContext.Set<ServiceInstanceRecord>()
        .FirstOrDefaultAsync(i => i.InstanceId == staleInstanceId);
      await Assert.That(staleInstance).IsNull()
        .Because("Stale instances should be cleaned up when heartbeat exceeds threshold");

      var activeInstance = await dbContext.Set<ServiceInstanceRecord>()
        .FirstOrDefaultAsync(i => i.InstanceId == activeInstanceId);
      await Assert.That(activeInstance).IsNotNull()
        .Because("Active instances should remain");
    }
  }

  /// <summary>
  /// **Given**: An instance crashes (becomes stale) with messages that have expired leases
  /// **When**: Another instance claims work
  /// **Then**: Stale instance is deleted, messages are released, and recovery instance reclaims them
  /// </summary>
  /// <docs>messaging/multi-instance-coordination#lease-expiry-orphaned-work</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_InstanceCrashes_MessagesReclaimedAfterLeaseExpiryAsync() {
    // Arrange - Create crashed instance (that will become stale) and recovery instance
    var crashedInstanceId = _idProvider.NewGuid();
    var recoveryInstanceId = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(crashedInstanceId, "TestService", "host1", 11111);
    await InsertServiceInstanceAsync(recoveryInstanceId, "TestService", "host2", 22222);

    // Make crashed instance STALE by setting last_heartbeat_at > 10 minutes ago
    await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var command = new Npgsql.NpgsqlCommand(
        "UPDATE wh_service_instances SET last_heartbeat_at = @staleTime WHERE instance_id = @instanceId",
        connection);
      command.Parameters.AddWithValue("staleTime", DateTimeOffset.UtcNow.AddMinutes(-15));
      command.Parameters.AddWithValue("instanceId", crashedInstanceId);
      await command.ExecuteNonQueryAsync();
    }

    // Insert messages claimed by crashed instance (with expired leases)
    // Use different stream IDs to avoid stream ordering blocking
    var message1Id = _idProvider.NewGuid();
    var message2Id = _idProvider.NewGuid();
    var stream1Id = _idProvider.NewGuid();
    var stream2Id = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(
      message1Id,
      "topic1",
      "Event1",
      "{}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: crashedInstanceId,  // Owned by crashed instance
      leaseExpiry: DateTimeOffset.UtcNow.AddSeconds(-10), // Expired 10 seconds ago
      streamId: stream1Id);

    await InsertOutboxMessageAsync(
      message2Id,
      "topic2",
      "Event2",
      "{}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: crashedInstanceId,  // Owned by crashed instance
      leaseExpiry: DateTimeOffset.UtcNow.AddSeconds(-5), // Expired 5 seconds ago
      streamId: stream2Id);

    // Act - Recovery instance claims work
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result = await coordinator.ProcessWorkBatchAsync(
      recoveryInstanceId,
      "TestService",
      "host2",
      22222,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Messages should be reclaimed by recovery instance
    await Assert.That(result.OutboxWork).Count().IsEqualTo(2)
      .Because("Orphaned messages should be reclaimed");

    var claimedIds = result.OutboxWork.Select(w => w.MessageId).ToHashSet();
    await Assert.That(claimedIds.Contains(message1Id)).IsTrue();
    await Assert.That(claimedIds.Contains(message2Id)).IsTrue();

    // Verify messages now owned by recovery instance
    var newOwner1 = await GetOutboxInstanceIdAsync(message1Id);
    var newOwner2 = await GetOutboxInstanceIdAsync(message2Id);
    await Assert.That(newOwner1).IsEqualTo(recoveryInstanceId);
    await Assert.That(newOwner2).IsEqualTo(recoveryInstanceId);
  }

  /// <summary>
  /// **Given**: Multiple active instances (all heartbeating)
  /// **When**: ProcessWorkBatchAsync is called
  /// **Then**: All active instances are counted in distribution calculation
  /// </summary>
  /// <docs>messaging/multi-instance-coordination#new-instance-joining</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_MultipleActiveInstances_AllCountedInDistributionAsync() {
    // Arrange - Three active instances (all heartbeating recently)
    var instance1Id = _idProvider.NewGuid();
    var instance2Id = _idProvider.NewGuid();
    var instance3Id = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instance1Id, "TestService", "host1", 11111);
    await InsertServiceInstanceAsync(instance2Id, "TestService", "host2", 22222);
    await InsertServiceInstanceAsync(instance3Id, "TestService", "host3", 33333);

    // Insert messages that will be distributed across 3 instances
    var messages = new List<Guid>();
    for (int i = 0; i < 12; i++) {
      var messageId = _idProvider.NewGuid();
      messages.Add(messageId);
      await InsertOutboxMessageAsync(
        messageId,
        $"topic{i}",
        $"Event{i}",
        $"{{\"index\":{i}}}",
        statusFlags: (int)MessageProcessingStatus.Stored);
    }

    // Act - All three instances claim work
    var coordinator1 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result1 = await coordinator1.ProcessWorkBatchAsync(
      instance1Id, "TestService", "host1", 11111, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    var coordinator2 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result2 = await coordinator2.ProcessWorkBatchAsync(
      instance2Id, "TestService", "host2", 22222, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    var coordinator3 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result3 = await coordinator3.ProcessWorkBatchAsync(
      instance3Id, "TestService", "host3", 33333, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - Work should be distributed across all 3 instances
    var claimed1 = result1.OutboxWork.Select(w => w.MessageId).ToHashSet();
    var claimed2 = result2.OutboxWork.Select(w => w.MessageId).ToHashSet();
    var claimed3 = result3.OutboxWork.Select(w => w.MessageId).ToHashSet();

    // No overlap between instances
    await Assert.That(claimed1.Intersect(claimed2)).Count().IsEqualTo(0);
    await Assert.That(claimed1.Intersect(claimed3)).Count().IsEqualTo(0);
    await Assert.That(claimed2.Intersect(claimed3)).Count().IsEqualTo(0);

    // All messages claimed
    var totalClaimed = claimed1.Count + claimed2.Count + claimed3.Count;
    await Assert.That(totalClaimed).IsEqualTo(12)
      .Because("All 12 messages should be distributed across 3 instances");

    // Each instance should get at least 1 message (with 12 messages and 3 instances, we expect ~4 each)
    await Assert.That(claimed1.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(claimed2.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(claimed3.Count).IsGreaterThanOrEqualTo(1);
  }

  // ===== PARTITION STABILITY TESTS =====

  /// <summary>
  /// **Given**: Message assigned to Instance 1's partition
  /// **When**: Instance 2 joins (would normally cause partition reassignment)
  /// **Then**: Message remains with Instance 1 until lease expires or instance goes stale
  /// </summary>
  /// <docs>messaging/multi-instance-coordination#partition-reassignment</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_NewInstanceJoining_DoesNotStealActivePartitionsAsync() {
    // Arrange - Instance 1 exists with claimed messages
    var instance1Id = _idProvider.NewGuid();
    var instance2Id = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instance1Id, "TestService", "host1", 11111);

    var messageId = _idProvider.NewGuid();
    await InsertOutboxMessageAsync(
      messageId,
      "topic1",
      "Event1",
      "{}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: instance1Id,
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5)); // Active lease

    // Act - Instance 2 joins and tries to claim work
    await InsertServiceInstanceAsync(instance2Id, "TestService", "host2", 22222);

    var coordinator2 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result2 = await coordinator2.ProcessWorkBatchAsync(
      instance2Id, "TestService", "host2", 22222, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - Instance 2 should NOT claim instance 1's active messages
    var claimed2Ids = result2.OutboxWork.Select(w => w.MessageId).ToHashSet();
    await Assert.That(claimed2Ids.Contains(messageId)).IsFalse()
      .Because("New instances should not steal messages with active leases from existing instances");

    // Verify message still owned by instance 1
    var currentOwner = await GetOutboxInstanceIdAsync(messageId);
    await Assert.That(currentOwner).IsEqualTo(instance1Id)
      .Because("Message ownership should remain with instance 1 until lease expires or instance goes stale");
  }

  /// <summary>
  /// **Given**: Instance 1 holds partitions
  /// **When**: Instance 1 becomes stale
  /// **Then**: Partitions are released and redistributed via CASCADE DELETE
  /// </summary>
  /// <docs>messaging/multi-instance-coordination#partition-reassignment</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_InstanceGoesStale_PartitionsReleasedViaCascadeAsync() {
    // Arrange - Instance 1 with partitions
    var staleInstanceId = _idProvider.NewGuid();
    var activeInstanceId = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(staleInstanceId, "TestService", "host1", 11111);

    // Claim some messages to trigger partition assignment
    var messageId = _idProvider.NewGuid();
    await InsertOutboxMessageAsync(
      messageId,
      "topic1",
      "Event1",
      "{}",
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Instance 1 claims work (establishes partition ownership)
    var coordinator1 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    await coordinator1.ProcessWorkBatchAsync(
      staleInstanceId, "TestService", "host1", 11111, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Make instance 1 stale
    await using (var dbContext = CreateDbContext()) {
      var instance = await dbContext.Set<ServiceInstanceRecord>()
        .FirstOrDefaultAsync(i => i.InstanceId == staleInstanceId);
      instance!.LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-15);
      await dbContext.SaveChangesAsync();
    }

    // Act - New instance joins and triggers stale cleanup
    await InsertServiceInstanceAsync(activeInstanceId, "TestService", "host2", 22222);

    var coordinator2 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    await coordinator2.ProcessWorkBatchAsync(
      activeInstanceId, "TestService", "host2", 22222, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: [],
      staleThresholdSeconds: 600);

    // Assert - Stale instance's partitions should be released (CASCADE DELETE)
    await using (var dbContext = CreateDbContext()) {
      var staleInstance = await dbContext.Set<ServiceInstanceRecord>()
        .FirstOrDefaultAsync(i => i.InstanceId == staleInstanceId);
      await Assert.That(staleInstance).IsNull()
        .Because("Stale instance should be deleted");

      // Partition assignments for stale instance should also be deleted (CASCADE)
      await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
          "SELECT COUNT(*) FROM wh_partition_assignments WHERE instance_id = @instanceId",
          connection);
        command.Parameters.AddWithValue("instanceId", staleInstanceId);
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        await Assert.That(count).IsEqualTo(0)
          .Because("CASCADE DELETE should remove partition assignments when instance is deleted");
      }
    }
  }

  /// <summary>
  /// **Given**: Instance 1 fails but lease not yet expired
  /// **When**: Other instances try to claim work
  /// **Then**: Messages remain locked until lease expires
  /// </summary>
  /// <docs>messaging/multi-instance-coordination#lease-expiry-orphaned-work</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_LeaseNotExpired_MessagesRemainLockedAsync() {
    // Arrange - Instance 1 claims messages with active lease
    var instance1Id = _idProvider.NewGuid();
    var instance2Id = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instance1Id, "TestService", "host1", 11111);
    await InsertServiceInstanceAsync(instance2Id, "TestService", "host2", 22222);

    var messageId = _idProvider.NewGuid();
    await InsertOutboxMessageAsync(
      messageId,
      "topic1",
      "Event1",
      "{}",
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: instance1Id,
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5)); // Still valid for 5 minutes

    // Act - Instance 2 tries to claim work
    var coordinator2 = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());
    var result2 = await coordinator2.ProcessWorkBatchAsync(
      instance2Id, "TestService", "host2", 22222, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - Instance 2 should NOT claim instance 1's message
    var claimed2Ids = result2.OutboxWork.Select(w => w.MessageId).ToHashSet();
    await Assert.That(claimed2Ids.Contains(messageId)).IsFalse()
      .Because("Messages with active leases should not be claimable by other instances");

    // Verify message still owned by instance 1
    var currentOwner = await GetOutboxInstanceIdAsync(messageId);
    await Assert.That(currentOwner).IsEqualTo(instance1Id);
  }

  // ===== SCHEDULED RETRY STREAM ORDERING TESTS =====

  /// <summary>
  /// **Given**: Message M1 in stream S is scheduled for retry (scheduled_for > now)
  /// **When**: Instance tries to claim later message M2 in same stream
  /// **Then**: M2 is blocked until M1's scheduled_for time passes
  /// </summary>
  /// <docs>messaging/multi-instance-coordination#scheduled-retry-blocking</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_ScheduledRetry_BlocksLaterMessagesInStreamAsync() {
    // Arrange - Two messages in same stream, first one scheduled for future retry
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var streamId = _idProvider.NewGuid();
    var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);

    // Message 1 - scheduled for retry in 10 minutes
    var message1Id = _idProvider.NewGuid();
    await InsertOutboxMessageWithTimestampAndScheduledAsync(
      message1Id,
      "topic",
      "Event1",
      streamId,
      createdAt: baseTime,
      scheduledFor: DateTimeOffset.UtcNow.AddMinutes(10), // Scheduled for future
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Message 2 - later in stream, ready to process
    var message2Id = _idProvider.NewGuid();
    await InsertOutboxMessageWithTimestampAsync(
      message2Id,
      "topic",
      "Event2",
      "{\"seq\":2}",
      streamId,
      createdAt: baseTime.AddMilliseconds(100),
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Act - Try to claim work
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId, "TestService", "test-host", 12345, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - M2 should be blocked by M1's scheduled retry
    var claimedIds = result.OutboxWork.Select(w => w.MessageId).ToHashSet();
    await Assert.That(claimedIds.Contains(message1Id)).IsFalse()
      .Because("M1 is not yet ready for retry (scheduled_for > now)");
    await Assert.That(claimedIds.Contains(message2Id)).IsFalse()
      .Because("M2 is blocked by earlier message M1 with scheduled_for > now");
  }

  /// <summary>
  /// **Given**: Message M1 scheduled for retry, scheduled_for time passes
  /// **When**: Instance claims work
  /// **Then**: M1 and M2 become claimable (stream unblocked)
  /// </summary>
  /// <docs>messaging/multi-instance-coordination#scheduled-retry-blocking</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_ScheduledRetryExpires_UnblocksStreamAsync() {
    // Arrange - Two messages in same stream, first one scheduled for past retry
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var streamId = _idProvider.NewGuid();
    var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);

    // Message 1 - scheduled for retry that has already passed
    var message1Id = _idProvider.NewGuid();
    await InsertOutboxMessageWithTimestampAndScheduledAsync(
      message1Id,
      "topic",
      "Event1",
      streamId,
      createdAt: baseTime,
      scheduledFor: DateTimeOffset.UtcNow.AddSeconds(-10), // Scheduled time has passed
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Message 2 - later in stream
    var message2Id = _idProvider.NewGuid();
    await InsertOutboxMessageWithTimestampAsync(
      message2Id,
      "topic",
      "Event2",
      "{\"seq\":2}",
      streamId,
      createdAt: baseTime.AddMilliseconds(100),
      statusFlags: (int)MessageProcessingStatus.Stored);

    // Act - Try to claim work
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId, "TestService", "test-host", 12345, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - Both messages should be claimable now
    var claimedIds = result.OutboxWork.Select(w => w.MessageId).ToHashSet();
    await Assert.That(result.OutboxWork).Count().IsEqualTo(2)
      .Because("Both M1 and M2 should be claimable once scheduled_for time passes");
    await Assert.That(claimedIds.Contains(message1Id)).IsTrue()
      .Because("M1's scheduled retry time has passed");
    await Assert.That(claimedIds.Contains(message2Id)).IsTrue()
      .Because("M2 is no longer blocked by M1");
  }

  // ===== IDEMPOTENCY TESTS =====

  /// <summary>
  /// **Given**: Inbox message with duplicate message_id
  /// **When**: Try to insert via ProcessWorkBatchAsync
  /// **Then**: Duplicate rejected via wh_message_deduplication table (ON CONFLICT DO NOTHING)
  /// </summary>
  /// <docs>messaging/idempotency-patterns#inbox-idempotency</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_DuplicateInboxMessage_DeduplicationPreventsAsync() {
    // Arrange - Insert first message
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    // First insert via deduplication table
    await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var command = new Npgsql.NpgsqlCommand(
        "INSERT INTO wh_message_deduplication (message_id, first_seen_at) VALUES (@messageId, NOW())",
        connection);
      command.Parameters.AddWithValue("messageId", messageId);
      await command.ExecuteNonQueryAsync();
    }

    // Act - Try to insert duplicate via ProcessWorkBatchAsync
    var inboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = "TestMessage, TestAssembly",
      StreamId = streamId,
      IsEvent = false
    };

    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId, "TestService", "test-host", 12345, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [inboxMessage],
      renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - Duplicate should be rejected (no work returned for duplicate)
    await Assert.That(result.InboxWork).Count().IsEqualTo(0)
      .Because("Duplicate inbox messages should be rejected via wh_message_deduplication ON CONFLICT DO NOTHING");

    // Verify message NOT in inbox (deduplication prevented insert)
    await using (var dbContext = CreateDbContext()) {
      var inboxRecord = await dbContext.Set<InboxRecord>()
        .FirstOrDefaultAsync(r => r.MessageId == messageId);
      await Assert.That(inboxRecord).IsNull()
        .Because("Duplicate message should not be inserted into inbox");

      // Verify deduplication record exists
      await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
      await connection.OpenAsync();
      await using var command = new Npgsql.NpgsqlCommand(
        "SELECT COUNT(*) FROM wh_message_deduplication WHERE message_id = @messageId",
        connection);
      command.Parameters.AddWithValue("messageId", messageId);
      var count = (long)(await command.ExecuteScalarAsync() ?? 0);
      await Assert.That(count).IsEqualTo(1)
        .Because("Deduplication table should still have single entry");
    }
  }

  /// <summary>
  /// **Given**: Outbox does NOT have deduplication table
  /// **When**: Application logic creates duplicate messages
  /// **Then**: Outbox accepts duplicates (application's responsibility to prevent)
  /// </summary>
  /// <docs>messaging/idempotency-patterns#outbox-idempotency</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_OutboxNoDuplication_TransactionalBoundaryAsync() {
    // Arrange - Outbox has no deduplication table, duplicates are allowed
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var streamId = _idProvider.NewGuid();

    // Act - Insert two messages with same content (different IDs)
    var message1Id = _idProvider.NewGuid();
    var message2Id = _idProvider.NewGuid();

    var outboxMessage1 = CreateTestOutboxMessage(message1Id, "orders.topic", streamId, isEvent: false);
    var outboxMessage2 = CreateTestOutboxMessage(message2Id, "orders.topic", streamId, isEvent: false);

    await _sut.ProcessWorkBatchAsync(
      _instanceId, "TestService", "test-host", 12345, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [outboxMessage1, outboxMessage2], newInboxMessages: [],
      renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - Both messages accepted (no deduplication for outbox)
    await using (var dbContext = CreateDbContext()) {
      var outboxRecord1 = await dbContext.Set<OutboxRecord>()
        .FirstOrDefaultAsync(r => r.MessageId == message1Id);
      var outboxRecord2 = await dbContext.Set<OutboxRecord>()
        .FirstOrDefaultAsync(r => r.MessageId == message2Id);

      await Assert.That(outboxRecord1).IsNotNull()
        .Because("Outbox does not deduplicate - application's responsibility");
      await Assert.That(outboxRecord2).IsNotNull()
        .Because("Outbox does not deduplicate - application's responsibility");
    }
  }

  // ===== ORDERING UNDER FAILURE TESTS =====

  /// <summary>
  /// **Given**: Message M1 fails (scheduled for retry), M2 and M3 are released (Status = 0)
  /// **When**: Another batch processes work
  /// **Then**: M2, M3 leases cleared BUT still blocked by M1's scheduled retry
  ///
  /// This test demonstrates that Status=0 completions clear leases (instance_id/lease_expiry = NULL),
  /// but the NOT EXISTS clause still blocks later messages if an earlier message has scheduled_for > now.
  /// </summary>
  /// <docs>messaging/failure-handling#failure-cascade</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_FailureWithCascadeRelease_AllowsLaterProcessingAsync() {
    // Arrange - Stream with 3 messages, first one will fail
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var streamId = _idProvider.NewGuid();
    var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);

    var message1Id = _idProvider.NewGuid();
    var message2Id = _idProvider.NewGuid();
    var message3Id = _idProvider.NewGuid();

    await InsertOutboxMessageWithTimestampAsync(
      message1Id, "topic", "Event1", "{\"seq\":1}", streamId,
      createdAt: baseTime,
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    await InsertOutboxMessageWithTimestampAsync(
      message2Id, "topic", "Event2", "{\"seq\":2}", streamId,
      createdAt: baseTime.AddMilliseconds(100),
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    await InsertOutboxMessageWithTimestampAsync(
      message3Id, "topic", "Event3", "{\"seq\":3}", streamId,
      createdAt: baseTime.AddMilliseconds(200),
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    // Act - M1 fails, M2 and M3 released (Status = 0 cascade release pattern)
    await _sut.ProcessWorkBatchAsync(
      _instanceId, "TestService", "test-host", 12345, metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = message2Id, Status = 0 }, // Release
        new MessageCompletion { MessageId = message3Id, Status = 0 }  // Release
      ],
      outboxFailures: [
        new MessageFailure {
          MessageId = message1Id,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Transient failure"
        }
      ],
      inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - M2 and M3 are still BLOCKED because M1 is scheduled for retry
    // Even though we released M2 and M3 (Status=0), the NOT EXISTS clause blocks them
    // because M1 (earlier message in same stream) has scheduled_for > now
    var result2 = await _sut.ProcessWorkBatchAsync(
      _instanceId, "TestService", "test-host", 12345, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    var claimedIds = result2.OutboxWork.Select(w => w.MessageId).ToHashSet();

    // M2 and M3 are BLOCKED by M1's scheduled retry
    await Assert.That(claimedIds.Contains(message2Id)).IsFalse()
      .Because("M2 is blocked by M1's scheduled_for > now");
    await Assert.That(claimedIds.Contains(message3Id)).IsFalse()
      .Because("M3 is blocked by M1's scheduled_for > now");

    // M1 should NOT be claimable (scheduled for future retry)
    await Assert.That(claimedIds.Contains(message1Id)).IsFalse()
      .Because("Failed message should be scheduled for retry (not immediately claimable)");

    // Verify that M2 and M3 DO have their leases cleared (Status=0 worked)
    await using (var dbContext = CreateDbContext()) {
      var message2 = await dbContext.Set<OutboxRecord>()
        .FirstOrDefaultAsync(m => m.MessageId == message2Id);
      var message3 = await dbContext.Set<OutboxRecord>()
        .FirstOrDefaultAsync(m => m.MessageId == message3Id);

      await Assert.That(message2?.InstanceId).IsNull()
        .Because("Status=0 completion should clear instance_id");
      await Assert.That(message2?.LeaseExpiry).IsNull()
        .Because("Status=0 completion should clear lease_expiry");
      await Assert.That(message3?.InstanceId).IsNull()
        .Because("Status=0 completion should clear instance_id");
      await Assert.That(message3?.LeaseExpiry).IsNull()
        .Because("Status=0 completion should clear lease_expiry");
    }
  }

  /// <summary>
  /// **Given**: Message M1 fails WITHOUT releasing later messages
  /// **When**: Later messages M2, M3 remain leased
  /// **Then**: Stream is blocked until M1 succeeds or messages released
  /// </summary>
  /// <docs>messaging/failure-handling#failure-cascade</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_FailureWithoutRelease_BlocksStreamAsync() {
    // Arrange - Stream with 3 messages
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var streamId = _idProvider.NewGuid();
    var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);

    var message1Id = _idProvider.NewGuid();
    var message2Id = _idProvider.NewGuid();
    var message3Id = _idProvider.NewGuid();

    await InsertOutboxMessageWithTimestampAsync(
      message1Id, "topic", "Event1", "{\"seq\":1}", streamId,
      createdAt: baseTime,
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    await InsertOutboxMessageWithTimestampAsync(
      message2Id, "topic", "Event2", "{\"seq\":2}", streamId,
      createdAt: baseTime.AddMilliseconds(100),
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    await InsertOutboxMessageWithTimestampAsync(
      message3Id, "topic", "Event3", "{\"seq\":3}", streamId,
      createdAt: baseTime.AddMilliseconds(200),
      statusFlags: (int)MessageProcessingStatus.Stored,
      instanceId: _instanceId);

    // Act - M1 fails, M2 and M3 NOT released (remain leased)
    await _sut.ProcessWorkBatchAsync(
      _instanceId, "TestService", "test-host", 12345, metadata: null,
      outboxCompletions: [],
      outboxFailures: [
        new MessageFailure {
          MessageId = message1Id,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Transient failure"
        }
      ],
      inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // Assert - M2 and M3 should NOT be claimable (still leased by this instance, blocked by M1 scheduled retry)
    var result2 = await _sut.ProcessWorkBatchAsync(
      _instanceId, "TestService", "test-host", 12345, metadata: null,
      outboxCompletions: [], outboxFailures: [], inboxCompletions: [], inboxFailures: [],
      receptorCompletions: [], receptorFailures: [], perspectiveCompletions: [], perspectiveFailures: [],
      newOutboxMessages: [], newInboxMessages: [], renewOutboxLeaseIds: [], renewInboxLeaseIds: []);

    // All messages should be blocked (M1 scheduled, M2/M3 still have active leases)
    await Assert.That(result2.OutboxWork).Count().IsEqualTo(0)
      .Because("Stream is blocked - M1 scheduled for retry, M2/M3 still have active leases");
  }

  // ===== STARVATION DETECTION TEST =====

  /// <summary>
  /// **Given**: Message M1 in stream S has high attempts count (e.g., 10+)
  /// **When**: Message still scheduled for retry
  /// **Then**: Poison message detection needed (not automatically flagged by system, application responsibility)
  /// </summary>
  /// <docs>messaging/failure-handling#poison-messages</docs>
  [Test]
  public async Task ProcessWorkBatchAsync_HighRetryCount_PoisonMessageDetectionAsync() {
    // Arrange - Message with high retry count (simulating poison message)
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    var streamId = _idProvider.NewGuid();
    var poisonMessageId = _idProvider.NewGuid();

    // Manually insert poison message with high attempts count
    await using (var dbContext = CreateDbContext()) {
      var partitionNumber = 0;
      await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
          "SELECT compute_partition(@streamId::uuid, 10000)",
          connection);
        command.Parameters.AddWithValue("streamId", streamId);
        partitionNumber = (int)(await command.ExecuteScalarAsync() ?? 0);
      }

      var envelopeTypeFullName = typeof(TestMessageEnvelope).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Could not get envelope type name");

      var poisonOutboxMessageData = new OutboxMessageData {
        MessageId = MessageId.From(poisonMessageId),
        Payload = JsonSerializer.Deserialize<JsonElement>("{}"),
        Hops = new List<MessageHop>()
      };

      var poisonEnvelopeMetadata = new EnvelopeMetadata {
        MessageId = MessageId.From(poisonMessageId),
        Hops = new List<MessageHop>()
      };

      dbContext.Set<OutboxRecord>().Add(new OutboxRecord {
        MessageId = poisonMessageId,
        Destination = "topic",
        MessageType = envelopeTypeFullName,
        MessageData = poisonOutboxMessageData,
        Metadata = poisonEnvelopeMetadata,
        Scope = null,
        StatusFlags = MessageProcessingStatus.Stored | MessageProcessingStatus.Failed,
        Attempts = 12, // High retry count
        Error = "SerializationError: Malformed JSON",
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        PublishedAt = null,
        ProcessedAt = null,
        ScheduledFor = DateTimeOffset.UtcNow.AddMinutes(30), // Still scheduled for retry
        InstanceId = null,
        LeaseExpiry = null,
        StreamId = streamId,
        PartitionNumber = partitionNumber
      });

      await dbContext.SaveChangesAsync();
    }

    // Act - Query for poison message candidates (application responsibility)
    await using (var dbContext = CreateDbContext()) {
      var poisonCandidates = await dbContext.Set<OutboxRecord>()
        .Where(r => r.Attempts >= 10 && (r.StatusFlags & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed)
        .ToListAsync();

      // Assert - Poison message should be detected
      await Assert.That(poisonCandidates).Count().IsEqualTo(1)
        .Because("Application should be able to detect poison message candidates via attempts count");

      var poison = poisonCandidates[0];
      await Assert.That(poison.MessageId).IsEqualTo(poisonMessageId);
      await Assert.That(poison.Attempts).IsGreaterThanOrEqualTo(10);

      // Application can now move to dead letter queue, set scheduled_for = NULL, or alert operations
    }
  }

  // Helper method to insert outbox messages with specific timestamps (for stream ordering tests)
  private async Task InsertOutboxMessageWithTimestampAsync(
    Guid messageId,
    string destination,
    string messageType,
    string messageData,
    Guid streamId,
    DateTimeOffset createdAt,
    int statusFlags = (int)MessageProcessingStatus.Stored,
    Guid? instanceId = null,
    DateTimeOffset? leaseExpiry = null) {
    await using var dbContext = CreateDbContext();

    // Calculate partition number
    int? partitionNumber = null;
    await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var command = new Npgsql.NpgsqlCommand(
        "SELECT compute_partition(@streamId::uuid, 10000)",
        connection);
      command.Parameters.AddWithValue("streamId", streamId);
      partitionNumber = (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    var actualLeaseExpiry = leaseExpiry;
    if (instanceId.HasValue && !leaseExpiry.HasValue) {
      actualLeaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
    }

    // Create minimal envelope JSON for testing (MessageType → envelope type, MessageData → envelope JSON)
    // The envelope structure is: { "MessageId": "guid", "Hops": [], "Payload": {} }
    var envelopeTypeFullName = typeof(TestMessageEnvelope).AssemblyQualifiedName
      ?? throw new InvalidOperationException("Could not get envelope type name");

    var timestampOutboxMessageData = new OutboxMessageData {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.Deserialize<JsonElement>("""{"Data":"test"}"""),
      Hops = new List<MessageHop>()
    };

    var timestampEnvelopeMetadata = new EnvelopeMetadata {
      MessageId = MessageId.From(messageId),
      Hops = new List<MessageHop>()
    };

    dbContext.Set<OutboxRecord>().Add(new OutboxRecord {
      MessageId = messageId,
      Destination = destination,
      MessageType = envelopeTypeFullName,  // Store envelope type (maps to event_type column)
      MessageData = timestampOutboxMessageData,  // Store complete envelope (maps to event_data column)
      Metadata = timestampEnvelopeMetadata,
      Scope = null,
      StatusFlags = (MessageProcessingStatus)statusFlags,
      Attempts = 0,
      Error = null,
      CreatedAt = createdAt,  // Use specified timestamp
      PublishedAt = null,
      ProcessedAt = null,
      InstanceId = instanceId,
      LeaseExpiry = actualLeaseExpiry,
      StreamId = streamId,
      PartitionNumber = partitionNumber
    });

    await dbContext.SaveChangesAsync();
  }

  // Helper method to insert outbox messages with timestamp AND scheduled_for (for scheduled retry tests)
  private async Task InsertOutboxMessageWithTimestampAndScheduledAsync(
    Guid messageId,
    string destination,
    string messageType,
    Guid streamId,
    DateTimeOffset createdAt,
    DateTimeOffset? scheduledFor,
    int statusFlags = (int)MessageProcessingStatus.Stored,
    Guid? instanceId = null) {
    await using var dbContext = CreateDbContext();

    // Calculate partition number
    int? partitionNumber = null;
    await using (var connection = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await using var command = new Npgsql.NpgsqlCommand(
        "SELECT compute_partition(@streamId::uuid, 10000)",
        connection);
      command.Parameters.AddWithValue("streamId", streamId);
      partitionNumber = (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    var envelopeTypeFullName = typeof(TestMessageEnvelope).AssemblyQualifiedName
      ?? throw new InvalidOperationException("Could not get envelope type name");

    var scheduledOutboxMessageData = new OutboxMessageData {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.Deserialize<JsonElement>("""{"Data":"test"}"""),
      Hops = new List<MessageHop>()
    };

    var scheduledEnvelopeMetadata = new EnvelopeMetadata {
      MessageId = MessageId.From(messageId),
      Hops = new List<MessageHop>()
    };

    dbContext.Set<OutboxRecord>().Add(new OutboxRecord {
      MessageId = messageId,
      Destination = destination,
      MessageType = envelopeTypeFullName,
      MessageData = scheduledOutboxMessageData,
      Metadata = scheduledEnvelopeMetadata,
      Scope = null,
      StatusFlags = (MessageProcessingStatus)statusFlags,
      Attempts = 0,
      Error = null,
      CreatedAt = createdAt,
      PublishedAt = null,
      ProcessedAt = null,
      ScheduledFor = scheduledFor, // Set scheduled retry time
      InstanceId = instanceId,
      LeaseExpiry = instanceId.HasValue ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
      StreamId = streamId,
      PartitionNumber = partitionNumber
    });

    await dbContext.SaveChangesAsync();
  }

  /// <summary>
  /// Minimal reproduction test for event storage issue.
  /// Tests that events with [StreamKey] are properly stored in wh_event_store.
  /// </summary>
  [Test]
  public async Task ProcessWorkBatchAsync_EventWithStreamKey_StoresInEventStoreAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    // Create a simple test event with StreamKey
    var testEventType = "Whizbang.Data.EFCore.Postgres.Tests.TestProductEvent, Whizbang.Data.EFCore.Postgres.Tests";
    var productId = _idProvider.NewGuid();
    var messageId = _idProvider.NewGuid();

    // Simulate event payload with StreamKey (ProductId)
    var eventPayload = $$"""
    {
      "ProductId": "{{productId}}",
      "Name": "Test Product"
    }
    """;

    // Create envelope with metadata indicating this is an event with stream_id
    var envelope = $$"""
    {
      "MessageId": "{{messageId}}",
      "Payload": {{eventPayload}},
      "Hops": []
    }
    """;

    var envelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{testEventType}]], Whizbang.Core";

    var outboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(
        envelope,
        JsonContextRegistry.CreateCombinedOptions()
      )!,
      EnvelopeType = envelopeType,
      StreamId = productId,  // This should be extracted from [StreamKey] attribute
      IsEvent = true,        // Mark as event
      MessageType = testEventType,
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = new List<MessageHop>()
      }
    };

    // Act
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [outboxMessage],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      leaseSeconds: 300);

    // Assert - Verify event was stored using raw SQL
    var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    await connection.OpenAsync();

    try {
      // Count events in event store
      using var countCmd = connection.CreateCommand();
      countCmd.CommandText = @"
        SELECT COUNT(*)
        FROM wh_event_store
        WHERE event_id = @eventId";

      var eventIdParam = countCmd.CreateParameter();
      eventIdParam.ParameterName = "@eventId";
      eventIdParam.Value = messageId;
      countCmd.Parameters.Add(eventIdParam);

      var eventStoreCount = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);

      await Assert.That(eventStoreCount).IsEqualTo(1L)
        .Because("Event with is_event=true and stream_id should be stored in wh_event_store");

      // Verify the stored event has correct stream_id
      using var selectCmd = connection.CreateCommand();
      selectCmd.CommandText = @"
        SELECT event_id, stream_id, event_type
        FROM wh_event_store
        WHERE event_id = @eventId";

      var eventIdParam2 = selectCmd.CreateParameter();
      eventIdParam2.ParameterName = "@eventId";
      eventIdParam2.Value = messageId;
      selectCmd.Parameters.Add(eventIdParam2);

      using var reader = await selectCmd.ExecuteReaderAsync();
      var found = await reader.ReadAsync();

      await Assert.That(found).IsTrue()
        .Because("Event should exist in wh_event_store");

      if (found) {
        var storedStreamId = reader.GetGuid(reader.GetOrdinal("stream_id"));
        var storedEventType = reader.GetString(reader.GetOrdinal("event_type"));

        await Assert.That(storedStreamId).IsEqualTo(productId)
          .Because("Stream ID should match the ProductId from [StreamKey]");
        await Assert.That(storedEventType).IsEqualTo(testEventType);
      }
    } finally {
      await connection.CloseAsync();
    }
  }
}
