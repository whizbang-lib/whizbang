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
  private readonly Uuid7IdProvider _idProvider = new();

  // Test message types
  public record _testEvent1([StreamKey] string Id = "test-1") : IEvent { }
  public record _testEvent2([StreamKey] string Id = "test-2") : IEvent { }
  public record _testEvent3([StreamKey] string Id = "test-3") : IEvent { }

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
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId1),
        Hops = []
      }
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
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages).Count().IsEqualTo(1);
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
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });

    // Act - Manual flush before disposal
    var result = await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - Manual flush should work immediately
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Manual FlushAsync should flush immediately");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
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
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(outboxId1),
        Hops = []
      }
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
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(outboxId2),
        Hops = []
      }
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
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(2);
    await Assert.That(fakeCoordinator.LastNewInboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastOutboxCompletions).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastInboxFailures).Count().IsEqualTo(1);
  }

  // ========================================
  // CONSTRUCTOR VALIDATION TESTS
  // ========================================

  [Test]
  public async Task Constructor_WithNullCoordinator_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    // Act & Assert
    await Assert.That(() => new ScopedWorkCoordinatorStrategy(
      null!,
      instanceProvider,
      null,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullInstanceProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var options = new WorkCoordinatorOptions();

    // Act & Assert
    await Assert.That(() => new ScopedWorkCoordinatorStrategy(
      fakeCoordinator,
      null!,
      null,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();

    // Act & Assert
    await Assert.That(() => new ScopedWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      null,
      null!
    )).Throws<ArgumentNullException>();
  }

  // ========================================
  // DISPOSED STATE TESTS
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);
    await sut.DisposeAsync();

    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent1> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent1(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test",
      Envelope = jsonEnvelope,
      EnvelopeType = "Test",
      StreamId = _idProvider.NewGuid(),
      IsEvent = false,
      MessageType = "Test",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] }
    })).ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxMessage_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);
    await sut.DisposeAsync();

    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent1> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent1(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    // Act & Assert
    await Assert.That(() => sut.QueueInboxMessage(new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = jsonEnvelope,
      EnvelopeType = "Test",
      MessageType = "Test"
    })).ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueOutboxCompletion_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxCompletion(_idProvider.NewGuid(), MessageProcessingStatus.Published))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxCompletion_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueInboxCompletion(_idProvider.NewGuid(), MessageProcessingStatus.Published))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueOutboxFailure_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxFailure(_idProvider.NewGuid(), MessageProcessingStatus.Failed, "error"))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxFailure_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueInboxFailure(_idProvider.NewGuid(), MessageProcessingStatus.Failed, "error"))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task FlushAsync_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(async () => await sut.FlushAsync(WorkBatchFlags.None))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task DisposeAsync_CalledMultipleTimes_DoesNotThrowAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);

    // Act - Dispose multiple times
    await sut.DisposeAsync();
    await sut.DisposeAsync();
    await sut.DisposeAsync();

    // Assert - Should not throw
  }

  // ========================================
  // DEBUG MODE TEST
  // ========================================

  [Test]
  public async Task FlushAsync_WithDebugMode_SetsDebugFlagAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinatorWithFlags();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { DebugMode = true };

    var sut = new ScopedWorkCoordinatorStrategy(fakeCoordinator, instanceProvider, null, options);

    var messageId = _idProvider.NewGuid();
    sut.QueueOutboxCompletion(messageId, MessageProcessingStatus.Published);

    // Act
    await sut.FlushAsync(WorkBatchFlags.None);

    // Assert - DebugMode flag should be set
    await Assert.That(fakeCoordinator.LastFlags & WorkBatchFlags.DebugMode).IsEqualTo(WorkBatchFlags.DebugMode);

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
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = request.NewOutboxMessages;
      LastNewInboxMessages = request.NewInboxMessages;
      LastOutboxCompletions = request.OutboxCompletions;
      LastInboxFailures = request.InboxFailures;

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

  private sealed class FakeWorkCoordinatorWithFlags : IWorkCoordinator {
    public WorkBatchFlags LastFlags { get; private set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      LastFlags = request.Flags;
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
}
