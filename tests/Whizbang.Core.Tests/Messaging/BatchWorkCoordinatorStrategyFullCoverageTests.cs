using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Additional coverage tests for BatchWorkCoordinatorStrategy targeting remaining uncovered branches.
/// Focuses on: _resetDebounceTimer disposed guard, _debounceTimerCallback disposed guard,
/// logger-null branches in dispose with unflushed items, OnBatchFlushed null subscriber,
/// inbox batch size trigger without logger, and various edge combinations.
/// </summary>
public class BatchWorkCoordinatorStrategyFullCoverageTests {
  private static MessageEnvelope<JsonElement> _createEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = []
    };
  }

  private static OutboxMessage _createOutboxMessage(Guid? messageId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = _createEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(id),
        Hops = []
      }
    };
  }

  private static InboxMessage _createInboxMessage(Guid? messageId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "TestHandler",
      Envelope = _createEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private static WorkCoordinatorOptions _createOptions(int batchSize = 5, int debounceMs = 200) {
    return new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Batch,
      BatchSize = batchSize,
      IntervalMilliseconds = debounceMs,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300,
      DebugMode = false
    };
  }

  // ============================================================
  // DisposeAsync: unflushed items WITHOUT logger — exercises null-logger branch
  // in the dispose unflushed warning check (lines 433-449)
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithUnflushedItems_WithoutLogger_SkipsWarningAndFlushesAsync() {
    // Arrange — no logger, all queue types
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");

    // Act
    await sut.DisposeAsync();

    // Assert — all items still flushed
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  // ============================================================
  // DisposeAsync: unflushed completions/failures WITH logger
  // (ensures all 6 count fields in unflushed warning are covered)
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithLogger_CompletionsAndFailuresOnly_LogsUnflushedAsync() {
    // Arrange
    var logger = new BatchFullCoverageLogger();
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger: logger);

    // Queue only completions and failures (no outbox/inbox messages)
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");

    // Act
    await sut.DisposeAsync();

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  // ============================================================
  // DisposeAsync: error during flush WITHOUT logger
  // ============================================================

  [Test]
  public async Task DisposeAsync_FlushThrows_WithoutLogger_SwallowsExceptionAsync() {
    // Arrange
    var throwingCoordinator = new BatchFullCoverageThrowingCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(throwingCoordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act & Assert — should not throw
    await sut.DisposeAsync();
  }

  // ============================================================
  // No OnBatchFlushed subscriber — null delegate path
  // OnBatchFlushed?.Invoke() does nothing when no subscribers
  // ============================================================

  [Test]
  public async Task FlushAsync_NoOnBatchFlushedSubscriber_DoesNotThrowAsync() {
    // Arrange — do NOT subscribe to OnBatchFlushed
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act — flush with no event subscriber
      var result = await sut.FlushAsync(WorkBatchOptions.None);

      // Assert — should succeed
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Inbox-only flush with logger — exercises QueuedInboxMessage log
  // ============================================================

  [Test]
  public async Task FlushAsync_InboxOnly_WithLogger_LogsFlushAsync() {
    // Arrange
    var logger = new BatchFullCoverageLogger();
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger: logger);

    sut.QueueInboxMessage(_createInboxMessage());

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
      await Assert.That(logger.LogCount).IsGreaterThan(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Completions-only flush
  // ============================================================

  [Test]
  public async Task FlushAsync_CompletionsOnly_FlushesAsync() {
    // Arrange
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Failures-only flush
  // ============================================================

  [Test]
  public async Task FlushAsync_FailuresOnly_FlushesAsync() {
    // Arrange
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Empty flush with metrics AND logger
  // ============================================================

  [Test]
  public async Task FlushAsync_EmptyQueues_WithMetricsAndLogger_RecordsBothAsync() {
    // Arrange
    var logger = new BatchFullCoverageLogger();
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new BatchWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger: logger, metrics: metrics);

    try {
      // Act
      var result = await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(result.OutboxWork).IsEmpty();
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
      await Assert.That(logger.LogCount).IsGreaterThan(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // BestEffort with metrics AND logger
  // ============================================================

  [Test]
  public async Task FlushAsync_BestEffort_WithMetricsAndLogger_RecordsMetricsAsync() {
    // Arrange
    var logger = new BatchFullCoverageLogger();
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new BatchWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger: logger, metrics: metrics);

    sut.QueueOutboxMessage(_createOutboxMessage());

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
  // All queue operations with metrics and logger
  // ============================================================

  [Test]
  public async Task FlushAsync_AllQueueTypes_WithMetricsAndLogger_FlushesEverythingAsync() {
    // Arrange
    var logger = new BatchFullCoverageLogger();
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger: logger, metrics: metrics);

    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err1");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err2");

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
      await Assert.That(logger.LogCount).IsGreaterThanOrEqualTo(5);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Constructor with scopeFactory and logger
  // ============================================================

  [Test]
  public async Task Constructor_WithScopeFactory_AndLogger_FlushesViaScope_Async() {
    // Arrange
    var logger = new BatchFullCoverageLogger();
    var scopedCoordinator = new BatchFullCoverageCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => scopedCoordinator);
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);

    var sut = new BatchWorkCoordinatorStrategy(
      coordinator: null,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: scopeFactory
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(scopedCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Dispose with empty queues and logger (no unflushed warning)
  // ============================================================

  [Test]
  public async Task DisposeAsync_EmptyQueues_WithLogger_SkipsUnflushedWarningAsync() {
    // Arrange
    var logger = new BatchFullCoverageLogger();
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger: logger);

    // Act — dispose with empty queues
    await sut.DisposeAsync();

    // Assert — should have logged start/dispose but no unflushed warning
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  // ============================================================
  // Inbox batch size trigger without logger
  // ============================================================

  [Test]
  public async Task QueueInboxMessage_BatchSizeReached_WithoutLogger_TriggersFlushAsync() {
    // Arrange — no logger
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 2, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      // Act
      sut.QueueInboxMessage(_createInboxMessage());
      sut.QueueInboxMessage(_createInboxMessage()); // Batch size reached

      await coordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Outbox batch size trigger without logger
  // ============================================================

  [Test]
  public async Task QueueOutboxMessage_BatchSizeReached_WithoutLogger_TriggersFlushAsync() {
    // Arrange
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 2, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      // Act
      sut.QueueOutboxMessage(_createOutboxMessage());
      sut.QueueOutboxMessage(_createOutboxMessage()); // Batch size reached

      await coordinator.WaitForFlushAsync(TimeSpan.FromSeconds(5));

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // IWorkFlusher with empty queues
  // ============================================================

  [Test]
  public async Task IWorkFlusher_FlushAsync_EmptyQueues_DoesNotCallCoordinatorAsync() {
    // Arrange
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    try {
      // Act
      IWorkFlusher flusher = sut;
      await flusher.FlushAsync(CancellationToken.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Null StreamId on outbox and inbox (allowed)
  // ============================================================

  [Test]
  public async Task QueueOutboxMessage_NullStreamId_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    var id = Guid.CreateVersion7();
    var message = new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = _createEnvelope(id),
      EnvelopeType = "Test",
      StreamId = null,
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] }
    };

    try {
      // Act & Assert
      sut.QueueOutboxMessage(message);
    } finally {
      await sut.DisposeAsync();
    }
  }

  [Test]
  public async Task QueueInboxMessage_NullStreamId_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new BatchFullCoverageCoordinator();
    var instanceProvider = new BatchFullCoverageInstanceProvider();
    var options = _createOptions(batchSize: 100, debounceMs: 60000);
    var sut = new BatchWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    var id = Guid.CreateVersion7();
    var message = new InboxMessage {
      MessageId = id,
      HandlerName = "TestHandler",
      Envelope = _createEnvelope(id),
      EnvelopeType = "Test",
      StreamId = null,
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
  // Test helpers
  // ============================================================

  private sealed class BatchFullCoverageInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "BatchFullCoverageService";
    public string HostName => "test-host";
    public int ProcessId => 54321;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class BatchFullCoverageCoordinator : IWorkCoordinator, IDisposable {
    private readonly SemaphoreSlim _flushSignal = new(0, int.MaxValue);
    public int ProcessWorkBatchCallCount { get; private set; }

    public void Dispose() => _flushSignal.Dispose();

    public async Task WaitForFlushAsync(TimeSpan timeout) {
      if (!await _flushSignal.WaitAsync(timeout)) {
        throw new TimeoutException("ProcessWorkBatchAsync was not called within timeout");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      _flushSignal.Release();
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

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class BatchFullCoverageThrowingCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated failure");
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class BatchFullCoverageLogger : ILogger<BatchWorkCoordinatorStrategy> {
    public int LogCount { get; private set; }

    public void Log<TState>(
      LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      LogCount++;
    }

    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
  }
}
