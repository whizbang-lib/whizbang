using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ScopedWorkCoordinatorStrategy immediate processing of returned work.
/// Verifies that work returned from ProcessWorkBatchAsync is written to channel immediately.
/// </summary>
public class ScopedWorkCoordinatorStrategyImmediateProcessingTests {
  [Test]
  public async Task FlushAsync_WithReturnedWork_WritesToChannelImmediatelyAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var coordinator = new TestWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = System.Guid.NewGuid(),
          Destination = "test-topic",
          MessageType = "TestEvent",
          MessageData = "{}",
          Metadata = "{}",
          Attempts = 0,
          Status = MessageProcessingStatus.None
        },
        new OutboxWork {
          MessageId = System.Guid.NewGuid(),
          Destination = "test-topic",
          MessageType = "TestEvent",
          MessageData = "{}",
          Metadata = "{}",
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };

    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    // Queue a message to trigger flush
    strategy.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = System.Guid.NewGuid(),
      Destination = "test-topic",
      EventType = "TestEvent",
      EventData = "{}",
      Metadata = "{}",
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
    strategy.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = System.Guid.NewGuid(),
      Destination = "test-topic",
      EventType = "TestEvent",
      EventData = "{}",
      Metadata = "{}",
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
    coordinator.WorkToReturn = [
      new OutboxWork { MessageId = System.Guid.NewGuid(), Destination = "topic1", MessageType = "Event1", MessageData = "{}", Metadata = "{}", Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = System.Guid.NewGuid(), Destination = "topic1", MessageType = "Event1", MessageData = "{}", Metadata = "{}", Attempts = 0, Status = MessageProcessingStatus.None }
    ];
    strategy.QueueOutboxMessage(new NewOutboxMessage { MessageId = System.Guid.NewGuid(), Destination = "topic1", EventType = "Event1", EventData = "{}", Metadata = "{}", IsEvent = false });
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Act - Second flush with 3 messages
    coordinator.WorkToReturn = [
      new OutboxWork { MessageId = System.Guid.NewGuid(), Destination = "topic2", MessageType = "Event2", MessageData = "{}", Metadata = "{}", Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = System.Guid.NewGuid(), Destination = "topic2", MessageType = "Event2", MessageData = "{}", Metadata = "{}", Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = System.Guid.NewGuid(), Destination = "topic2", MessageType = "Event2", MessageData = "{}", Metadata = "{}", Attempts = 0, Status = MessageProcessingStatus.None }
    ];
    strategy.QueueOutboxMessage(new NewOutboxMessage { MessageId = System.Guid.NewGuid(), Destination = "topic2", EventType = "Event2", EventData = "{}", Metadata = "{}", IsEvent = false });
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
        new OutboxWork { MessageId = messageId1, Destination = "topic", MessageType = "Event", MessageData = "{}", Metadata = "{}", Attempts = 0, Status = MessageProcessingStatus.None },
        new OutboxWork { MessageId = messageId2, Destination = "topic", MessageType = "Event", MessageData = "{}", Metadata = "{}", Attempts = 0, Status = MessageProcessingStatus.None },
        new OutboxWork { MessageId = messageId3, Destination = "topic", MessageType = "Event", MessageData = "{}", Metadata = "{}", Attempts = 0, Status = MessageProcessingStatus.None }
      ]
    };

    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    strategy.QueueOutboxMessage(new NewOutboxMessage { MessageId = System.Guid.NewGuid(), Destination = "topic", EventType = "Event", EventData = "{}", Metadata = "{}", IsEvent = false });

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
}
