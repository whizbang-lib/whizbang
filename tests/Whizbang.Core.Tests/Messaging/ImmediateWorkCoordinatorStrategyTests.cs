using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ImmediateWorkCoordinatorStrategy - verifies immediate flush behavior.
/// </summary>
public class ImmediateWorkCoordinatorStrategyTests {
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

  // Simple test message for envelope creation
  private record TestEvent(string Data);

  // ========================================
  // Priority 3 Tests: Immediate Strategy
  // ========================================

  [Test]
  public async Task FlushAsync_ImmediatelyCallsWorkCoordinatorAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      MaxPartitionsPerInstance = 100,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new TestEvent("test-data"),
      Hops = new List<MessageHop>()
    };
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    });

    // Act
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - FlushAsync should immediately call ProcessWorkBatchAsync
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Immediate strategy should call ProcessWorkBatchAsync on FlushAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task QueueOutboxMessage_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      MaxPartitionsPerInstance = 100,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new TestEvent("test-data"),
      Hops = new List<MessageHop>()
    };
    var outboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act
    sut.QueueOutboxMessage(outboxMessage);
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Message should be passed to coordinator
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].Destination).IsEqualTo("test-topic");
  }

  [Test]
  public async Task QueueInboxMessage_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      MaxPartitionsPerInstance = 100,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new TestEvent("test-data"),
      Hops = new List<MessageHop>()
    };
    var inboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act
    sut.QueueInboxMessage(inboxMessage);
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Message should be passed to coordinator
    await Assert.That(fakeCoordinator.LastNewInboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].HandlerName).IsEqualTo("TestHandler");
  }

  // ========================================
  // Test Fakes
  // ========================================

  private class FakeWorkCoordinator : IWorkCoordinator {
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
