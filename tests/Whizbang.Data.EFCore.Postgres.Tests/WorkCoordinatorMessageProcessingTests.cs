using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests that verify messages stored in outbox get returned for processing.
/// This is THE critical test for the bug: messages leased but never returned for processing.
/// </summary>
public class WorkCoordinatorMessageProcessingTests : EFCoreTestBase {
  private Guid _instanceId;
  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _sut = null!;

  [Before(Test)]
  public async Task InitializeSystemUnderTestAsync() {
    await base.SetupAsync();
    _instanceId = Guid.CreateVersion7();
    _sut = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext());
  }

  [Test]
  public async Task MessagesStoredInOutbox_AreReturnedImmediately_InSameCallAsync() {
    // This is the CORE bug test:
    // When messages are stored with newOutboxMessages, they should be returned immediately

    // Arrange
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();

    // Act - Store message in outbox
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
      newOutboxMessages: [
        new NewOutboxMessage {
          MessageId = messageId.Value,
          Destination = "test-topic",
          EventType = "TestEvent",
          EventData = "{\"data\":\"test\"}",
          Metadata = "{\"hops\":[]}",
          Scope = null,
          IsEvent = true,
          StreamId = streamId
        }
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    // Assert - Message should be returned in THIS CALL
    await Assert.That(result.OutboxWork)
      .HasCount()
      .EqualTo(1)
      .Because("Newly stored messages should be returned for processing in the same call");

    var work = result.OutboxWork[0];
    await Assert.That(work.MessageId).IsEqualTo(messageId.Value);
    await Assert.That(work.Destination).IsEqualTo("test-topic");
  }

  [Test]
  public async Task MessagesWithExpiredLease_AreReclaimed_InSubsequentCallAsync() {
    // Arrange - Store message with short lease
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();

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
      newOutboxMessages: [
        new NewOutboxMessage {
          MessageId = messageId.Value,
          Destination = "test-topic",
          EventType = "TestEvent",
          EventData = "{\"data\":\"test\"}",
          Metadata = "{\"hops\":[]}",
          Scope = null,
          IsEvent = true,
          StreamId = streamId
        }
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      leaseSeconds: -1  // Immediately expired lease!
    );

    // Act - Call again - expired lease should be reclaimed
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
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    // Assert - Expired message should be reclaimed
    await Assert.That(result.OutboxWork)
      .HasCount()
      .EqualTo(1)
      .Because("Messages with expired leases should be reclaimed");
  }

  [Test]
  public async Task MessagesWithValidLease_SameInstance_AreNotReturnedAgainAsync() {
    // This verifies we DON'T return already-leased messages (avoiding double-processing)

    // Arrange - Store message (gets leased)
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();

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
      newOutboxMessages: [
        new NewOutboxMessage {
          MessageId = messageId.Value,
          Destination = "test-topic",
          EventType = "TestEvent",
          EventData = "{\"data\":\"test\"}",
          Metadata = "{\"hops\":[]}",
          Scope = null,
          IsEvent = true,
          StreamId = streamId
        }
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      leaseSeconds: 300  // Long lease
    );

    // Act - Call again WITHOUT completing the message
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
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    // Assert - Should NOT return the message again (still leased)
    await Assert.That(result.OutboxWork)
      .HasCount()
      .EqualTo(0)
      .Because("Messages with valid leases should not be returned again to avoid double-processing");
  }
}
