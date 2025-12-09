using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ScopedWorkCoordinatorStrategy immediate processing of returned work.
/// Verifies that work returned from ProcessWorkBatchAsync is written to channel immediately.
/// </summary>
public class ScopedWorkCoordinatorStrategyImmediateProcessingTests {
  // Helper method to create test envelope
  private static TestMessageEnvelope CreateTestEnvelope(System.Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
    };
  }

  [Test]
  public async Task FlushAsync_WithReturnedWork_WritesToChannelImmediatelyAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var messageId1 = System.Guid.NewGuid();
    var messageId2 = System.Guid.NewGuid();
    var coordinator = new TestWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = messageId1,
          Destination = "test-topic",
          Envelope = CreateTestEnvelope(messageId1),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        },
        new OutboxWork {
          MessageId = messageId2,
          Destination = "test-topic",
          Envelope = CreateTestEnvelope(messageId2),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };

    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    // Queue a message to trigger flush
    var queuedMessageId = System.Guid.NewGuid();
    strategy.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = queuedMessageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(queuedMessageId),
      IsEvent = false
    });

    // Act
    var result = await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Assert - Work should be written to channel immediately
    await Assert.That(channelWriter.WrittenWork).HasCount().EqualTo(2);
    await Assert.That(result.OutboxWork).HasCount().EqualTo(2);
  }

  [Test]
  public async Task FlushAsync_NoReturnedWork_DoesNotWriteToChannelAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var coordinator = new TestWorkCoordinator {
      WorkToReturn = []  // No work returned
    };

    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    // Queue a message to trigger flush
    var queuedMessageId = System.Guid.NewGuid();
    strategy.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = queuedMessageId,
      Destination = "test-topic",
      Envelope = CreateTestEnvelope(queuedMessageId),
      IsEvent = false
    });

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Assert - Nothing written to channel
    await Assert.That(channelWriter.WrittenWork).HasCount().EqualTo(0);
  }

  [Test]
  public async Task FlushAsync_MultipleFlushes_AllWorkWrittenToChannelAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var coordinator = new TestWorkCoordinator();
    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    // Act - First flush with 2 messages
    var msg1 = System.Guid.NewGuid();
    var msg2 = System.Guid.NewGuid();
    coordinator.WorkToReturn = [
      new OutboxWork { MessageId = msg1, Destination = "topic1", Envelope = CreateTestEnvelope(msg1), Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = msg2, Destination = "topic1", Envelope = CreateTestEnvelope(msg2), Attempts = 0, Status = MessageProcessingStatus.None }
    ];
    var queued1 = System.Guid.NewGuid();
    strategy.QueueOutboxMessage(new NewOutboxMessage { MessageId = queued1, Destination = "topic1", Envelope = CreateTestEnvelope(queued1), IsEvent = false });
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Act - Second flush with 3 messages
    var msg3 = System.Guid.NewGuid();
    var msg4 = System.Guid.NewGuid();
    var msg5 = System.Guid.NewGuid();
    coordinator.WorkToReturn = [
      new OutboxWork { MessageId = msg3, Destination = "topic2", Envelope = CreateTestEnvelope(msg3), Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = msg4, Destination = "topic2", Envelope = CreateTestEnvelope(msg4), Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = msg5, Destination = "topic2", Envelope = CreateTestEnvelope(msg5), Attempts = 0, Status = MessageProcessingStatus.None }
    ];
    var queued2 = System.Guid.NewGuid();
    strategy.QueueOutboxMessage(new NewOutboxMessage { MessageId = queued2, Destination = "topic2", Envelope = CreateTestEnvelope(queued2), IsEvent = false });
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Assert - All 5 messages written to channel
    await Assert.That(channelWriter.WrittenWork).HasCount().EqualTo(5);
  }

  [Test]
  public async Task FlushAsync_WithOrderedWork_MaintainsOrderInChannelAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var messageId1 = System.Guid.NewGuid();
    var messageId2 = System.Guid.NewGuid();
    var messageId3 = System.Guid.NewGuid();

    var coordinator = new TestWorkCoordinator {
      WorkToReturn = [
        new OutboxWork { MessageId = messageId1, Destination = "topic", Envelope = CreateTestEnvelope(messageId1), Attempts = 0, Status = MessageProcessingStatus.None },
        new OutboxWork { MessageId = messageId2, Destination = "topic", Envelope = CreateTestEnvelope(messageId2), Attempts = 0, Status = MessageProcessingStatus.None },
        new OutboxWork { MessageId = messageId3, Destination = "topic", Envelope = CreateTestEnvelope(messageId3), Attempts = 0, Status = MessageProcessingStatus.None }
      ]
    };

    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    var queuedMessageId = System.Guid.NewGuid();
    strategy.QueueOutboxMessage(new NewOutboxMessage { MessageId = queuedMessageId, Destination = "topic", Envelope = CreateTestEnvelope(queuedMessageId), IsEvent = false });

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Assert - Work written in same order
    await Assert.That(channelWriter.WrittenWork[0].MessageId).IsEqualTo(messageId1);
    await Assert.That(channelWriter.WrittenWork[1].MessageId).IsEqualTo(messageId2);
    await Assert.That(channelWriter.WrittenWork[2].MessageId).IsEqualTo(messageId3);
  }

  // Test helper - Mock work channel writer
  private class TestWorkChannelWriter : IWorkChannelWriter {
    public List<OutboxWork> WrittenWork { get; } = [];

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return ValueTask.CompletedTask;
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return true;
    }
  }

  // Test helper - Mock work coordinator
  private class TestWorkCoordinator : IWorkCoordinator {
    public List<OutboxWork> WorkToReturn { get; set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      System.Guid instanceId,
      string serviceName,
      string hostName,
      int processId,
      System.Collections.Generic.Dictionary<string, object>? metadata,
      MessageCompletion[] outboxCompletions,
      MessageFailure[] outboxFailures,
      MessageCompletion[] inboxCompletions,
      MessageFailure[] inboxFailures,
      NewOutboxMessage[] newOutboxMessages,
      NewInboxMessage[] newInboxMessages,
      System.Guid[] renewOutboxLeaseIds,
      System.Guid[] renewInboxLeaseIds,
      WorkBatchFlags flags,
      int partitionCount,
      int maxPartitionsPerInstance,
      int leaseSeconds,
      int staleThresholdSeconds,
      CancellationToken cancellationToken = default
    ) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = WorkToReturn,
        InboxWork = []
      });
    }
  }

  // Test helper - Mock service instance provider
  private class TestServiceInstanceProvider : IServiceInstanceProvider {
    public System.Guid InstanceId { get; } = System.Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  // Test envelope implementation
  private class TestMessageEnvelope : IMessageEnvelope {
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }

    public void AddHop(MessageHop hop) {
      Hops.Add(hop);
    }

    public DateTimeOffset GetMessageTimestamp() {
      return Hops.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
    }

    public CorrelationId? GetCorrelationId() {
      return Hops.Count > 0 ? Hops[0].CorrelationId : null;
    }

    public MessageId? GetCausationId() {
      return Hops.Count > 0 ? Hops[0].CausationId : null;
    }

    public object? GetMetadata(string key) {
      for (var i = Hops.Count - 1; i >= 0; i--) {
        if (Hops[i].Type == HopType.Current && Hops[i].Metadata?.ContainsKey(key) == true) {
          return Hops[i].Metadata[key];
        }
      }
      return null;
    }

    public object GetPayload() {
      return new { };
    }
  }
}
