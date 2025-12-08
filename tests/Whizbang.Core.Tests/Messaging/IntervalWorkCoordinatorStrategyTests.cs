using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IntervalWorkCoordinatorStrategy - verifies timer-based batching behavior.
/// </summary>
public class IntervalWorkCoordinatorStrategyTests {
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

  // ========================================
  // Priority 3 Tests: Interval Strategy
  // ========================================

  [Test]
  public async Task BackgroundTimer_FlushesEveryIntervalAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 100,  // 100ms interval for fast test
      PartitionCount = 10000,
      MaxPartitionsPerInstance = 100,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    sut.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      EventType = "TestEvent",
      EventData = "{}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true
    });

    // Act - Wait for timer to fire (give it 250ms = 2+ intervals)
    await Task.Delay(250);

    // Assert - Timer should have flushed the queued message
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Background timer should flush queued messages automatically");

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueuedMessages_BatchedUntilTimerAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 500,  // 500ms interval
      PartitionCount = 10000,
      MaxPartitionsPerInstance = 100,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();

    // Act - Queue two messages quickly (before timer fires)
    sut.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = messageId1,
      Destination = "topic1",
      EventType = "Event1",
      EventData = "{}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true
    });

    await Task.Delay(50);  // Small delay, but less than timer interval

    sut.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = messageId2,
      Destination = "topic2",
      EventType = "Event2",
      EventData = "{}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true
    });

    // Wait for timer to fire
    await Task.Delay(600);

    // Assert - Both messages should be batched together in single flush
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Timer should batch and flush messages together");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages.Length).IsGreaterThanOrEqualTo(2)
      .Because("Both messages should be batched in the same flush");

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task DisposeAsync_FlushesAndStopsTimerAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,  // 1 second interval
      PartitionCount = 10000,
      MaxPartitionsPerInstance = 100,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    sut.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      EventType = "TestEvent",
      EventData = "{}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true
    });

    // Act - Dispose before timer fires (within 1 second)
    await sut.DisposeAsync();

    // Assert - Disposal should flush queued message immediately
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush queued messages immediately");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);

    var callCountBeforeDelay = fakeCoordinator.ProcessWorkBatchCallCount;

    // Wait to verify timer stopped (no additional flushes after disposal)
    await Task.Delay(1500);

    // Assert - Timer should be stopped (no additional flush calls)
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(callCountBeforeDelay)
      .Because("Timer should be stopped after disposal");
  }

  [Test]
  public async Task ManualFlushAsync_DoesNotWaitForTimerAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 5000,  // 5 second interval (long)
      PartitionCount = 10000,
      MaxPartitionsPerInstance = 100,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    sut.QueueOutboxMessage(new NewOutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      EventType = "TestEvent",
      EventData = "{}",
      Metadata = "{}",
      Scope = null,
      StreamId = _idProvider.NewGuid(),
      IsEvent = true
    });

    // Act - Manual flush (should not wait for 5 second timer)
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Manual flush should work immediately (not wait for timer)
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Manual FlushAsync should flush immediately without waiting for timer");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // Test Fakes
  // ========================================

  private class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public NewOutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public NewInboxMessage[] LastNewInboxMessages { get; private set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      Guid instanceId,
      string serviceName,
      string hostName,
      int processId,
      Dictionary<string, object>? metadata,
      MessageCompletion[] outboxCompletions,
      MessageFailure[] outboxFailures,
      MessageCompletion[] inboxCompletions,
      MessageFailure[] inboxFailures,
      NewOutboxMessage[] newOutboxMessages,
      NewInboxMessage[] newInboxMessages,
      Guid[] renewOutboxLeaseIds,
      Guid[] renewInboxLeaseIds,
      WorkBatchFlags flags = WorkBatchFlags.None,
      int partitionCount = 10000,
      int maxPartitionsPerInstance = 100,
      int leaseSeconds = 300,
      int staleThresholdSeconds = 300,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = newOutboxMessages;
      LastNewInboxMessages = newInboxMessages;

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = []
      });
    }
  }

  private class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() {
      return new ServiceInstanceInfo {
        ServiceName = ServiceName,
        InstanceId = InstanceId,
        HostName = HostName,
        ProcessId = ProcessId
      };
    }
  }
}
