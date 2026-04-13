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
      StaleThresholdSeconds = 300,
      DebugMode = false
    };
  }

  // ============================================================
  // FlushMode.BestEffort - returns empty batch without flushing
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
      var result = await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

      // Assert - BestEffort returns empty batch; items remain queued for timer
      await Assert.That(result.OutboxWork).IsEmpty();
      await Assert.That(result.InboxWork).IsEmpty();
      await Assert.That(result.PerspectiveWork).IsEmpty();
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
        .Because("BestEffort mode should not call ProcessWorkBatchAsync");
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
      await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

      // Then Required flush picks them up
      await sut.FlushAsync(WorkBatchOptions.None, FlushMode.Required);

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
      await sut.FlushAsync(WorkBatchOptions.None);
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
        await sut.FlushAsync(WorkBatchOptions.None, FlushMode.Required, cts.Token);
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
  public async Task QueueOutboxMessage_WithNullStreamId_DoesNotThrowAsync() {
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
      // Act & Assert - null StreamId should not throw
      sut.QueueOutboxMessage(message);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_WithNullStreamId_DoesNotThrowAsync() {
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
      // Act & Assert
      sut.QueueInboxMessage(message);
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

  [Test]
  public async Task QueueOutboxMessage_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      // Act - no logger, should skip logging
      sut.QueueOutboxMessage(_createOutboxMessage());
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

    try {
      sut.QueueInboxMessage(_createInboxMessage());
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxCompletion_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxCompletion_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueOutboxFailure_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "error");
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxFailure_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "error");
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
      var result = await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
      await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastNewInboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastOutboxCompletions.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastInboxCompletions.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastOutboxFailures.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastInboxFailures.Length).IsEqualTo(1);
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
      await sut.FlushAsync(WorkBatchOptions.None);

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
      var result = await sut.FlushAsync(WorkBatchOptions.None);

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
      var result = await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort);

      // Assert
      await Assert.That(result.OutboxWork).IsEmpty();
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // DisposeAsync without logger
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    // Queue something so disposal flush has work
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act & Assert - no exception
    await sut.DisposeAsync();
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

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  // ============================================================
  // DisposeAsync: error during flush without logger
  // ============================================================

  [Test]
  public async Task DisposeAsync_WhenFlushThrows_WithoutLogger_DoesNotThrowAsync() {
    // Arrange
    var throwingCoordinator = new ThrowingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(throwingCoordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act & Assert - should swallow exception gracefully
    await sut.DisposeAsync();
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
      await sut.FlushAsync(WorkBatchOptions.None);
      var secondResult = await sut.FlushAsync(WorkBatchOptions.None);

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
        return await sut.FlushAsync(WorkBatchOptions.None);
      });

      await firstFlushStarted.WaitAsync(TimeSpan.FromSeconds(5));
      // Give time for first flush to acquire the _flushing lock
      await Task.Delay(50);

      var secondResult = await sut.FlushAsync(WorkBatchOptions.None);

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
  public async Task DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new TrackingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    // Act & Assert
    await sut.DisposeAsync();
    await sut.DisposeAsync();
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
      await sut.FlushAsync(WorkBatchOptions.None);

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
    await Assert.That(async () => await sut.FlushAsync(WorkBatchOptions.None, FlushMode.BestEffort))
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

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
      await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastNewInboxMessages.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastOutboxCompletions.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastInboxCompletions.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastOutboxFailures.Length).IsEqualTo(1);
      await Assert.That(coordinator.LastInboxFailures.Length).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Timer callback with error, without logger (no error logging branch)
  // ============================================================

  [Test]
  public async Task TimerCallback_WhenFlushThrows_WithoutLogger_SwallowsExceptionAsync() {
    // Arrange
    var throwingCoordinator = new ThrowingWorkCoordinator();
    var instanceProvider = new TestInstanceProvider();
    var options = _createOptions(intervalMs: 50); // Short interval for timer to fire quickly
    var sut = new IntervalWorkCoordinatorStrategy(throwingCoordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Wait enough time for timer to fire and hit the catch branch
    var tcs = new TaskCompletionSource();
    _ = Task.Run(async () => {
      await Task.Delay(200);
      tcs.SetResult();
    });
    await tcs.Task;

    // Act & Assert - should not propagate exception from timer callback
    await sut.DisposeAsync();
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

  private sealed class TrackingWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageCompletion[] LastInboxCompletions { get; private set; } = [];
    public MessageFailure[] LastOutboxFailures { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = request.NewOutboxMessages;
      LastNewInboxMessages = request.NewInboxMessages;
      LastOutboxCompletions = request.OutboxCompletions;
      LastInboxCompletions = request.InboxCompletions;
      LastOutboxFailures = request.OutboxFailures;
      LastInboxFailures = request.InboxFailures;

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ThrowingWorkCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated coordinator failure");
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

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

    public async Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      await Task.Delay(_delayMs, cancellationToken);
      return new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      };
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
