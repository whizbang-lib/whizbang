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
/// Tests for ScopedWorkCoordinatorStrategy - verifies scope-based batching and disposal flush.
/// </summary>
public class ScopedWorkCoordinatorStrategyTests {
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

  // Test message types
  public record _testEvent1 : IEvent { }
  public record _testEvent2 : IEvent { }
  public record _testEvent3 : IEvent { }

  // ========================================
  // Priority 3 Tests: Scoped Strategy
  // ========================================

  [Test]
  public async Task DisposeAsync_FlushesQueuedMessagesAsync() {
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

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      null,  // IWorkChannelWriter (not needed for these tests)
      options
    );

    var messageId1 = _idProvider.NewGuid();
    var messageId2 = _idProvider.NewGuid();

    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    var envelope1 = new MessageEnvelope<_testEvent1> {
      MessageId = MessageId.From(messageId1),
      Payload = new _testEvent1(),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ]
    };

    // Serialize to JsonElement envelope
    var envelope1Json = JsonSerializer.Serialize((object)envelope1, jsonOptions);
    var jsonEnvelope1 = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelope1Json, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId1,
      Destination = "topic1",
      Envelope = jsonEnvelope1,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    });

    var envelope2 = new MessageEnvelope<_testEvent2> {
      MessageId = MessageId.From(messageId2),
      Payload = new _testEvent2(),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ]
    };

    // Serialize to JsonElement envelope
    var envelope2Json = JsonSerializer.Serialize((object)envelope2, jsonOptions);
    var jsonEnvelope2 = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelope2Json, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    sut.QueueInboxMessage(new InboxMessage {
      MessageId = messageId2,
      HandlerName = "Handler1",
      Envelope = jsonEnvelope2,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    });

    // Act - Dispose should flush queued messages
    await sut.DisposeAsync();

    // Assert - Messages should be flushed on disposal
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush queued messages");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(messageId2);
  }

  [Test]
  public async Task FlushAsync_BeforeDisposal_FlushesImmediatelyAsync() {
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

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      null,  // IWorkChannelWriter (not needed for these tests)
      options
    );

    var messageId = _idProvider.NewGuid();

    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    var envelope = new MessageEnvelope<_testEvent1> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent1(),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ]
    };

    // Serialize to JsonElement envelope
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
      MessageType = "TestMessage, TestAssembly"
    });

    // Act - Manual flush before disposal
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Manual flush should work immediately
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Manual FlushAsync should flush immediately");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);

    // Act - Dispose after manual flush (should not flush again - queue is empty)
    await sut.DisposeAsync();

    // Assert - No additional flush on disposal (queue already empty)
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should not flush again if queue is empty");
  }

  [Test]
  public async Task MultipleQueues_FlushedTogetherOnDisposalAsync() {
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

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      null,  // IWorkChannelWriter (not needed for these tests)
      options
    );

    var outboxId1 = _idProvider.NewGuid();
    var outboxId2 = _idProvider.NewGuid();
    var inboxId1 = _idProvider.NewGuid();
    var completionId = _idProvider.NewGuid();
    var failureId = _idProvider.NewGuid();

    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Queue multiple types of operations
    var envelope1 = new MessageEnvelope<_testEvent1> {
      MessageId = MessageId.From(outboxId1),
      Payload = new _testEvent1(),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ]
    };

    var envelope1Json = JsonSerializer.Serialize((object)envelope1, jsonOptions);
    var jsonEnvelope1 = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelope1Json, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = outboxId1,
      Destination = "topic1",
      Envelope = jsonEnvelope1,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    });

    var envelope2 = new MessageEnvelope<_testEvent2> {
      MessageId = MessageId.From(outboxId2),
      Payload = new _testEvent2(),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ]
    };

    var envelope2Json = JsonSerializer.Serialize((object)envelope2, jsonOptions);
    var jsonEnvelope2 = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelope2Json, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = outboxId2,
      Destination = "topic2",
      Envelope = jsonEnvelope2,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    });

    var envelope3 = new MessageEnvelope<_testEvent3> {
      MessageId = MessageId.From(inboxId1),
      Payload = new _testEvent3(),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          }
        }
      ]
    };

    var envelope3Json = JsonSerializer.Serialize((object)envelope3, jsonOptions);
    var jsonEnvelope3 = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelope3Json, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    sut.QueueInboxMessage(new InboxMessage {
      MessageId = inboxId1,
      HandlerName = "Handler1",
      Envelope = jsonEnvelope3,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    });

    sut.QueueOutboxCompletion(completionId, MessageProcessingStatus.Published);
    sut.QueueInboxFailure(failureId, MessageProcessingStatus.Stored, "Test error");

    // Act - Dispose should flush all queued operations together
    await sut.DisposeAsync();

    // Assert - All operations flushed in single batch
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("All operations should be flushed in a single batch");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).HasCount().EqualTo(2);
    await Assert.That(fakeCoordinator.LastNewInboxMessages).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastOutboxCompletions).HasCount().EqualTo(1);
    await Assert.That(fakeCoordinator.LastInboxFailures).HasCount().EqualTo(1);
  }

  // ========================================
  // Test Fakes
  // ========================================

  private class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];

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
      LastOutboxCompletions = outboxCompletions;
      LastInboxFailures = inboxFailures;

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
