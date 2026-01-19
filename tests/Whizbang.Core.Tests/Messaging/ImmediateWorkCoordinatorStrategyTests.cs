using System.Text.Json;
using System.Text.Json.Serialization;
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
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

  // Simple test message for envelope creation
  public record _testEvent([StreamKey] string Data) : IEvent;

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
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = new List<MessageHop>()
    };

    // Serialize typed envelope to JsonElement envelope for OutboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = new List<MessageHop>()
      }
    });

    // Act
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - FlushAsync should immediately call ProcessWorkBatchAsync
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Immediate strategy should call ProcessWorkBatchAsync on FlushAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
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
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = new List<MessageHop>()
    };

    // Serialize typed envelope to JsonElement envelope for OutboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    var outboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = new List<MessageHop>()
      }
    };

    // Act
    sut.QueueOutboxMessage(outboxMessage);
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Message should be passed to coordinator
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
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
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = new List<MessageHop>()
    };

    // Serialize typed envelope to JsonElement envelope for InboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    var inboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act
    sut.QueueInboxMessage(inboxMessage);
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Message should be passed to coordinator
    await Assert.That(fakeCoordinator.LastNewInboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].HandlerName).IsEqualTo("TestHandler");
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = request.NewOutboxMessages;
      LastNewInboxMessages = request.NewInboxMessages;

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
}
