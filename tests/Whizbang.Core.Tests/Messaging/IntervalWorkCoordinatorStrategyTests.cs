using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IntervalWorkCoordinatorStrategy - verifies timer-based batching behavior.
/// </summary>
public class IntervalWorkCoordinatorStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

  // Helper method to create test envelope
  private static TestMessageEnvelope _createTestEnvelope(Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
    };
  }

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
    var envelope = _createTestEnvelope(messageId);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = new List<MessageHop>()
      }
    });

    // Act - Wait for timer to fire (give it 500ms = 5x the interval for reliability under load)
    await Task.Delay(500);

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
      IntervalMilliseconds = 1000,  // 1 second interval (longer to avoid races)
      PartitionCount = 10000,
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

    // Act - Queue two messages quickly (well before timer fires)
    var envelope1 = _createTestEnvelope(messageId1);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId1,
      Destination = "topic1",
      Envelope = envelope1,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId1),
        Hops = new List<MessageHop>()
      }
    });

    await Task.Delay(100);  // Delay between messages (still less than 1s interval)

    var envelope2 = _createTestEnvelope(messageId2);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId2,
      Destination = "topic2",
      Envelope = envelope2,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId2),
        Hops = new List<MessageHop>()
      }
    });

    // Wait for timer to fire (1s interval + buffer)
    await Task.Delay(1500);

    // Assert - Both messages should be batched together in single flush
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Timer should have fired and flushed messages");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages.Length).IsEqualTo(2)
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
    var envelope = _createTestEnvelope(messageId);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = new List<MessageHop>()
      }
    });

    // Act - Dispose before timer fires (within 1 second)
    await sut.DisposeAsync();

    // Assert - Disposal should flush queued message immediately
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush queued messages immediately");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
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
    var envelope = _createTestEnvelope(messageId);
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = new List<MessageHop>()
      }
    });

    // Act - Manual flush (should not wait for 5 second timer)
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Manual flush should work immediately (not wait for timer)
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Manual FlushAsync should flush immediately without waiting for timer");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      Guid instanceId,
      string serviceName,
      string hostName,
      int processId,
      Dictionary<string, JsonElement>? metadata,
      MessageCompletion[] outboxCompletions,
      MessageFailure[] outboxFailures,
      MessageCompletion[] inboxCompletions,
      MessageFailure[] inboxFailures,
      ReceptorProcessingCompletion[] receptorCompletions,
      ReceptorProcessingFailure[] receptorFailures,
      PerspectiveCheckpointCompletion[] perspectiveCompletions,
      PerspectiveCheckpointFailure[] perspectiveFailures,
      OutboxMessage[] newOutboxMessages,
      InboxMessage[] newInboxMessages,
      Guid[] renewOutboxLeaseIds,
      Guid[] renewInboxLeaseIds,
      WorkBatchFlags flags = WorkBatchFlags.None,
      int partitionCount = 10000,
      int leaseSeconds = 300,
      int staleThresholdSeconds = 300,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = newOutboxMessages;
      LastNewInboxMessages = newInboxMessages;

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCheckpointCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCheckpointFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
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

  // Test envelope implementation
  private sealed class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }
    public JsonElement Payload { get; init; } = JsonDocument.Parse("{}").RootElement;  // Test payload
    object IMessageEnvelope.Payload => Payload;  // Explicit interface implementation

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

    public JsonElement? GetMetadata(string key) {
      for (var i = Hops.Count - 1; i >= 0; i--) {
        if (Hops[i].Type == HopType.Current && Hops[i].Metadata?.ContainsKey(key) == true) {
          return Hops[i].Metadata![key];
        }
      }
      return null;
    }
  }
}
