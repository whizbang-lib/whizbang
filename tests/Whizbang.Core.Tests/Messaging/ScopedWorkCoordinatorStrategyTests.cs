using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ScopedWorkCoordinatorStrategy - verifies scope-based batching and disposal flush.
/// </summary>
public class ScopedWorkCoordinatorStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // Test message types
  public record _testEvent1([StreamId] string Id = "test-1") : IEvent { }
  public record _testEvent2([StreamId] string Id = "test-2") : IEvent { }
  public record _testEvent3([StreamId] string Id = "test-3") : IEvent { }

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
    _ = await sut.FlushAsync(WorkBatchOptions.None);

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
  // BestEffort FLUSH MODE — DbContext DISPOSAL SAFETY
  // ========================================

  /// <summary>
  /// Regression test: BestEffort mode previously deferred flush to DisposeAsync,
  /// but by that time the DbContext (IWorkCoordinator) may already be disposed
  /// by the DI container — causing ObjectDisposedException on first HTTP request.
  ///
  /// Fix: Scoped strategy treats BestEffort the same as Required (flush immediately).
  /// </summary>
  [Test]
  public async Task BestEffort_WithDisposedCoordinator_DoesNotThrow_BecauseFlushHappensImmediatelyAsync() {
    // Arrange - Coordinator that throws on second call (simulating DbContext disposed)
    var disposableCoordinator = new FakeDisposableWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      disposableCoordinator,
      instanceProvider,
      null,
      options
    );

    _queueTestOutboxMessage(sut);

    // Act — BestEffort should flush immediately (the fix)
    await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

    // Assert — message was flushed during the BestEffort call, not deferred
    await Assert.That(disposableCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Scoped strategy should flush BestEffort immediately to avoid disposal race");

    // Now simulate DI container disposing the coordinator (DbContext)
    disposableCoordinator.SimulateDisposal();

    // DisposeAsync should have nothing to flush (no ObjectDisposedException)
    await sut.DisposeAsync();

    // Still only 1 call — the BestEffort flush, not DisposeAsync
    await Assert.That(disposableCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should not attempt flush — already flushed");
  }

  [Test]
  public async Task BestEffort_MultipleMessages_AllFlushedImmediately_NoneLeftForDisposalAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator,
      instanceProvider,
      null,
      options
    );

    // Simulate a typical request: multiple publishes via BestEffort
    _queueTestOutboxMessage(sut);
    await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

    _queueTestOutboxMessage(sut);
    await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

    _queueTestOutboxMessage(sut);
    await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

    // Assert — each BestEffort call flushed immediately
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(3)
      .Because("each BestEffort call should flush immediately on Scoped strategy");

    // DisposeAsync should be a no-op
    await sut.DisposeAsync();
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(3)
      .Because("DisposeAsync should not flush — nothing left in queues");
  }

  // ========================================
  // DISPOSE SKIPS LIFECYCLE — data-only safety net
  // ========================================

  /// <summary>
  /// Verifies that DisposeAsync persists data (calls ProcessWorkBatchAsync) but does NOT
  /// invoke lifecycle stages, preventing ObjectDisposedException when ambient resources
  /// like HttpContext are already disposed during scope teardown.
  /// </summary>
  [Test]
  public async Task DisposeAsync_SkipsLifecycle_StillPersistsDataAsync() {
    // Arrange — use a scope factory that tracks whether lifecycle scopes were created
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var trackingScopeFactory = new TrackingScopeFactory();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      null,
      options,
      dependencies: new ScopedWorkCoordinatorDependencies {
        ScopeFactory = trackingScopeFactory
      }
    );

    _queueTestOutboxMessage(sut);

    // Act — dispose triggers data-only flush (skipLifecycle: true)
    await sut.DisposeAsync();

    // Assert — data was persisted
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should still persist data via ProcessWorkBatchAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);

    // Assert — no lifecycle scopes were created (lifecycle was skipped)
    await Assert.That(trackingScopeFactory.ScopeCreationCount).IsEqualTo(0)
      .Because("DisposeAsync should skip lifecycle stages to avoid ObjectDisposedException");
  }

  /// <summary>
  /// Verifies that DisposeAsync with queued messages calls ProcessWorkBatchAsync
  /// without creating lifecycle scopes, while explicit FlushAsync with no unflushed
  /// data in DisposeAsync correctly avoids double-flush.
  /// </summary>
  [Test]
  public async Task DisposeAsync_WithUnflushedData_PersistsWithoutLifecycleScopesAsync() {
    // Arrange — coordinator that tracks all calls
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var trackingScopeFactory = new TrackingScopeFactory();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      null,
      options,
      dependencies: new ScopedWorkCoordinatorDependencies {
        ScopeFactory = trackingScopeFactory
      }
    );

    // Queue messages but don't flush manually — let DisposeAsync handle it
    _queueTestOutboxMessage(sut);
    _queueTestOutboxMessage(sut);

    // Act — DisposeAsync should persist data but skip lifecycle
    await sut.DisposeAsync();

    // Assert — data was persisted
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should persist queued data");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(2);

    // Assert — no lifecycle scopes created
    await Assert.That(trackingScopeFactory.ScopeCreationCount).IsEqualTo(0)
      .Because("DisposeAsync should skip lifecycle to avoid ObjectDisposedException");
  }

  // ========================================
  // PENDING AUDIT MESSAGES — accumulation bug fix
  // ========================================

  /// <summary>
  /// Regression test: PendingAuditMessages were not cleared after flush,
  /// causing stale audit messages to accumulate across multiple flushes.
  /// </summary>
  [Test]
  public async Task FlushAsync_PendingAuditMessages_ClearedAfterFlushAsync() {
    // Arrange — enable audit so PendingAuditMessages gets populated
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var systemEventOptions = new Whizbang.Core.SystemEvents.SystemEventOptions();
    systemEventOptions.EnableEventAudit();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options,
      dependencies: new ScopedWorkCoordinatorDependencies {
        SystemEventOptions = systemEventOptions
      }
    );

    // First flush: queue an event message (generates audit message)
    _queueTestOutboxMessage(sut);
    await sut.FlushAsync(WorkBatchOptions.None);
    var firstFlushOutboxCount = fakeCoordinator.LastNewOutboxMessages.Length;

    // Second flush: queue another event message
    _queueTestOutboxMessage(sut);
    await sut.FlushAsync(WorkBatchOptions.None);
    var secondFlushOutboxCount = fakeCoordinator.LastNewOutboxMessages.Length;

    // Assert — same count both times (no accumulation of stale audit messages)
    await Assert.That(secondFlushOutboxCount).IsEqualTo(firstFlushOutboxCount)
      .Because("PendingAuditMessages should be cleared after each flush, not accumulate");

    // Cleanup
    await sut.DisposeAsync();
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
    await Assert.That(async () => await sut.FlushAsync(WorkBatchOptions.None))
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
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - DebugMode flag should be set
    await Assert.That(fakeCoordinator.LastFlags & WorkBatchOptions.DebugMode).IsEqualTo(WorkBatchOptions.DebugMode);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // Logger Coverage Tests (Lines 97-100, 123, 161, 227-228, 270)
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_WithAuditEnabled_BuildsAuditMessageAsync() {
    // Arrange - EventAuditEnabled + IsEvent exercises lines 97-100
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var systemEventOptions = new Whizbang.Core.SystemEvents.SystemEventOptions();
    systemEventOptions.EnableEventAudit();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options,
      dependencies: new ScopedWorkCoordinatorDependencies {
        SystemEventOptions = systemEventOptions
      }
    );

    // Queue an event message with IsEvent=true
    _queueTestOutboxMessage(sut);

    // Flush to merge audit messages (lines 227-228)
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - Should have original + audit message in the batch
    await Assert.That(fakeCoordinator.LastNewOutboxMessages.Length).IsGreaterThanOrEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueOutboxCompletion_WithLogger_LogsCompletionQueuedAsync() {
    // Arrange - logger != null exercises line 123
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Act
    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueInboxFailure_WithLogger_LogsFailureQueuedAsync() {
    // Arrange - logger != null exercises line 161
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Act
    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "Test error");

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task FlushAsync_WithLogger_QueuedMessagesButNoWorkReturned_LogsNoWorkReturnedAsync() {
    // Arrange - logger != null and queued messages but 0 outbox work returned exercises line 270
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Queue a message so there is work queued
    _queueTestOutboxMessage(sut);

    // Act - Flush (FakeWorkCoordinator returns empty OutboxWork, triggers line 270)
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - Logger received multiple log calls
    await Assert.That(logger.LogCount).IsGreaterThanOrEqualTo(2);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeScopedLogger : Microsoft.Extensions.Logging.ILogger<ScopedWorkCoordinatorStrategy> {
    public int LogCount { get; private set; }

    public void Log<TState>(
      Microsoft.Extensions.Logging.LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      LogCount++;
    }

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
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
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
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

  private void _queueTestOutboxMessage(ScopedWorkCoordinatorStrategy strategy) {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent1> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent1(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });
  }

  private void _queueTestInboxMessage(ScopedWorkCoordinatorStrategy strategy) {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent2> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent2(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    strategy.QueueInboxMessage(new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    });
  }

  /// <summary>
  /// Simulates a DbContext-backed coordinator that becomes disposed mid-scope
  /// (as happens when DI container disposes services in arbitrary order).
  /// </summary>
  private sealed class FakeDisposableWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    private bool _disposed;

    public void SimulateDisposal() => _disposed = true;

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      if (_disposed) {
        throw new ObjectDisposedException("DbContext", "Cannot access a disposed object.");
      }
      ProcessWorkBatchCallCount++;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Tracks how many scopes are created — lifecycle stages create scopes to resolve invokers.
  /// When skipLifecycle is true, no scopes should be created.
  /// </summary>
  private sealed class TrackingScopeFactory : IServiceScopeFactory {
    public int ScopeCreationCount { get; private set; }

    public IServiceScope CreateScope() {
      ScopeCreationCount++;
      return new FakeServiceScope();
    }

    private sealed class FakeServiceScope : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = new FakeServiceProvider();
      public void Dispose() { }
    }

    private sealed class FakeServiceProvider : IServiceProvider {
      public object? GetService(Type serviceType) => null;
    }
  }

  // ========================================
  // QUEUE WITH LOGGER Tests (Lines 74-76, 84-86, 102-104, 111-113)
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_WithLogger_LogsQueuedMessageAsync() {
    // Arrange - logger != null exercises lines 74-76
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Act
    _queueTestOutboxMessage(sut);

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0)
      .Because("QueueOutboxMessage should log when logger is provided");

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueInboxMessage_WithLogger_LogsQueuedMessageAsync() {
    // Arrange - logger != null exercises lines 84-86
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Act
    _queueTestInboxMessage(sut);

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0)
      .Because("QueueInboxMessage should log when logger is provided");

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueInboxCompletion_WithLogger_LogsCompletionQueuedAsync() {
    // Arrange - logger != null exercises lines 102-104
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Act
    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0)
      .Because("QueueInboxCompletion should log when logger is provided");

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueOutboxFailure_WithLogger_LogsFailureQueuedAsync() {
    // Arrange - logger != null exercises lines 111-113
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Act
    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "Test outbox error");

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0)
      .Because("QueueOutboxFailure should log when logger is provided");

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // METRICS Tests (Lines 127, 135)
  // ========================================

  [Test]
  public async Task FlushAsync_WithMetrics_RecordsFlushCallsMetricAsync() {
    // Arrange - metrics != null exercises line 127
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics());

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, metrics: metrics
    );

    _queueTestOutboxMessage(sut);

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - no exception means metrics were recorded successfully
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task FlushAsync_WithMetrics_EmptyQueue_RecordsEmptyFlushMetricAsync() {
    // Arrange - metrics != null + empty queue exercises lines 127 AND 135
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics());

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, metrics: metrics
    );

    // Act - flush with empty queues
    var result = await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
    await Assert.That(result.InboxWork).Count().IsEqualTo(0);
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
      .Because("empty queue should skip ProcessWorkBatchAsync");

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH DIAGNOSTIC LOGGING — outbox work returned (Lines 176-179)
  // ========================================

  [Test]
  public async Task FlushAsync_WithLogger_OutboxWorkReturned_LogsReturnedWorkAsync() {
    // Arrange - logger != null + OutboxWork.Count > 0 exercises lines 176-179
    var fakeCoordinator = new FakeWorkCoordinatorWithOutboxWork();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    _queueTestOutboxMessage(sut);

    // Act
    var result = await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - OutboxWork was returned and logged
    await Assert.That(result.OutboxWork).Count().IsGreaterThan(0);
    await Assert.That(logger.LogCount).IsGreaterThanOrEqualTo(3)
      .Because("Should log flush summary, instance id, batch result, AND returned outbox work");

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // IWorkFlusher EXPLICIT INTERFACE (Line 190-191)
  // ========================================

  [Test]
  public async Task IWorkFlusher_FlushAsync_DelegatesToFlushWithRequiredModeAsync() {
    // Arrange - exercises line 190-191 (explicit IWorkFlusher.FlushAsync implementation)
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options
    );

    _queueTestOutboxMessage(sut);

    // Act - call via IWorkFlusher interface
    IWorkFlusher flusher = sut;
    await flusher.FlushAsync(CancellationToken.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("IWorkFlusher.FlushAsync should delegate to FlushAsync with Required mode");

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // DISPOSE WITH LOGGER — unflushed ops (Lines 204-211)
  // ========================================

  [Test]
  public async Task DisposeAsync_WithLogger_UnflushedOps_LogsWarningAsync() {
    // Arrange - logger != null + unflushed ops exercises lines 204-211
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Queue messages but don't flush manually
    _queueTestOutboxMessage(sut);
    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);
    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "test error");

    // Act
    await sut.DisposeAsync();

    // Assert - logger should have received warning about unflushed ops
    await Assert.That(logger.LogCount).IsGreaterThanOrEqualTo(4)
      .Because("Should log queue operations AND the unflushed-on-disposal warning");
  }

  // ========================================
  // DISPOSE CATCH BLOCK — flush throws (Lines 238-241)
  // ========================================

  [Test]
  public async Task DisposeAsync_FlushThrows_WithLogger_LogsErrorAndDoesNotThrowAsync() {
    // Arrange - exercises lines 238-241 (catch block with logger)
    var throwingCoordinator = new FakeThrowingWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      throwingCoordinator, instanceProvider, null, options, logger: logger
    );

    // Queue a message so DisposeAsync attempts flush
    _queueTestOutboxMessage(sut);

    // Act - DisposeAsync should catch the exception and log it, not throw
    await sut.DisposeAsync();

    // Assert - error was logged, no exception propagated
    await Assert.That(logger.LogCount).IsGreaterThan(0)
      .Because("DisposeAsync should log the error when flush fails");
  }

  [Test]
  public async Task DisposeAsync_FlushThrows_WithoutLogger_DoesNotThrowAsync() {
    // Arrange - exercises catch block without logger (line 239 condition)
    var throwingCoordinator = new FakeThrowingWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      throwingCoordinator, instanceProvider, null, options
    // no logger
    );

    // Queue a message so DisposeAsync attempts flush
    _queueTestOutboxMessage(sut);

    // Act - DisposeAsync should catch the exception, not throw
    await sut.DisposeAsync();

    // Assert - no exception was thrown (test passes if we reach here)
  }

  // ========================================
  // STREAM ID GUARD Tests (Lines 71, 81)
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_WithEmptyGuidStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange - StreamId = Guid.Empty (non-null) exercises line 71
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options
    );

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
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = Guid.Empty, // Empty Guid triggers StreamIdGuard
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] }
    })).Throws<Whizbang.Core.Validation.InvalidStreamIdException>();

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueInboxMessage_WithEmptyGuidStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange - StreamId = Guid.Empty (non-null) exercises line 81
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options
    );

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
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = Guid.Empty, // Empty Guid triggers StreamIdGuard
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    })).Throws<Whizbang.Core.Validation.InvalidStreamIdException>();

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH WITH INBOX-ONLY (no outbox queued) — exercises branch where outboxMessages.Length == 0
  // ========================================

  [Test]
  public async Task FlushAsync_WithInboxOnly_FlushesInboxMessagesAsync() {
    // Arrange - only inbox messages, no outbox, exercises the else-if branch at line 181
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options
    );

    _queueTestInboxMessage(sut);

    // Act
    var result = await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(0);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH WITH COMPLETIONS-ONLY — exercises flush with no new messages but completions
  // ========================================

  [Test]
  public async Task FlushAsync_WithCompletionsOnly_FlushesCompletionsAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options
    );

    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);

    // Act
    var result = await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task FlushAsync_WithOutboxFailuresOnly_FlushesFailuresAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options
    );

    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "test error");

    // Act
    var result = await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // CONSTRUCTOR — null dependencies defaults (Line 62)
  // ========================================

  [Test]
  public async Task Constructor_WithNullDependencies_DefaultsToEmptyDependenciesAsync() {
    // Arrange & Act - null dependencies should default to new ScopedWorkCoordinatorDependencies()
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options,
      dependencies: null
    );

    // Should work fine without throwing
    _queueTestOutboxMessage(sut);
    await sut.FlushAsync(WorkBatchOptions.None);

    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH WITH LOGGER — multiple outbox work items (Lines 177-179 loop)
  // ========================================

  [Test]
  public async Task FlushAsync_WithLogger_MultipleOutboxWorkReturned_LogsUpToThreeAsync() {
    // Arrange - exercises the Take(3) loop at lines 177-179
    var fakeCoordinator = new FakeWorkCoordinatorWithMultipleOutboxWork();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    _queueTestOutboxMessage(sut);

    // Act
    var result = await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - should have logged returned work items (up to 3)
    await Assert.That(result.OutboxWork).Count().IsEqualTo(4);
    // Logs: queued message, flush summary, instance id, batch result, + 3 returned work items
    await Assert.That(logger.LogCount).IsGreaterThanOrEqualTo(6)
      .Because("Should log up to 3 returned outbox work items plus other diagnostics");

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // DISPOSE — inbox completions and failures in unflushed warning
  // ========================================

  [Test]
  public async Task DisposeAsync_WithLogger_InboxCompletionsAndFailures_LogsAllCountsAsync() {
    // Arrange - exercises all counters in unflushed warning (lines 204-211)
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var logger = new FakeScopedLogger();

    var sut = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, null, options, logger: logger
    );

    // Queue all types of operations to ensure all counters in the warning are non-zero
    _queueTestOutboxMessage(sut);
    _queueTestInboxMessage(sut);
    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);
    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "error1");
    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "error2");

    // Act
    await sut.DisposeAsync();

    // Assert - logger should have many calls from queuing + disposal warning
    await Assert.That(logger.LogCount).IsGreaterThanOrEqualTo(7)
      .Because("Should log each queue operation AND the unflushed-on-disposal warning");
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush all remaining operations");
  }

  // ========================================
  // Additional Test Fakes
  // ========================================

  /// <summary>
  /// Coordinator that always throws on ProcessWorkBatchAsync.
  /// Used to test DisposeAsync catch block (lines 238-241).
  /// </summary>
  private sealed class FakeThrowingWorkCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated database failure");
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Coordinator that returns outbox work items — used to exercise lines 176-179.
  /// </summary>
  private sealed class FakeWorkCoordinatorWithOutboxWork : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      var outboxWork = request.NewOutboxMessages.Select(m => new OutboxWork {
        MessageId = m.MessageId,
        Destination = m.Destination,
        Envelope = m.Envelope,
        EnvelopeType = m.EnvelopeType,
        MessageType = m.MessageType,
        Attempts = 0,
        Flags = WorkBatchOptions.NewlyStored
      }).ToList();

      return Task.FromResult(new WorkBatch {
        OutboxWork = outboxWork,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Coordinator that returns 4 outbox work items to test Take(3) logging loop.
  /// </summary>
  private sealed class FakeWorkCoordinatorWithMultipleOutboxWork : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      var outboxWork = new List<OutboxWork>();
      var envelope = request.NewOutboxMessages.Length > 0 ? request.NewOutboxMessages[0].Envelope : default!;
      // Return 4 items — only 3 should be logged
      for (int i = 0; i < 4; i++) {
        outboxWork.Add(new OutboxWork {
          MessageId = Guid.NewGuid(),
          Destination = $"topic-{i}",
          Envelope = envelope,
          EnvelopeType = "TestEnvelope, TestAssembly",
          MessageType = "TestMessage, TestAssembly",
          Attempts = 0,
          Flags = i % 2 == 0 ? WorkBatchOptions.NewlyStored : WorkBatchOptions.None
        });
      }

      return Task.FromResult(new WorkBatch {
        OutboxWork = outboxWork,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeWorkCoordinatorWithFlags : IWorkCoordinator {
    public WorkBatchOptions LastFlags { get; private set; }

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
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }
}
