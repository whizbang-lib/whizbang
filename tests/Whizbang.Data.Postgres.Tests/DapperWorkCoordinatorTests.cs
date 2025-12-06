using System.Text.Json;
using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Postgres.Tests;

/// <summary>
/// Integration tests for DapperWorkCoordinator.
/// Tests the process_work_batch PostgreSQL function and lease-based work coordination.
/// Uses UUIDv7 for all message IDs to ensure proper time-ordered database indexing.
/// </summary>
public class DapperWorkCoordinatorTests : PostgresTestBase {
  private DapperWorkCoordinator _sut = null!;
  private Guid _instanceId;
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

  [Before(Test)]
  public async Task TestSetupAsync() {
    _instanceId = _idProvider.NewGuid();
    // Get connection string from base class (before any connections are opened)
    _sut = new DapperWorkCoordinator(ConnectionString);
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
      newOutboxMessages: [],
      newInboxMessages: [],
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
      newOutboxMessages: [],
      newInboxMessages: []);

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
      newOutboxMessages: [],
      newInboxMessages: []);

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
        new MessageCompletion { MessageId = messageId1, Status = MessageProcessingStatus.ReceptorProcessed },
        new MessageCompletion { MessageId = messageId2, Status = MessageProcessingStatus.ReceptorProcessed }
      ],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: []);

    // Assert
    await Assert.That(result.InboxWork).HasCount().EqualTo(0);

    // Verify messages marked as Completed
    var status1 = await GetInboxStatusAsync(messageId1);
    var status2 = await GetInboxStatusAsync(messageId2);
    await Assert.That(status1).IsEqualTo("Completed");
    await Assert.That(status2).IsEqualTo("Completed");
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
      newOutboxMessages: [],
      newInboxMessages: []);

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
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: []);

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
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: []);

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
        new MessageCompletion { MessageId = completedInboxId, Status = MessageProcessingStatus.ReceptorProcessed }
      ],
      inboxFailures: [
        new MessageFailure {
          MessageId = failedInboxId,
          CompletedStatus = MessageProcessingStatus.Stored,
          Error = "Inbox error"
        }
      ],
      newOutboxMessages: [],
      newInboxMessages: []);

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

  // ========================================
  // Priority 1 Tests: New Message Storage
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_NewOutboxMessage_StoresAndReturnsImmediatelyAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    var newOutboxMessage = new NewOutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{}",
      Scope = null,
      StreamId = streamId,
      IsEvent = true
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
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: []);

    // Assert - Message should be stored AND returned for immediate processing
    await Assert.That(result.OutboxWork).HasCount().EqualTo(1);
    var work = result.OutboxWork[0];
    await Assert.That(work.MessageId).IsEqualTo(messageId);
    await Assert.That(work.Destination).IsEqualTo("test-topic");
    await Assert.That(work.MessageType).IsEqualTo("TestEvent");
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

    var newInboxMessage = new NewInboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true
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
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage]);

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
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage]);

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

    var newInboxMessage = new NewInboxMessage {
      MessageId = _idProvider.NewGuid(),
      HandlerName = "TestHandler",
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{}",
      Scope = null,
      StreamId = streamId,
      IsEvent = true
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
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage]);

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

    var newOutboxMessage = new NewOutboxMessage {
      MessageId = _idProvider.NewGuid(),
      Destination = "test-topic",
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{}",
      Scope = null,
      StreamId = streamId,
      IsEvent = true
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
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: []);

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

    // Insert outbox message with IsEvent=true
    await InsertOutboxMessageAsync(
      messageId,
      "test-topic",
      "TestEvent",
      "{\"test\":\"data\"}",
      status: "Publishing",
      instanceId: _instanceId,
      streamId: streamId,
      isEvent: true);

    // Act - Complete the outbox message
    var result = await _sut.ProcessWorkBatchAsync(
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
      newOutboxMessages: [],
      newInboxMessages: []);

    // Assert - Event should be persisted to event store
    var eventVersion = await GetEventStoreVersionAsync(streamId, messageId);
    await Assert.That(eventVersion).IsNotNull()
      .Because("Completing an event outbox message should persist it to wb_event_store");
    await Assert.That(eventVersion!.Value).IsEqualTo(1)
      .Because("First event in stream should have version 1");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_WithEventInbox_PersistsToEventStoreAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();
    var streamId = _idProvider.NewGuid();

    // Insert inbox message with IsEvent=true
    await InsertInboxMessageAsync(
      messageId,
      "TestHandler",
      "TestEvent",
      "{\"test\":\"data\"}",
      status: "Processing",
      instanceId: _instanceId,
      streamId: streamId,
      isEvent: true);

    // Act - Complete the inbox message
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [
        new MessageCompletion { MessageId = messageId, Status = MessageProcessingStatus.ReceptorProcessed }
      ],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: []);

    // Assert - Event should be persisted to event store
    var eventVersion = await GetEventStoreVersionAsync(streamId, messageId);
    await Assert.That(eventVersion).IsNotNull()
      .Because("Completing an event inbox message should persist it to wb_event_store");
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

    // Insert first event already in event store (simulating conflict)
    await InsertEventStoreRecordAsync(streamId, messageId1, "TestEvent", "{}", version: 1);

    // Insert inbox message trying to use same version
    await InsertInboxMessageAsync(
      messageId2,
      "TestHandler",
      "TestEvent",
      "{\"test\":\"data\"}",
      status: "Processing",
      instanceId: _instanceId,
      streamId: streamId,
      isEvent: true);

    // Act - Try to complete inbox message (should handle version conflict)
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [
        new MessageCompletion { MessageId = messageId2, Status = MessageProcessingStatus.ReceptorProcessed }
      ],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: []);

    // Assert - Should handle optimistic concurrency (either increment version or fail gracefully)
    var event1 = await GetEventStoreVersionAsync(streamId, messageId1);
    await Assert.That(event1).IsEqualTo(1)
      .Because("First event should remain at version 1");

    // Either the second event gets version 2, or the operation fails gracefully
    var event2 = await GetEventStoreVersionAsync(streamId, messageId2);
    if (event2 is not null) {
      await Assert.That(event2.Value).IsEqualTo(2)
        .Because("If optimistic concurrency succeeds, second event gets version 2");
    } else {
      // If it failed, inbox should be marked as Failed
      var inboxStatus = await GetInboxStatusAsync(messageId2);
      await Assert.That(inboxStatus).IsEqualTo("Failed")
        .Because("If optimistic concurrency fails, inbox should be marked Failed");
    }
  }

  [Test]
  public async Task ProcessWorkBatchAsync_MultipleEventsInStream_IncrementsVersionAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var streamId = _idProvider.NewGuid();
    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();
    var messageId3 = _idProvider.NewGuid();

    // Insert three outbox messages for the same stream
    await InsertOutboxMessageAsync(messageId1, "topic1", "Event1", "{}", status: "Publishing", instanceId: _instanceId, streamId: streamId, isEvent: true);
    await InsertOutboxMessageAsync(messageId2, "topic1", "Event2", "{}", status: "Publishing", instanceId: _instanceId, streamId: streamId, isEvent: true);
    await InsertOutboxMessageAsync(messageId3, "topic1", "Event3", "{}", status: "Publishing", instanceId: _instanceId, streamId: streamId, isEvent: true);

    // Act - Complete all three messages
    var result = await _sut.ProcessWorkBatchAsync(
      _instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion { MessageId = messageId1, Status = MessageProcessingStatus.Published },
        new MessageCompletion { MessageId = messageId2, Status = MessageProcessingStatus.Published },
        new MessageCompletion { MessageId = messageId3, Status = MessageProcessingStatus.Published }
      ],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      newOutboxMessages: [],
      newInboxMessages: []);

    // Assert - Events should have sequential versions
    var version1 = await GetEventStoreVersionAsync(streamId, messageId1);
    var version2 = await GetEventStoreVersionAsync(streamId, messageId2);
    var version3 = await GetEventStoreVersionAsync(streamId, messageId3);

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

    // Insert outbox message with IsEvent=false (command, not event)
    await InsertOutboxMessageAsync(
      messageId,
      "test-topic",
      "TestCommand",
      "{\"test\":\"data\"}",
      status: "Publishing",
      instanceId: _instanceId,
      streamId: streamId,
      isEvent: false);

    // Act - Complete the outbox message
    var result = await _sut.ProcessWorkBatchAsync(
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
      newOutboxMessages: [],
      newInboxMessages: []);

    // Assert - Non-event should NOT be persisted to event store
    var eventVersion = await GetEventStoreVersionAsync(streamId, messageId);
    await Assert.That(eventVersion).IsNull()
      .Because("Non-events (IsEvent=false) should not be persisted to wb_event_store");

    // Verify outbox was still marked as Published
    var outboxStatus = await GetOutboxStatusAsync(messageId);
    await Assert.That(outboxStatus).IsEqualTo("Published");
  }

  // ========================================
  // Priority 1 Tests: IsEvent Serialization
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventTrue_StoresIsEventFlagAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newOutboxMessage = new NewOutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true  // CRITICAL: IsEvent = true
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
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: []);

    // Assert - Verify is_event flag is stored correctly
    var isEvent = await GetOutboxIsEventAsync(messageId);
    await Assert.That(isEvent).IsTrue()
      .Because("NewOutboxMessage with IsEvent=true should persist is_event=true to wb_outbox");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventFalse_StoresIsEventFlagAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newOutboxMessage = new NewOutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      EventType = "TestCommand",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = false  // CRITICAL: IsEvent = false
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
      newOutboxMessages: [newOutboxMessage],
      newInboxMessages: []);

    // Assert - Verify is_event flag is stored correctly
    var isEvent = await GetOutboxIsEventAsync(messageId);
    await Assert.That(isEvent).IsFalse()
      .Because("NewOutboxMessage with IsEvent=false should persist is_event=false to wb_outbox");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewInboxMessage_WithIsEventTrue_StoresIsEventFlagAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newInboxMessage = new NewInboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      EventType = "TestEvent",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true  // CRITICAL: IsEvent = true
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
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage]);

    // Assert - Verify is_event flag is stored correctly
    var isEvent = await GetInboxIsEventAsync(messageId);
    await Assert.That(isEvent).IsTrue()
      .Because("NewInboxMessage with IsEvent=true should persist is_event=true to wb_inbox");
  }

  [Test]
  public async Task ProcessWorkBatchAsync_NewInboxMessage_WithIsEventFalse_StoresIsEventFlagAsync() {
    // Arrange
    await InsertServiceInstanceAsync(_instanceId, "TestService", "test-host", 12345);
    var messageId = _idProvider.NewGuid();

    var newInboxMessage = new NewInboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      EventType = "TestCommand",
      EventData = "{\"test\":\"data\"}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = false  // CRITICAL: IsEvent = false
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
      newOutboxMessages: [],
      newInboxMessages: [newInboxMessage]);

    // Assert - Verify is_event flag is stored correctly
    var isEvent = await GetInboxIsEventAsync(messageId);
    await Assert.That(isEvent).IsFalse()
      .Because("NewInboxMessage with IsEvent=false should persist is_event=false to wb_inbox");
  }

  // Helper methods for test data setup and verification

  private async Task InsertServiceInstanceAsync(Guid instanceId, string serviceName, string hostName, int processId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    var now = DateTimeOffset.UtcNow;
    await connection.ExecuteAsync(@"
      INSERT INTO wb_service_instances (instance_id, service_name, host_name, process_id, started_at, last_heartbeat_at, metadata)
      VALUES (@instanceId, @serviceName, @hostName, @processId, @now, @now, NULL)",
      new { instanceId, serviceName, hostName, processId, now });
  }

  private async Task<DateTimeOffset?> GetInstanceHeartbeatAsync(Guid instanceId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<DateTimeOffset?>(@"
      SELECT last_heartbeat_at FROM wb_service_instances WHERE instance_id = @instanceId",
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
    await connection.ExecuteAsync(@"
      INSERT INTO wb_outbox (
        message_id, destination, event_type, event_data, metadata, scope,
        status, attempts, error, created_at, published_at,
        instance_id, lease_expiry, stream_id, is_event
      ) VALUES (
        @messageId, @destination, @messageType, @messageData::jsonb, '{}'::jsonb, NULL,
        @status, 0, NULL, @now, NULL,
        @instanceId, @leaseExpiry, @streamId, @isEvent
      )",
      new {
        messageId,
        destination,
        messageType,
        messageData,
        status,
        instanceId,
        leaseExpiry,
        streamId,
        isEvent,
        now = DateTimeOffset.UtcNow
      });
  }

  private async Task<string?> GetOutboxStatusAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<string?>(@"
      SELECT status FROM wb_outbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<string?> GetOutboxErrorAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<string?>(@"
      SELECT error FROM wb_outbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<Guid?> GetOutboxInstanceIdAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<Guid?>(@"
      SELECT instance_id FROM wb_outbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<DateTimeOffset?> GetOutboxLeaseExpiryAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<DateTimeOffset?>(@"
      SELECT lease_expiry FROM wb_outbox WHERE message_id = @messageId",
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
    await connection.ExecuteAsync(@"
      INSERT INTO wb_inbox (
        message_id, handler_name, event_type, event_data, metadata, scope,
        status, attempts, received_at, processed_at, instance_id, lease_expiry,
        stream_id, is_event
      ) VALUES (
        @messageId, @handlerName, @messageType, @messageData::jsonb, '{}'::jsonb, NULL,
        @status, 0, @now, NULL, @instanceId, @leaseExpiry,
        @streamId, @isEvent
      )",
      new {
        messageId,
        handlerName,
        messageType,
        messageData,
        status,
        instanceId,
        leaseExpiry,
        streamId,
        isEvent,
        now = DateTimeOffset.UtcNow
      });
  }

  private async Task<string?> GetInboxStatusAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<string?>(@"
      SELECT status FROM wb_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<string?> GetInboxErrorAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<string?>(@"
      SELECT error FROM wb_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<Guid?> GetInboxInstanceIdAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<Guid?>(@"
      SELECT instance_id FROM wb_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<int> CountInboxMessagesAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QuerySingleAsync<int>(@"
      SELECT COUNT(*) FROM wb_inbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task InsertEventStoreRecordAsync(
    Guid streamId,
    Guid messageId,
    string eventType,
    string eventData,
    int version) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    await connection.ExecuteAsync(@"
      INSERT INTO wb_event_store (
        stream_id, message_id, event_type, event_data, version, timestamp
      ) VALUES (
        @streamId, @messageId, @eventType, @eventData::jsonb, @version, @now
      )",
      new {
        streamId,
        messageId,
        eventType,
        eventData,
        version,
        now = DateTimeOffset.UtcNow
      });
  }

  private async Task<int?> GetEventStoreVersionAsync(Guid streamId, Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<int?>(@"
      SELECT version FROM wb_event_store WHERE stream_id = @streamId AND message_id = @messageId",
      new { streamId, messageId });
  }

  private async Task<bool> GetOutboxIsEventAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<bool>(@"
      SELECT is_event FROM wb_outbox WHERE message_id = @messageId",
      new { messageId });
  }

  private async Task<bool> GetInboxIsEventAsync(Guid messageId) {
    using var connection = await ConnectionFactory.CreateConnectionAsync();
    return await connection.QueryFirstOrDefaultAsync<bool>(@"
      SELECT is_event FROM wb_inbox WHERE message_id = @messageId",
      new { messageId });
  }
}
