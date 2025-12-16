using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// JSON source generation context for test envelope types.
/// Required for AOT serialization/deserialization of test envelopes.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MessageEnvelope<DapperWorkCoordinatorTests.TestEvent>))]
[JsonSerializable(typeof(DapperWorkCoordinatorTests.TestEvent))]
internal partial class TestEnvelopeJsonContext : JsonSerializerContext { }

/// <summary>
/// Integration tests for DapperWorkCoordinator.
/// Tests the process_work_batch PostgreSQL function and lease-based work coordination.
/// Uses UUIDv7 for all message IDs to ensure proper time-ordered database indexing.
/// </summary>
public class DapperWorkCoordinatorTests : PostgresTestBase {
  private DapperWorkCoordinator _sut = null!;
  private Guid _instanceId;
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();
  private static readonly JsonSerializerOptions _jsonOptions;

  static DapperWorkCoordinatorTests() {
    // Combine all JSON contexts including test-specific ones
    var baseOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    _jsonOptions = new JsonSerializerOptions(baseOptions) {
      TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
        baseOptions.TypeInfoResolver!,
        TestEnvelopeJsonContext.Default
      )
    };
  }

  // Test helper record and envelope creation
  public record TestEvent { }

  private static IMessageEnvelope<JsonElement> CreateTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,  // Empty JSON object for testing
      Hops = []
    };
    return envelope;
  }

  [Before(Test)]
  public async Task TestSetupAsync() {
    _instanceId = _idProvider.NewGuid();
    // Get connection string from base class (before any connections are opened)
    _sut = new DapperWorkCoordinator(ConnectionString, _jsonOptions);
    await Task.CompletedTask;  // Keep async signature for consistency
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
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
      outboxCompletions: [
        new MessageCompletion { MessageId = messageId1, Status = MessageProcessingStatus.Published },
        new MessageCompletion { MessageId = messageId2, Status = MessageProcessingStatus.Published }
      ],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

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
      outboxCompletions: [],
      outboxFailures: [
        new MessageFailure {
          MessageId = messageId,
          CompletedStatus = MessageProcessingStatus.Stored,  // What succeeded before failure
          Error = "Network timeout"
        }
      ],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(0);

    // Verify message marked as Failed with error
    var status = await GetOutboxStatusAsync(messageId);
    var error = await GetOutboxErrorAsync(messageId);
    await Assert.That(status).IsEqualTo("Failed");
    await Assert.That(error).Contains("Network timeout");
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
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [
        new MessageCompletion { MessageId = messageId1, Status = MessageProcessingStatus.EventStored },
        new MessageCompletion { MessageId = messageId2, Status = MessageProcessingStatus.EventStored }
      ],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.InboxWork).HasCount().EqualTo(0);

    // Verify messages deleted (FullyCompleted messages are deleted in non-debug mode)
    var status1 = await GetInboxStatusAsync(messageId1);
    var status2 = await GetInboxStatusAsync(messageId2);
    await Assert.That(status1).IsNull()
      .Because("Fully completed messages should be deleted from inbox");
    await Assert.That(status2).IsNull()
      .Because("Fully completed messages should be deleted from inbox");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedWithErrorAsync() {
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
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [
        new MessageFailure {
          MessageId = messageId,
          CompletedStatus = MessageProcessingStatus.Stored,  // What succeeded before failure
          Error = "Handler exception"
        }
      ],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.InboxWork).HasCount().EqualTo(0);

    // Verify message marked as Failed with error
    var status = await GetInboxStatusAsync(messageId);
    var error = await GetInboxErrorAsync(messageId);
    await Assert.That(status).IsEqualTo("Failed");
    await Assert.That(error).Contains("Handler exception");
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
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10),
      streamId: _idProvider.NewGuid());

    await InsertOutboxMessageAsync(
      orphanedId2,
      "topic2",
      "OrphanedEvent2",
      "{\"data\":2}",
      status: "Publishing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-5),
      streamId: _idProvider.NewGuid());

    // Active message (not expired)
    await InsertOutboxMessageAsync(
      activeId,
      "topic3",
      "ActiveEvent",
      "{\"data\":3}",
      status: "Publishing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(5),
      streamId: _idProvider.NewGuid());

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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Should return 2 work items, not the active one
    await Assert.That(result.OutboxWork).HasCount().EqualTo(2);
    await Assert.That(result.InboxWork).HasCount().EqualTo(0);

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
      status: "Processing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10),
      streamId: _idProvider.NewGuid());

    await InsertInboxMessageAsync(
      orphanedId2,
      "Handler2",
      "OrphanedEvent2",
      "{\"data\":2}",
      status: "Processing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-5),
      streamId: _idProvider.NewGuid());

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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(0);
    await Assert.That(result.InboxWork).HasCount().EqualTo(2);

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
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10),
      streamId: _idProvider.NewGuid());
    await InsertInboxMessageAsync(
      orphanedInboxId,
      "Handler3",
      "OrphanedEvent2",
      "{}",
      status: "Processing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10),
      streamId: _idProvider.NewGuid());

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
        new MessageCompletion { MessageId = completedInboxId, Status = MessageProcessingStatus.EventStored }
      ],
      inboxFailures: [
        new MessageFailure {
          MessageId = failedInboxId,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Inbox error"
        }
      ],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(1);
    await Assert.That(result.InboxWork).HasCount().EqualTo(1);

    // Verify completed
    await Assert.That(await GetOutboxStatusAsync(completedOutboxId)).IsEqualTo("Published");
    await Assert.That(await GetInboxStatusAsync(completedInboxId)).IsNull()
      .Because("Fully completed inbox messages should be deleted");

    // Verify failed
    await Assert.That(await GetOutboxStatusAsync(failedOutboxId)).IsEqualTo("Failed");
    await Assert.That(await GetInboxStatusAsync(failedInboxId)).IsEqualTo("Failed");

    // Verify work returned and claimed
    await Assert.That(result.OutboxWork[0].MessageId).IsEqualTo(orphanedOutboxId);
    await Assert.That(result.InboxWork[0].MessageId).IsEqualTo(orphanedInboxId);
    await Assert.That(await GetOutboxInstanceIdAsync(orphanedOutboxId)).IsEqualTo(_instanceId);
    await Assert.That(await GetInboxInstanceIdAsync(orphanedInboxId)).IsEqualTo(_instanceId);
  }

  // ========================================
  // Priority 1 Tests: New Message Storage
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_NewOutboxMessage_StoresAndReturnsImmediatelyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    var newOutboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Message should be stored AND returned for immediate processing
    await Assert.That(result.OutboxWork).HasCount().EqualTo(1);
    var work = result.OutboxWork[0];
    await Assert.That(work.MessageId).IsEqualTo(messageId);
    await Assert.That(work.Destination).IsEqualTo("test-topic");
    await Assert.That(work.StreamId).IsEqualTo(streamId);

    // Verify message stored in database
    var dbStatus = await GetOutboxStatusAsync(messageId);
    await Assert.That(dbStatus).IsEqualTo("Pending");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewInboxMessage_StoresWithDeduplicationAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newInboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act - First call should store and return work
    var result1 = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Act - Second call with same message ID should return empty (duplicate)
    var result2 = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - First call returns work
    await Assert.That(result1.InboxWork).HasCount().EqualTo(1);
    await Assert.That(result1.InboxWork[0].MessageId).IsEqualTo(messageId);

    // Assert - Second call returns empty (duplicate detected via INSERT ... ON CONFLICT DO NOTHING)
    await Assert.That(result2.InboxWork).HasCount().EqualTo(0);

    // Verify only one message in database
    var count = await CountInboxMessagesAsync(messageId);
    await Assert.That(count).IsEqualTo(1);
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewInboxMessage_WithStreamId_AssignsPartitionAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var streamId = _idProvider.NewGuid();

    var messageId = _idProvider.NewGuid();
    var newInboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.InboxWork).HasCount().EqualTo(1);
    var work = result.InboxWork[0];

    // Verify partition was assigned via consistent hashing
    await Assert.That(work.PartitionNumber).IsNotNull();
    await Assert.That(work.PartitionNumber!.Value).IsGreaterThanOrEqualTo(0);
    await Assert.That(work.PartitionNumber!.Value).IsLessThanOrEqualTo(9999);

    // Verify sequence_order was set
    await Assert.That(work.SequenceOrder).IsGreaterThan(0);
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewOutboxMessage_WithStreamId_AssignsPartitionAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var streamId = _idProvider.NewGuid();

    var messageId = _idProvider.NewGuid();
    var newOutboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert
    await Assert.That(result.OutboxWork).HasCount().EqualTo(1);
    var work = result.OutboxWork[0];

    // Verify partition was assigned via consistent hashing
    await Assert.That(work.PartitionNumber).IsNotNull();
    await Assert.That(work.PartitionNumber!.Value).IsGreaterThanOrEqualTo(0);
    await Assert.That(work.PartitionNumber!.Value).IsLessThanOrEqualTo(9999);

    // Verify sequence_order was set
    await Assert.That(work.SequenceOrder).IsGreaterThan(0);
  }

  // ========================================
  // Priority 1 Tests: Event Store Integration
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_WithEventOutbox_PersistsToEventStoreAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    var newOutboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act - Create new outbox message with IsEvent=true
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Event should be persisted to event store
    var eventVersion = await GetEventStoreVersionAsync(streamId, expectedVersion: 1);
    await Assert.That(eventVersion).IsNotNull()
      .Because("Creating a new event outbox message should persist it to wh_event_store");
    await Assert.That(eventVersion!.Value).IsEqualTo(1)
      .Because("First event in stream should have version 1");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_WithEventInbox_PersistsToEventStoreAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    var newInboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act - Create new inbox message with IsEvent=true
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Event should be persisted to event store
    var eventVersion = await GetEventStoreVersionAsync(streamId, expectedVersion: 1);
    await Assert.That(eventVersion).IsNotNull()
      .Because("Creating a new event inbox message should persist it to wh_event_store");
    await Assert.That(eventVersion!.Value).IsEqualTo(1)
      .Because("First event in stream should have version 1");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_EventVersionConflict_HandlesOptimisticConcurrencyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var streamId = _idProvider.NewGuid();
    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();

    // Insert first event already in event store (simulating existing event at version 1)
    await InsertEventStoreRecordAsync(streamId, messageId1, "TestEvent", "{}", version: 1);

    var newInboxMessage = new InboxMessage {
      MessageId = messageId2,
      HandlerName = "TestHandler",
      Envelope = CreateTestEnvelope(messageId2),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act - Try to create new inbox message (should handle version conflict)
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Should handle optimistic concurrency (sequential versioning)
    var event1 = await GetEventStoreVersionAsync(streamId, expectedVersion: 1);
    await Assert.That(event1).IsEqualTo(1)
      .Because("First event should remain at version 1");

    // Second event should get version 2 (sequential versioning)
    var event2 = await GetEventStoreVersionAsync(streamId, expectedVersion: 2);
    await Assert.That(event2).IsNotNull()
      .Because("New event should be persisted with next sequential version");
    await Assert.That(event2!.Value).IsEqualTo(2)
      .Because("Second event in stream should have version 2");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_MultipleEventsInStream_IncrementsVersionAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var streamId = _idProvider.NewGuid();
    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();
    var messageId3 = _idProvider.NewGuid();

    var newOutboxMessages = new[] {
      new OutboxMessage {
        MessageId = messageId1,
        Destination = "topic1",
        Envelope = CreateTestEnvelope(messageId1),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
        StreamId = streamId,
        IsEvent = true,
        MessageType = "TestMessage, TestAssembly"
      },
      new OutboxMessage {
        MessageId = messageId2,
        Destination = "topic1",
        Envelope = CreateTestEnvelope(messageId2),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
        StreamId = streamId,
        IsEvent = true,
        MessageType = "TestMessage, TestAssembly"
      },
      new OutboxMessage {
        MessageId = messageId3,
        Destination = "topic1",
        Envelope = CreateTestEnvelope(messageId3),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
        StreamId = streamId,
        IsEvent = true,
        MessageType = "TestMessage, TestAssembly"
      }
    };

    // Act - Create all three messages in a single batch
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: newOutboxMessages,
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Events should have sequential versions
    var version1 = await GetEventStoreVersionAsync(streamId, expectedVersion: 1);
    var version2 = await GetEventStoreVersionAsync(streamId, expectedVersion: 2);
    var version3 = await GetEventStoreVersionAsync(streamId, expectedVersion: 3);

    await Assert.That(version1).IsEqualTo(1);
    await Assert.That(version2).IsEqualTo(2);
    await Assert.That(version3).IsEqualTo(3);
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NonEvent_DoesNotPersistToEventStoreAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    var newOutboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = streamId,
      IsEvent = false,  // CRITICAL: IsEvent = false (command, not event)
      MessageType = "TestMessage, TestAssembly"
    };

    // Act - Create new outbox message with IsEvent=false
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Non-event should NOT be persisted to event store
    var eventVersion = await GetEventStoreVersionAsync(streamId, expectedVersion: 1);
    await Assert.That(eventVersion).IsNull()
      .Because("Non-events (IsEvent=false) should not be persisted to wh_event_store");

    // Verify outbox message was still stored (just not in event store)
    var outboxStatus = await GetOutboxStatusAsync(messageId);
    await Assert.That(outboxStatus).IsEqualTo("Pending")
      .Because("Non-event messages should still be stored in outbox");
  }

  // ========================================
  // Priority 2 Tests: Partition Distribution
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_ConsistentHashing_SameStreamSamePartitionAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var streamId = _idProvider.NewGuid();

    // Act - Insert 10 messages with same stream_id
    var messageIds = new List<Guid>();
    for (int i = 0; i < 10; i++) {
      var messageId = _idProvider.NewGuid();
      messageIds.Add(messageId);

      var newOutboxMessage = new OutboxMessage {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = CreateTestEnvelope(messageId),
        EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
        StreamId = streamId,  // SAME stream_id for all
        IsEvent = true,
        MessageType = "TestMessage, TestAssembly"
      };

      await _sut.ProcessWorkBatchAsync(
        _instanceId,
        "TestService",
        "test-host",
        12345,
        metadata: null,
        outboxCompletions: [],
        outboxFailures: [],
        inboxCompletions: [],
        inboxFailures: [],
        [],  // receptorCompletions
        [],  // receptorFailures
        [],  // perspectiveCompletions
        [],  // perspectiveFailures
        newOutboxMessages: [newOutboxMessage],
        newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);
    }

    // Assert - All messages should have same partition_number
    var partitions = new HashSet<int>();
    foreach (var messageId in messageIds) {
      var partition = await GetOutboxPartitionNumberAsync(messageId);
      await Assert.That(partition).IsNotNull()
        .Because("Messages with stream_id should have partition assigned");
      partitions.Add(partition!.Value);
    }

    await Assert.That(partitions).HasCount().EqualTo(1)
      .Because("All messages from same stream should map to same partition via consistent hashing");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_PartitionAssignment_WithinRangeAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);

    // Act - Insert messages with various stream_ids
    var messageIds = new List<Guid>();
    for (int i = 0; i < 20; i++) {
      var messageId = _idProvider.NewGuid();
      var streamId = _idProvider.NewGuid();  // Different stream_id each time
      messageIds.Add(messageId);

      var newOutboxMessage = new OutboxMessage {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = CreateTestEnvelope(messageId),
        EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
        StreamId = streamId,
        IsEvent = true,
        MessageType = "TestMessage, TestAssembly"
      };

      await _sut.ProcessWorkBatchAsync(
        _instanceId,
        "TestService",
        "test-host",
        12345,
        metadata: null,
        outboxCompletions: [],
        outboxFailures: [],
        inboxCompletions: [],
        inboxFailures: [],
        [],  // receptorCompletions
        [],  // receptorFailures
        [],  // perspectiveCompletions
        [],  // perspectiveFailures
        newOutboxMessages: [newOutboxMessage],
        newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);
    }

    // Assert - All partition_numbers in range 0-9999
    foreach (var messageId in messageIds) {
      var partition = await GetOutboxPartitionNumberAsync(messageId);
      await Assert.That(partition).IsNotNull();
      await Assert.That(partition!.Value).IsGreaterThanOrEqualTo(0);
      await Assert.That(partition!.Value).IsLessThanOrEqualTo(9999)
        .Because("Partition numbers must be in range 0-9999");
    }
  }

  [Test]
  public async Task ProcessWorkBatchAsync_LoadBalancing_DistributesAcrossInstancesAsync() {
    // Arrange - Create 3 service instances
    var instance1 = _idProvider.NewGuid();
    var instance2 = _idProvider.NewGuid();
    var instance3 = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instance1, "Service1", "host1", 1);
    await InsertServiceInstanceAsync(instance2, "Service2", "host2", 2);
    await InsertServiceInstanceAsync(instance3, "Service3", "host3", 3);

    // Insert 30 messages (30 different streams)
    for (int i = 0; i < 30; i++) {
      var messageId = _idProvider.NewGuid();
      var streamId = _idProvider.NewGuid();

      await InsertOutboxMessageAsync(
        messageId,
        "test-topic",
        "TestEvent",
        "{}",
        status: "Pending",
        instanceId: null,
        leaseExpiry: null,
        streamId: streamId,
        isEvent: true);
    }

    // Act - Each instance claims work
    var result1 = await _sut.ProcessWorkBatchAsync(
      instance1, "Service1", "host1", 1,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      maxPartitionsPerInstance: 10);

    var result2 = await _sut.ProcessWorkBatchAsync(
      instance2, "Service2", "host2", 2,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      maxPartitionsPerInstance: 10);

    var result3 = await _sut.ProcessWorkBatchAsync(
      instance3, "Service3", "host3", 3,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      maxPartitionsPerInstance: 10);

    // Assert - Work distributed across instances
    // Note: With random stream IDs hashing to partition numbers and modulo-based distribution,
    // not all messages may be claimed if maxPartitionsPerInstance limits are reached.
    // The key test is that work IS distributed across multiple instances.
    var totalWork = result1.OutboxWork.Count + result2.OutboxWork.Count + result3.OutboxWork.Count;
    await Assert.That(totalWork).IsGreaterThan(20)
      .Because("Most messages should be claimed across instances (hash distribution may leave some unclaimed)");

    // Each instance should claim some work (not all to one instance)
    await Assert.That(result1.OutboxWork.Count).IsGreaterThan(0)
      .Because("Instance 1 should claim some partitions");
    await Assert.That(result2.OutboxWork.Count).IsGreaterThan(0)
      .Because("Instance 2 should claim some partitions");
    await Assert.That(result3.OutboxWork.Count).IsGreaterThan(0)
      .Because("Instance 3 should claim some partitions");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_InstanceFailover_RedistributesPartitionsAsync() {
    // Arrange - Instance A claims partitions
    var instanceA = _idProvider.NewGuid();
    var instanceB = _idProvider.NewGuid();

    await InsertServiceInstanceAsync(instanceA, "ServiceA", "hostA", 1);
    await InsertServiceInstanceAsync(instanceB, "ServiceB", "hostB", 2);

    // Insert messages
    var messageIds = new List<Guid>();
    for (int i = 0; i < 10; i++) {
      var messageId = _idProvider.NewGuid();
      messageIds.Add(messageId);

      await InsertOutboxMessageAsync(
        messageId,
        "test-topic",
        "TestEvent",
        "{}",
        status: "Pending",
        instanceId: null,
        leaseExpiry: null,
        streamId: _idProvider.NewGuid(),
        isEvent: true);
    }

    // Instance A claims work
    var resultA = await _sut.ProcessWorkBatchAsync(
      instanceA, "ServiceA", "hostA", 1,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Note: With random stream IDs and hash-based partition distribution,
    // Instance A may not claim all 10 messages initially
    await Assert.That(resultA.OutboxWork.Count).IsGreaterThan(0)
      .Because("Instance A should claim at least some work initially");

    var initialWorkCount = resultA.OutboxWork.Count;

    // Act - Mark Instance A as stale (simulate failure)
    await MarkInstanceHeartbeatOldAsync(instanceA, DateTimeOffset.UtcNow.AddHours(-2));

    // Mark all messages as having expired leases
    foreach (var messageId in messageIds) {
      await UpdateOutboxLeaseExpiryAsync(messageId, DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    // Instance B calls ProcessWorkBatchAsync
    var resultB = await _sut.ProcessWorkBatchAsync(
      instanceB, "ServiceB", "hostB", 2,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Instance B claims the orphaned partitions that Instance A had claimed
    await Assert.That(resultB.OutboxWork.Count).IsGreaterThan(0)
      .Because("Instance B should claim orphaned work from failed Instance A");

    // Verify that Instance B now owns at least some of the messages that Instance A previously claimed
    var instanceBOwnedCount = 0;
    foreach (var messageId in messageIds) {
      var currentInstance = await GetOutboxInstanceIdAsync(messageId);
      if (currentInstance == instanceB) {
        instanceBOwnedCount++;
      }
    }

    await Assert.That(instanceBOwnedCount).IsGreaterThan(0)
      .Because("Failed instance's work should be redistributed to active instance");
  }

  // ========================================
  // Priority 2 Tests: Granular Status Tracking
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_StatusFlags_AccumulateCorrectlyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertOutboxMessageAsync(messageId, "test-topic", "TestEvent", "{}", status: "Publishing", instanceId: _instanceId);

    // Act 1 - Complete with Stored status
    await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = messageId, Status = MessageProcessingStatus.Stored }
      ],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Verify status after first completion
    var status1 = await GetOutboxStatusFlagsAsync(messageId);
    await Assert.That((status1 & MessageProcessingStatus.Stored) == MessageProcessingStatus.Stored).IsTrue()
      .Because("Status should include Stored flag");

    // Act 2 - Complete with Published status (simulating next stage)
    await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = messageId, Status = MessageProcessingStatus.Published }
      ],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Status should accumulate (bitwise OR)
    var status2 = await GetOutboxStatusFlagsAsync(messageId);
    await Assert.That((status2 & MessageProcessingStatus.Stored) == MessageProcessingStatus.Stored).IsTrue()
      .Because("Status should retain Stored flag");
    await Assert.That((status2 & MessageProcessingStatus.Published) == MessageProcessingStatus.Published).IsTrue()
      .Because("Status should include Published flag");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_PartialCompletion_TracksCorrectlyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    await InsertInboxMessageAsync(
      messageId,
      "TestHandler",
      "TestEvent",
      "{}",
      status: "Processing",
      instanceId: _instanceId,
      streamId: _idProvider.NewGuid(),
      isEvent: true);

    // Act - Fail message with CompletedStatus = Stored | EventStored
    var partialStatus = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored;
    await _sut.ProcessWorkBatchAsync(
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
          CompletedStatus = partialStatus,
          Error = "Failed at receptor processing"
        }
      ],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Database should reflect partial completion
    var status = await GetInboxStatusFlagsAsync(messageId);
    await Assert.That((status & MessageProcessingStatus.Stored) == MessageProcessingStatus.Stored).IsTrue()
      .Because("Partial completion should include Stored flag");
    await Assert.That((status & MessageProcessingStatus.EventStored) == MessageProcessingStatus.EventStored).IsTrue()
      .Because("Partial completion should include EventStored flag");

    var dbStatus = await GetInboxStatusAsync(messageId);
    await Assert.That(dbStatus).IsEqualTo("Failed")
      .Because("Overall status should be Failed");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_WorkBatchFlags_SetCorrectlyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var newMessageId = _idProvider.NewGuid();
    var orphanedMessageId = _idProvider.NewGuid();

    // Insert orphaned message (expired lease)
    await InsertOutboxMessageAsync(
      orphanedMessageId,
      "test-topic",
      "OrphanedEvent",
      "{}",
      status: "Publishing",
      instanceId: _idProvider.NewGuid(),
      leaseExpiry: DateTimeOffset.UtcNow.AddMinutes(-10),
      streamId: _idProvider.NewGuid(),
      isEvent: true);

    var newOutboxMessage = new OutboxMessage {
      MessageId = newMessageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(newMessageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Check flags for both messages
    var newMessage = result.OutboxWork.FirstOrDefault(w => w.MessageId == newMessageId);
    var orphanedMessage = result.OutboxWork.FirstOrDefault(w => w.MessageId == orphanedMessageId);

    await Assert.That(newMessage).IsNotNull()
      .Because("Newly stored message should be returned");
    await Assert.That(orphanedMessage).IsNotNull()
      .Because("Orphaned message should be returned");

    await Assert.That((newMessage!.Flags & WorkBatchFlags.NewlyStored) == WorkBatchFlags.NewlyStored).IsTrue()
      .Because("Newly stored message should have NewlyStored flag");

    await Assert.That((orphanedMessage!.Flags & WorkBatchFlags.Orphaned) == WorkBatchFlags.Orphaned).IsTrue()
      .Because("Orphaned message should have Orphaned flag");
  }

  // ========================================
  // Priority 3 Tests: Stale Instance Cleanup
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_StaleInstances_CleanedUpAsync() {
    // Arrange
    var staleInstanceId = _idProvider.NewGuid();
    var activeInstanceId = _instanceId;

    // Insert stale instance (heartbeat > 300 seconds old, default staleThresholdSeconds)
    await InsertServiceInstanceAsync(staleInstanceId, "StaleService", "stale-host", 999);
    await MarkInstanceHeartbeatOldAsync(staleInstanceId, DateTimeOffset.UtcNow.AddSeconds(-400));

    // Insert active instance
    await InsertServiceInstanceAsync(activeInstanceId, "ActiveService", "active-host", 123);

    // Act - Active instance calls ProcessWorkBatchAsync (triggers cleanup)
    var result = await _sut.ProcessWorkBatchAsync(
      activeInstanceId,
      "ActiveService",
      "active-host",
      123,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      staleThresholdSeconds: 300);

    // Assert - Stale instance should be deleted
    var staleExists = await ServiceInstanceExistsAsync(staleInstanceId);
    var activeExists = await ServiceInstanceExistsAsync(activeInstanceId);

    await Assert.That(staleExists).IsFalse()
      .Because("Instance with heartbeat older than staleThresholdSeconds should be deleted");
    await Assert.That(activeExists).IsTrue()
      .Because("Active instance should remain");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_ActiveInstances_NotCleanedAsync() {
    // Arrange
    var instance1 = _idProvider.NewGuid();
    var instance2 = _idProvider.NewGuid();

    // Insert two instances with recent heartbeats
    await InsertServiceInstanceAsync(instance1, "Service1", "host1", 111);
    await InsertServiceInstanceAsync(instance2, "Service2", "host2", 222);

    // Act - Instance 1 calls ProcessWorkBatchAsync
    var result = await _sut.ProcessWorkBatchAsync(
      instance1,
      "Service1",
      "host1",
      111,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      staleThresholdSeconds: 300);

    // Assert - Both instances should still exist
    var exists1 = await ServiceInstanceExistsAsync(instance1);
    var exists2 = await ServiceInstanceExistsAsync(instance2);

    await Assert.That(exists1).IsTrue()
      .Because("Active instance 1 should not be deleted");
    await Assert.That(exists2).IsTrue()
      .Because("Active instance 2 with recent heartbeat should not be deleted");
  }

  // ========================================
  // Priority 1 Tests: IsEvent Serialization
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventTrue_StoresIsEventFlagAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newOutboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,  // CRITICAL: IsEvent = true
      MessageType = "TestMessage, TestAssembly"
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Verify is_event flag is stored correctly
    var isEvent = await GetOutboxIsEventAsync(messageId);
    await Assert.That(isEvent).IsTrue()
      .Because("OutboxMessage with IsEvent=true should persist is_event=true to wh_outbox");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventFalse_StoresIsEventFlagAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newOutboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = _idProvider.NewGuid(),
      IsEvent = false,  // CRITICAL: IsEvent
      MessageType = "TestMessage, TestAssembly"
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Verify is_event flag is stored correctly
    var isEvent = await GetOutboxIsEventAsync(messageId);
    await Assert.That(isEvent).IsFalse()
      .Because("OutboxMessage with IsEvent=false should persist is_event=false to wh_outbox");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewInboxMessage_WithIsEventTrue_StoresIsEventFlagAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newInboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,  // CRITICAL: IsEvent
      MessageType = "TestMessage, TestAssembly"
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Verify is_event flag is stored correctly
    var isEvent = await GetInboxIsEventAsync(messageId);
    await Assert.That(isEvent).IsTrue()
      .Because("InboxMessage with IsEvent=true should persist is_event=true to wh_inbox");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewInboxMessage_WithIsEventFalse_StoresIsEventFlagAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newInboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = CreateTestEnvelope(messageId),
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      StreamId = _idProvider.NewGuid(),
      IsEvent = false,  // CRITICAL: IsEvent
      MessageType = "TestMessage, TestAssembly"
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
      [],  // receptorCompletions
      [],  // receptorFailures
      [],  // perspectiveCompletions
      [],  // perspectiveFailures
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []);

    // Assert - Verify is_event flag is stored correctly
    var isEvent = await GetInboxIsEventAsync(messageId);
    await Assert.That(isEvent).IsFalse()
      .Because("InboxMessage with IsEvent=false should persist is_event=false to wh_inbox");
  }

  // Helper methods for test data setup and verification

  private async Task InsertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var now = DateTimeOffset.UtcNow;
    await connection.ExecuteAsync(@"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at, metadata)
      VALUES (@instanceId, @serviceName, @hostName, @processId, @now, @now, NULL)",
      new { instanceId, serviceName, hostName, processId, now });
  }

  private async Task<DateTimeOffset?> GetInstanceHeartbeatAsync(Guid instanceId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<DateTimeOffset?>(@"
      SELECT last_heartbeat_at FROM wh_service_instances WHERE instance_id = @instanceId",
      new { instanceId });
  }

  private async Task InsertOutboxMessageAsync(
    Guid messageId,
    string destination,
    string messageType,
    string messageData,
    string status = "Pending",
    Guid? instanceId = null,
    DateTimeOffset? leaseExpiry = null,
    Guid? streamId = null,
    bool isEvent = false) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Map status string to MessageProcessingStatus flags
    var statusFlags = status switch {
      "Pending" => 1,  // Stored
      "Publishing" => 1,  // Stored
      "Published" => 5,  // Stored | Published
      "Failed" => 32769,  // Stored | Failed
      _ => 1  // Default to Stored
    };

    // Add EventStored flag if isEvent=true
    if (isEvent) {
      statusFlags |= 2;  // Add EventStored bit
    }

    // Create envelope for storage (to match what ProcessWorkBatchAsync expects)
    // Manual JSON construction for test data (avoids AOT serialization complexities)
    var envelopeType = typeof(MessageEnvelope<TestEvent>).AssemblyQualifiedName!;
    var envelopeJson = $$"""
    {
      "$type": "{{envelopeType}}",
      "MessageId": "{{messageId}}",
      "Payload": {},
      "Hops": []
    }
    """;

    await connection.ExecuteAsync(@"
      INSERT INTO wh_outbox (
        message_id, destination, event_type, event_data, metadata, scope,
        status, attempts, error, created_at, published_at,
        instance_id, lease_expiry, stream_id, partition_number
      ) VALUES (
        @messageId, @destination, @envelopeType, @envelopeJson::jsonb, '{}'::jsonb, NULL,
        @statusFlags, 0, NULL, @now, NULL,
        @instanceId, @leaseExpiry, @streamId,
        CASE WHEN @streamId IS NULL THEN NULL ELSE compute_partition(@streamId::uuid, 10000) END
      )",
      new {
        messageId,
        destination,
        envelopeType,
        envelopeJson,
        statusFlags,
        instanceId,
        leaseExpiry,
        streamId,
        now = DateTimeOffset.UtcNow
      });
  }

  private async Task<string?> GetOutboxStatusAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var statusFlags = await connection.QueryFirstOrDefaultAsync<int?>(@"
      SELECT status FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });

    if (statusFlags == null) {
      return null;
    }

    // Convert status flags to human-readable string
    var status = (MessageProcessingStatus)statusFlags.Value;

    // Check for Failed first (highest priority)
    if ((status & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed) {
      return "Failed";
    }

    // Check for Published
    if ((status & MessageProcessingStatus.Published) == MessageProcessingStatus.Published) {
      return "Published";
    }

    // Default to Pending (only Stored flag)
    return "Pending";
  }

  private async Task<string?> GetOutboxErrorAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<string?>(@"
      SELECT error FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<Guid?> GetOutboxInstanceIdAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<Guid?>(@"
      SELECT instance_id FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<DateTimeOffset?> GetOutboxLeaseExpiryAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<DateTimeOffset?>(@"
      SELECT lease_expiry FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task InsertInboxMessageAsync(
    Guid messageId,
    string handlerName,
    string messageType,
    string messageData,
    string status = "Pending",
    Guid? instanceId = null,
    DateTimeOffset? leaseExpiry = null,
    Guid? streamId = null,
    bool isEvent = false) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();

    // Map status string to MessageProcessingStatus flags
    var statusFlags = status switch {
      "Pending" => 1,  // Stored
      "Processing" => 1,  // Stored
      "Completed" => 2,  // EventStored (inbox is done when event stored)
      "Failed" => 32769,  // Stored | Failed
      _ => 1  // Default to Stored
    };

    // Add EventStored flag if isEvent=true
    if (isEvent) {
      statusFlags |= 2;  // Add EventStored bit
    }

    // Create envelope for storage (to match what ProcessWorkBatchAsync expects)
    // Manual JSON construction for test data (avoids AOT serialization complexities)
    var envelopeType = typeof(MessageEnvelope<TestEvent>).AssemblyQualifiedName!;
    var envelopeJson = $$"""
    {
      "$type": "{{envelopeType}}",
      "MessageId": "{{messageId}}",
      "Payload": {},
      "Hops": []
    }
    """;

    await connection.ExecuteAsync(@"
      INSERT INTO wh_inbox (
        message_id, handler_name, event_type, event_data, metadata, scope,
        status, attempts, received_at, processed_at, instance_id, lease_expiry,
        stream_id, partition_number
      ) VALUES (
        @messageId, @handlerName, @envelopeType, @envelopeJson::jsonb, '{}'::jsonb, NULL,
        @statusFlags, 0, @now, NULL, @instanceId, @leaseExpiry,
        @streamId,
        CASE WHEN @streamId IS NULL THEN NULL ELSE compute_partition(@streamId::uuid, 10000) END
      )",
      new {
        messageId,
        handlerName,
        envelopeType,
        envelopeJson,
        statusFlags,
        instanceId,
        leaseExpiry,
        streamId,
        now = DateTimeOffset.UtcNow
      });
  }

  private async Task<string?> GetInboxStatusAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var statusFlags = await connection.QueryFirstOrDefaultAsync<int?>(@"
      SELECT status FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });

    if (statusFlags == null) {
      return null;
    }

    // Convert status flags to human-readable string
    var status = (MessageProcessingStatus)statusFlags.Value;

    // Check for Failed first (highest priority)
    if ((status & MessageProcessingStatus.Failed) == MessageProcessingStatus.Failed) {
      return "Failed";
    }

    // Default to Pending (only Stored flag)
    return "Pending";
  }

  private async Task<string?> GetInboxErrorAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<string?>(@"
      SELECT error FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<Guid?> GetInboxInstanceIdAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<Guid?>(@"
      SELECT instance_id FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<int> CountInboxMessagesAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task InsertEventStoreRecordAsync(
    Guid streamId,
    Guid eventId,
    string eventType,
    string eventData,
    int version) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wh_event_store (
        event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, scope, sequence_number, version, created_at
      ) VALUES (
        @eventId, @streamId, @streamId, 'Test', @eventType, @eventData::jsonb, '{}'::jsonb, NULL, nextval('wh_event_sequence'), @version, @now
      )",
      new {
        eventId,
        streamId,
        eventType,
        eventData,
        version,
        now = DateTimeOffset.UtcNow
      });
  }

  private async Task<int?> GetEventStoreVersionAsync(Guid streamId, int expectedVersion) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<int?>(@"
      SELECT version FROM wh_event_store WHERE stream_id = @streamId AND version = @expectedVersion",
      new { streamId, expectedVersion });
  }

  private async Task<bool> GetOutboxIsEventAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var status = await connection.QueryFirstOrDefaultAsync<int>(@"
      SELECT status FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    // Check if EventStored bit (bit 2, value 2) is set
    return (status & 2) == 2;
  }

  private async Task<bool> GetInboxIsEventAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var status = await connection.QueryFirstOrDefaultAsync<int>(@"
      SELECT status FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
    // Check if EventStored bit (bit 2, value 2) is set
    return (status & 2) == 2;
  }

  private async Task<int?> GetOutboxPartitionNumberAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<int?>(@"
      SELECT partition_number FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task MarkInstanceHeartbeatOldAsync(Guid instanceId, DateTimeOffset oldHeartbeat) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      UPDATE wh_service_instances
      SET last_heartbeat_at = @oldHeartbeat
      WHERE instance_id = @instanceId",
      new { instanceId, oldHeartbeat });
  }

  private async Task UpdateOutboxLeaseExpiryAsync(Guid messageId, DateTimeOffset leaseExpiry) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      UPDATE wh_outbox
      SET lease_expiry = @leaseExpiry
      WHERE message_id = @messageId",
      new { messageId, leaseExpiry });
  }

  private async Task<MessageProcessingStatus> GetOutboxStatusFlagsAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var statusFlags = await connection.QueryFirstOrDefaultAsync<int>(@"
      SELECT status FROM wh_outbox WHERE message_id = @messageId",
      new { messageId });
    return (MessageProcessingStatus)statusFlags;
  }

  private async Task<MessageProcessingStatus> GetInboxStatusFlagsAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var statusFlags = await connection.QueryFirstOrDefaultAsync<int>(@"
      SELECT status FROM wh_inbox WHERE message_id = @messageId",
      new { messageId });
    return (MessageProcessingStatus)statusFlags;
  }

  private async Task<bool> ServiceInstanceExistsAsync(Guid instanceId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var count = await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wh_service_instances WHERE instance_id = @instanceId",
      new { instanceId });
    return count > 0;
  }
}
