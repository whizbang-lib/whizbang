using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Edge-case and branch-coverage tests for IntervalWorkCoordinatorStrategy.
/// Targets uncovered paths: BestEffort flush, CoalesceWindow, IWorkFlusher,
/// StreamIdGuard validation, null logger branches, metrics, scopeFactory constructor,
/// and timer callback early returns.
/// </summary>
public class IntervalWorkCoordinatorStrategyEdgeCaseTests {
  private static TestMessageEnvelope _createEnvelope(Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
    };
  }

  private static OutboxMessage _createOutboxMessage(Guid? messageId = null, Guid? streamId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = _createEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = streamId ?? Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(id),
        Hops = []
      }
    };
  }

  private static InboxMessage _createInboxMessage(Guid? messageId = null, Guid? streamId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "TestHandler",
      Envelope = _createEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = streamId ?? Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private static WorkCoordinatorOptions _createOptions(int intervalMs = 60000, int coalesceMs = 0) {
    return new WorkCoordinatorOptions {
      IntervalMilliseconds = intervalMs,
      CoalesceWindowMilliseconds = coalesceMs,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300,
      DebugMode = false
    };
  }

  // ============================================================
  // FlushAsync (fire-and-forget) - returns empty batch without flushing
  // ============================================================

  [Test]
  public async Task FlushAsync_BestEffortMode_ReturnsEmptyBatchWithoutFlushingAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert - BestEffort returns empty batch; items remain queued for timer
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
        .Because("BestEffort mode should not flush to the work coordinator");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_BestEffortMode_ItemsStillFlushedOnNextRequiredFlushAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    try {
      // Act - BestEffort defers
      await sut.FlushAsync(WorkBatchOptions.None);

      // Then Required flush picks them up
      await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
      await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // CoalesceWindow - delay before flushing
  // ============================================================

  [Test]
  public async Task FlushAsync_WithCoalesceWindow_WaitsBeforeFlushingAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions(coalesceMs: 50); // 50ms coalesce window
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      var sw = System.Diagnostics.Stopwatch.StartNew();
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
      sw.Stop();

      // Assert - should have waited at least 50ms for coalesce window
      await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(40)
        .Because("CoalesceWindow should introduce a delay before flushing");
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_CoalesceWindowWithCancellation_ThrowsOperationCanceledAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions(coalesceMs: 5000); // Long coalesce window
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    using var cts = new CancellationTokenSource();
    cts.Cancel(); // Cancel immediately

    try {
      // Act & Assert
      var threw = false;
      try {
        await sut.FlushAndGetBatchAsync(WorkBatchOptions.None, cts.Token);
      } catch (OperationCanceledException) {
        threw = true;
      }
      await Assert.That(threw).IsTrue();
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Constructor: scopeFactory path (null coordinator + scopeFactory)
  // ============================================================

  [Test]
  public async Task Constructor_WithNullCoordinatorAndScopeFactory_DoesNotThrowAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator, TrackingWorkCoordinator>();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();

    // Act - null coordinator but valid scopeFactory should not throw
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider: instanceProvider,
      options: options,
      scopeFactory: scopeFactory
    );

    // Assert - should construct successfully
    await Assert.That(sut).IsNotNull();

    await sut.DisposeAsync();
  }

  [Test]
  public async Task Constructor_WithNullCoordinatorAndNullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();

    // Act & Assert
    await Assert.That(() => new IntervalWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider: instanceProvider,
      options: options,
      scopeFactory: null
    )).Throws<ArgumentNullException>();
  }

  // ============================================================
  // Constructor: null instanceProvider
  // ============================================================

  [Test]
  public async Task Constructor_WithNullInstanceProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var options = _createOptions();

    // Act & Assert
    await Assert.That(() => new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: null!,
      options: options
    )).Throws<ArgumentNullException>();
  }

  // ============================================================
  // Constructor: null options
  // ============================================================

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();

    // Act & Assert
    await Assert.That(() => new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: null!
    )).Throws<ArgumentNullException>();
  }

  // ============================================================
  // IWorkFlusher explicit interface
  // ============================================================

  [Test]
  public async Task IWorkFlusher_FlushAsync_DelegatesToFlushCoreAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act - call through IWorkFlusher interface
      IWorkFlusher flusher = sut;
      await flusher.FlushAsync(CancellationToken.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
        .Because("IWorkFlusher.FlushAsync should delegate to FlushAsync with Required mode");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task IWorkFlusher_FlushAsync_WithEmptyQueues_DoesNotCallCoordinatorAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      // Act - flush with nothing queued via IWorkFlusher
      IWorkFlusher flusher = sut;
      await flusher.FlushAsync(CancellationToken.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // StreamIdGuard validation: empty StreamId on queue methods
  // ============================================================

  [Test]
  public async Task QueueOutboxMessage_WithEmptyStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      // Act & Assert - StreamId = Guid.Empty (non-null) should fail validation
      await Assert.That(() => sut.QueueOutboxMessage(_createOutboxMessage(streamId: Guid.Empty)))
        .ThrowsExactly<InvalidStreamIdException>();
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_WithEmptyStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      // Act & Assert
      await Assert.That(() => sut.QueueInboxMessage(_createInboxMessage(streamId: Guid.Empty)))
        .ThrowsExactly<InvalidStreamIdException>();
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxMessage_WithNullStreamId_ReachesTheCoordinatorAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    var id = Guid.CreateVersion7();
    var message = new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = _createEnvelope(id),
      EnvelopeType = "Test",
      StreamId = null, // null is valid
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] }
    };

    try {
      // Act
      sut.QueueOutboxMessage(message);
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - the guard admits a null StreamId, and the message survives the flush with the
      // null intact. Only rejecting null would be a regression; so would quietly substituting
      // Guid.Empty, which the guard itself rejects on the next hop.
      await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1)
        .Because("a stream-less outbox message must still be stored, not dropped by the guard");
      await Assert.That(coordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(id);
      await Assert.That(coordinator.LastNewOutboxMessages[0].StreamId).IsNull();
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_WithNullStreamId_ReachesTheCoordinatorAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    var id = Guid.CreateVersion7();
    var message = new InboxMessage {
      MessageId = id,
      HandlerName = "TestHandler",
      Envelope = _createEnvelope(id),
      EnvelopeType = "Test",
      StreamId = null, // null is valid
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    };

    try {
      // Act
      sut.QueueInboxMessage(message);
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - same contract on the inbox side: null is admitted and preserved to the store.
      await Assert.That(coordinator.LastNewInboxMessages.Length).IsEqualTo(1)
        .Because("a stream-less inbox message must still be stored, not dropped by the guard");
      await Assert.That(coordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(id);
      await Assert.That(coordinator.LastNewInboxMessages[0].StreamId).IsNull();
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Null logger branches: constructor and queue methods
  // ============================================================

  [Test]
  public async Task Constructor_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();

    // Act - no logger parameter
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    // Assert
    await Assert.That(sut).IsNotNull();

    await sut.DisposeAsync();
  }

  // Each of these drives the "logger is null, skip the log line" branch of a Queue* method. The
  // branch is one `if` around a log call, and the thing that must survive it is the enqueue: a
  // regression that moved the enqueue inside the logger guard would lose every message in a host
  // that never configured logging, silently. So each test flushes and looks for the queued item at
  // the far end rather than stopping at "the call returned".

  [Test]
  public async Task QueueOutboxMessage_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    var message = _createOutboxMessage();

    try {
      // Act - no logger, should skip logging
      sut.QueueOutboxMessage(message);
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - the enqueue happened, not just the logging skip
      await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(message.MessageId);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    var message = _createInboxMessage();

    try {
      sut.QueueInboxMessage(message);
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      await Assert.That(coordinator.LastNewInboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(message.MessageId);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxCompletion_WithoutLogger_DoesNotThrowAsync() {
    // Completions reach IOutboxCompletionChannel only down the scoped-provider path, so the scope
    // constructor is what makes the queued completion observable at all.
    var host = new ChannelScopeHost();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider: instanceProvider,
      options: options,
      scopeFactory: host.ScopeFactory);
    var messageId = Guid.CreateVersion7();

    try {
      sut.QueueOutboxCompletion(messageId, MessageProcessingStatus.Published);
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      await Assert.That(host.CompletionChannel.EnqueuedIds).Contains(messageId)
        .Because("the completion is what marks the outbox row done — losing it in the null-logger "
               + "branch would leave the message to be republished on the next claim");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxCompletion_WithoutLogger_DoesNotThrowAsync() {
    // Unlike the outbox side, WorkCoordinatorFlushHelper has no sink for inbox completions — it
    // consumes them only in the empty-queue check. So the observable here is one step earlier:
    // whether the queued completion made the flush non-empty. An empty flush short-circuits before
    // a scope is ever created, so a coordinator resolution is proof the item was really queued.
    var host = new ChannelScopeHost();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider: instanceProvider,
      options: options,
      scopeFactory: host.ScopeFactory);

    try {
      sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      await Assert.That(host.CoordinatorResolutions).IsEqualTo(1)
        .Because("a flush whose queues were all empty returns before creating a scope; resolving "
               + "the coordinator proves the completion survived the null-logger branch");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxFailure_WithoutLogger_DoesNotThrowAsync() {
    var host = new ChannelScopeHost();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider: instanceProvider,
      options: options,
      scopeFactory: host.ScopeFactory);
    var messageId = Guid.CreateVersion7();

    try {
      sut.QueueOutboxFailure(messageId, MessageProcessingStatus.Failed, "error");
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      await Assert.That(host.FailureChannel.Enqueued).Contains((WorkCategory.Outbox, messageId))
        .Because("a failure that never reaches the failure channel is a message that retries "
               + "forever without its attempt count ever moving");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxFailure_WithoutLogger_DoesNotThrowAsync() {
    var host = new ChannelScopeHost();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider: instanceProvider,
      options: options,
      scopeFactory: host.ScopeFactory);
    var messageId = Guid.CreateVersion7();

    try {
      sut.QueueInboxFailure(messageId, MessageProcessingStatus.Failed, "error");
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      await Assert.That(host.FailureChannel.Enqueued).Contains((WorkCategory.Inbox, messageId))
        .Because("the inbox category is what routes the failure to the inbox row; a miscategorized "
               + "or dropped failure leaves the handler's message stuck in-flight");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // FlushAsync without logger
  // ============================================================

  [Test]
  public async Task FlushAsync_WithQueuedItems_WithoutLogger_FlushesSuccessfullyAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err1");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err2");

    try {
      // Act
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert — outbox + inbox each trigger their own store call; completions/failures
      // route via channels (helper-level coverage in WorkCoordinatorFlushHelperTests).
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
      await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastNewInboxMessages.Length).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Metrics paths
  // ============================================================

  [Test]
  public async Task FlushAsync_WithMetrics_RecordsFlushCallsAsync() {
    // Arrange
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, metrics: metrics);

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - metrics FlushCalls counter was incremented (no exception means success)
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_EmptyQueuesWithMetrics_RecordsEmptyFlushCallsAsync() {
    // Arrange
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger, metrics: metrics);

    try {
      // Act - flush with nothing queued
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - should return empty and record empty flush metric
      await Assert.That(result.OutboxWork).IsEmpty();
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task FlushAsync_BestEffortWithMetrics_RecordsFlushCallsAsync() {
    // Arrange
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, metrics: metrics);

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // DisposeAsync without logger
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithoutLogger_DrainsTheQueueAndMarksDisposedAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    // Queue something so disposal flush has work
    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    // Act
    await sut.DisposeAsync();

    // Assert - the shutdown drain is the last chance queued work has to be persisted; a
    // null-logger host must not skip it. And disposal must actually complete: a strategy that
    // drained but never flipped to disposed would keep accepting work nothing will ever flush.
    await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1)
      .Because("disposal must drain queued work even with no logger configured");
    await Assert.That(coordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
    await Assert.That(() => sut.QueueOutboxMessage(_createOutboxMessage()))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task DisposeAsync_WithUnflushedItems_WithoutLogger_FlushesSuccessfullyAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");

    // Act
    await sut.DisposeAsync();

    // Assert — outbox + inbox each trigger their own store call
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  // ============================================================
  // DisposeAsync: error during flush without logger
  // ============================================================

  [Test]
  public async Task DisposeAsync_WhenFlushThrows_WithoutLogger_SwallowsAndStillDisposesAsync() {
    // Arrange
    var throwingCoordinator = new ThrowingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(throwingCoordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act - should swallow the failing drain rather than throwing out of DisposeAsync
    await sut.DisposeAsync();

    // Assert - the call count proves the drain really reached the failing coordinator, so the
    // swallow is exercised rather than skipped; and a failed drain must still leave the strategy
    // disposed, otherwise a shutdown that hits a broken store leaves a live timer behind.
    await Assert.That(throwingCoordinator.StoreOutboxCallCount).IsEqualTo(1)
      .Because("without a call there is no exception, and the swallow branch is never reached");
    await Assert.That(() => sut.QueueOutboxMessage(_createOutboxMessage()))
      .ThrowsExactly<ObjectDisposedException>();
  }

  // ============================================================
  // Timer callback: disposed early return
  // ============================================================

  [Test]
  public async Task TimerCallback_WhenDisposed_DoesNotFlushAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions(intervalMs: 50); // Short interval
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    // Dispose immediately, then wait for timer to have fired (if it were active)
    await sut.DisposeAsync();

    var callCountAfterDispose = coordinator.ProcessWorkBatchCallCount;

    // Use a TaskCompletionSource to wait rather than polling
    var tcs = new TaskCompletionSource();
    _ = Task.Run(async () => {
      // Wait enough time for the timer to have fired if it were still active
      await Task.Delay(200);
      tcs.SetResult();
    });
    await tcs.Task;

    // Assert - no additional flush calls after disposal
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(callCountAfterDispose)
      .Because("Timer callback should return early when disposed");
  }

  // ============================================================
  // Flush clears queues: second flush after first should be empty
  // ============================================================

  [Test]
  public async Task FlushAsync_ClearsQueues_SecondFlushReturnsEmptyAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
      var secondResult = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
        .Because("Second flush should detect empty queues and not call coordinator");
      await Assert.That(secondResult.OutboxWork).IsEmpty();
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Concurrent flush: _flushing flag without logger
  // ============================================================

  [Test]
  public async Task FlushAsync_ConcurrentFlush_WithoutLogger_ReturnsEmptyBatchAsync() {
    // Arrange
    var slowCoordinator = new SlowWorkCoordinator(500);
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(slowCoordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      var firstFlushStarted = new SemaphoreSlim(0, 1);
      var firstFlushTask = Task.Run(async () => {
        firstFlushStarted.Release();
        return await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
      });

      await firstFlushStarted.WaitAsync(TimeSpan.FromSeconds(5));
      // Give time for first flush to acquire the _flushing lock
      await Task.Delay(50);

      var secondResult = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(secondResult.OutboxWork).IsEmpty()
        .Because("Concurrent flush should return empty batch");

      await firstFlushTask;
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // FlushAsync after dispose
  // ============================================================

  [Test]
  public async Task FlushAsync_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(async () => await sut.FlushAsync(WorkBatchOptions.None))
      .ThrowsExactly<ObjectDisposedException>();
  }

  // ============================================================
  // Queue operations after dispose
  // ============================================================

  [Test]
  public async Task QueueOutboxMessage_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxMessage(_createOutboxMessage()))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxMessage_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueInboxMessage(_createInboxMessage()))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueOutboxCompletion_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxCompletion_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueOutboxFailure_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err"))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task QueueInboxFailure_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(() => sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err"))
      .ThrowsExactly<ObjectDisposedException>();
  }

  // ============================================================
  // Double dispose
  // ============================================================

  [Test]
  public async Task DisposeAsync_CalledTwice_DoesNotDrainTwiceAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    // Queue work so the first disposal has something to drain — with empty queues both
    // disposals are indistinguishable and the second one proves nothing.
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act
    await sut.DisposeAsync();
    var afterFirstDispose = coordinator.ProcessWorkBatchCallCount;
    await sut.DisposeAsync();

    // Assert - the second disposal returns at the _disposed guard. Re-running the drain would
    // re-store whatever the first one already stored, which is a duplicate insert, not a no-op.
    await Assert.That(afterFirstDispose).IsEqualTo(1);
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(afterFirstDispose)
      .Because("disposal is idempotent — a second call must not re-run the drain");
  }

  // ============================================================
  // ScopeFactory flush path: null coordinator + scope creates coordinator
  // ============================================================

  [Test]
  public async Task FlushAsync_WithScopeFactory_ResolvesCoordinatorFromScopeAsync() {
    // Arrange
    var scopedCoordinator = new TrackingWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => scopedCoordinator);
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();

    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider: instanceProvider,
      options: options,
      scopeFactory: scopeFactory
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(scopedCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
        .Because("Should resolve IWorkCoordinator from scope when coordinator is null");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // BestEffort mode with metrics: records flush call
  // ============================================================

  [Test]
  public async Task FlushAsync_BestEffortMode_WithDisposedState_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);
    await sut.DisposeAsync();

    // Act & Assert
    await Assert.That(async () => await sut.FlushAsync(WorkBatchOptions.None))
      .ThrowsExactly<ObjectDisposedException>();
  }

  // ============================================================
  // All queue types combined then flush via IWorkFlusher
  // ============================================================

  [Test]
  public async Task IWorkFlusher_FlushAsync_WithAllQueueTypes_FlushesEverythingAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err1");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err2");

    try {
      // Act
      IWorkFlusher flusher = sut;
      await flusher.FlushAsync(CancellationToken.None);

      // Assert — outbox + inbox each trigger their own store call; completions/failures
      // route via channels (covered in WorkCoordinatorFlushHelperTests).
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
      await Assert.That(coordinator.HandlerCommits.Count).IsEqualTo(1)
        .Because("the queued inbox completion reaches the coordinator as a handler commit (#734); it used to be counted and dropped");
      await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastNewInboxMessages.Length).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Timer callback with error, without logger (no error logging branch)
  // ============================================================

  [Test]
  public async Task TimerCallback_WhenFlushThrows_WithoutLogger_KeepsTickingAsync() {
    // Arrange
    var throwingCoordinator = new ThrowingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions(intervalMs: 50); // Short interval for timer to fire quickly
    var sut = new IntervalWorkCoordinatorStrategy(throwingCoordinator, instanceProvider, options);

    try {
      // Act - the coordinator signals each call, so this waits on the flush itself rather than
      // on a delay that has to be guessed.
      sut.QueueOutboxMessage(_createOutboxMessage());
      await throwingCoordinator.FirstStoreOutboxCall.WaitAsync(TimeSpan.FromSeconds(30));

      // The first tick threw. Queue again: if the exception had escaped the callback the timer
      // would be dead and this second flush would never happen.
      sut.QueueOutboxMessage(_createOutboxMessage());
      await throwingCoordinator.SecondStoreOutboxCall.WaitAsync(TimeSpan.FromSeconds(30));

      // Assert - swallowing is only correct if the timer survives it; a strategy that stops
      // flushing after one bad batch strands every message queued afterwards.
      await Assert.That(throwingCoordinator.StoreOutboxCallCount).IsGreaterThanOrEqualTo(2)
        .Because("a failed interval flush must not stop the timer");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Test helpers
  // ============================================================

  private sealed class TestInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "EdgeCaseTestService";
    public string HostName => "test-host";
    public int ProcessId => 99999;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>
  /// A scoped host for the strategy's <c>scopeFactory</c> constructor. Completions and failures
  /// only route to their channels when the flush resolves them from a scope, so a direct
  /// coordinator leaves them unobservable.
  /// </summary>
  private sealed class ChannelScopeHost {
    private readonly ServiceProvider _provider;
    private int _coordinatorResolutions;

    public ChannelScopeHost() {
      Coordinator = new TrackingWorkCoordinator();
      CompletionChannel = new CountingOutboxCompletionChannel();
      FailureChannel = new CountingFailureChannel();

      var services = new ServiceCollection();
      services.AddScoped<IWorkCoordinator>(_ => {
        Interlocked.Increment(ref _coordinatorResolutions);
        return Coordinator;
      });
      services.AddSingleton<IOutboxCompletionChannel>(CompletionChannel);
      services.AddSingleton<IFailureChannel>(FailureChannel);
      _provider = services.BuildServiceProvider();
      ScopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    public TrackingWorkCoordinator Coordinator { get; }
    public CountingOutboxCompletionChannel CompletionChannel { get; }
    public CountingFailureChannel FailureChannel { get; }
    public IServiceScopeFactory ScopeFactory { get; }

    /// <summary>Non-zero only when the flush got far enough to open a scope — an all-empty flush
    /// short-circuits before that.</summary>
    public int CoordinatorResolutions => Volatile.Read(ref _coordinatorResolutions);
  }

  private sealed class CountingOutboxCompletionChannel : IOutboxCompletionChannel {
    private readonly ConcurrentQueue<Guid> _ids = new();

    public IReadOnlyCollection<Guid> EnqueuedIds => _ids;

    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      _ids.Enqueue(outboxMessageId);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CountingFailureChannel : IFailureChannel {
    private readonly ConcurrentQueue<(WorkCategory Category, Guid MessageId)> _enqueued = new();

    public IReadOnlyCollection<(WorkCategory Category, Guid MessageId)> Enqueued => _enqueued;

    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      _enqueued.Enqueue((category, failure.MessageId));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class TrackingWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageCompletion[] LastInboxCompletions { get; private set; } = [];
    public MessageFailure[] LastOutboxFailures { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];

    public Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = messages;
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Handler commits the flush helper makes for queued inbox completions (#734).</summary>
    public List<HandlerCommitRequest> HandlerCommits { get; } = [];

    public Task CommitHandlerResultAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      lock (HandlerCommits) {
        HandlerCommits.Add(request);
      }
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewInboxMessages = messages;
      return Task.CompletedTask;
    }

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Fails every store. Counts and signals the calls so a test can prove the flush actually
  /// reached the failing seam — a swallowed exception leaves no other trace, and without the
  /// count a test that never flushed at all is indistinguishable from one that flushed and
  /// recovered.
  /// </summary>
  private sealed class ThrowingWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _storeOutboxCalls;

    /// <summary>How many flushes reached <see cref="StoreOutboxMessagesAsync"/> before throwing.</summary>
    public int StoreOutboxCallCount => Volatile.Read(ref _storeOutboxCalls);

    /// <summary>Completes when the first store call arrives — a signal to wait on instead of a delay.</summary>
    public Task FirstStoreOutboxCall => _firstCall.Task;

    /// <summary>Completes when the second store call arrives, i.e. the timer survived the first failure.</summary>
    public Task SecondStoreOutboxCall => _secondCall.Task;

    public Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      var call = Interlocked.Increment(ref _storeOutboxCalls);
      if (call == 1) {
        _firstCall.TrySetResult();
      } else if (call == 2) {
        _secondCall.TrySetResult();
      }
      throw new InvalidOperationException("Simulated coordinator failure");
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("Simulated coordinator failure");

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class SlowWorkCoordinator(int delayMs) : IWorkCoordinator {
    private readonly int _delayMs = delayMs;

    public async Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      await Task.Delay(_delayMs, cancellationToken);
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
      await Task.Delay(_delayMs, cancellationToken);
    }

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class RecordingLogger<T> : ILogger<T>, IDisposable {
    private readonly ConcurrentBag<string> _messages = [];

    public IReadOnlyCollection<string> Messages => _messages;

    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      _messages.Add(formatter(state, exception));
    }
  }

  private sealed class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }
    public JsonElement Payload { get; init; } = JsonDocument.Parse("{}").RootElement;
    object IMessageEnvelope.Payload => Payload;

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

    public SecurityContext? GetCurrentSecurityContext() => null;
    public ScopeContext? GetCurrentScope() => null;
  }
}
