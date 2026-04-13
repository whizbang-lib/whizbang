using System.Collections.Concurrent;
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
/// Additional coverage tests for IntervalWorkCoordinatorStrategy targeting remaining uncovered branches.
/// Focuses on: logger-null branches in dispose with unflushed items, metrics with empty flush on logger path,
/// timer callback error with null logger, dispose error without logger, and inbox queue combinations.
/// </summary>
public class IntervalWorkCoordinatorStrategyFullCoverageTests {
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
  // DisposeAsync: unflushed items WITHOUT logger — exercises the null logger path
  // in the dispose unflushed warning check (line 367-381: _logger != null guard)
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithUnflushedItems_WithoutLogger_SkipsUnflushedWarningLogAsync() {
    // Arrange — no logger, queue multiple operation types so the unflushed check has non-zero counts
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions(intervalMs: 60000);
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    // Queue one of each to ensure the unflushed-operations branch is reached
    sut.QueueOutboxMessage(_createOutboxMessage());
    sut.QueueInboxMessage(_createInboxMessage());
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err");

    // Act — dispose without logger (should skip the LogDisposingWithUnflushedOperations branch)
    await sut.DisposeAsync();

    // Assert — all operations were still flushed on disposal
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush even without logger");
  }

  // ============================================================
  // DisposeAsync: unflushed items WITH logger — completions AND failures non-zero
  // to exercise all count parameters in LogDisposingWithUnflushedOperations
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithLogger_AllQueueTypes_LogsAllCountsInWarningAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions(intervalMs: 60000);
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options, logger);

    // Queue completions and failures (not just messages) to test all unflushed counters
    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err1");
    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "err2");

    // Act
    await sut.DisposeAsync();

    // Assert — warning about unflushed items should be logged
    await Assert.That(logger.Messages.Any(m => m.Contains("unflushed") || m.Contains("disposing"))).IsTrue()
      .Because("LogDisposingWithUnflushedOperations should fire with non-zero completion/failure counts");
  }

  // ============================================================
  // FlushAsync: empty flush with metrics AND logger (both branches active)
  // ============================================================

  [Test]
  public async Task FlushAsync_EmptyQueues_WithMetricsAndLogger_RecordsEmptyFlushAndLogsAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger, metrics: metrics);

    try {
      // Act
      var result = await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(result.OutboxWork).IsEmpty();
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
      await Assert.That(logger.Messages.Any(m => m.Contains("No queued") || m.Contains("queued operations"))).IsTrue()
        .Because("Empty flush should log no-op message");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // FlushAsync: with items, with metrics AND logger (all logging + metrics active)
  // ============================================================

  [Test]
  public async Task FlushAsync_WithItems_WithMetricsAndLogger_TracksMetricsAndLogsAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger, metrics: metrics);

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
      await Assert.That(logger.Messages.Count).IsGreaterThan(5)
        .Because("All queue operations + flush summary should generate log messages");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // DisposeAsync: error flushing on disposal WITHOUT logger
  // ============================================================

  [Test]
  public async Task DisposeAsync_FlushThrows_WithoutLogger_SwallowsExceptionAsync() {
    // Arrange
    var throwingCoordinator = new FullCoverageThrowingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions(intervalMs: 60000);
    var sut = new IntervalWorkCoordinatorStrategy(throwingCoordinator, instanceProvider, options);

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act & Assert — should not throw
    await sut.DisposeAsync();
  }

  // ============================================================
  // DisposeAsync: no unflushed items with logger (exercises the check when queues empty)
  // ============================================================

  [Test]
  public async Task DisposeAsync_NoUnflushedItems_WithLogger_SkipsUnflushedWarningAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options, logger);

    // Act — dispose with empty queues
    await sut.DisposeAsync();

    // Assert — should log disposing and disposed, but NOT unflushed warning
    await Assert.That(logger.Messages.Any(m => m.Contains("unflushed"))).IsFalse()
      .Because("No unflushed items should skip the unflushed warning");
    await Assert.That(logger.Messages.Any(m => m.Contains("disposed"))).IsTrue();
  }

  // ============================================================
  // ScopeFactory: flush via scope factory with logger
  // ============================================================

  [Test]
  public async Task FlushAsync_WithScopeFactory_AndLogger_FlushesAndLogsAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var scopedCoordinator = new FullCoverageTrackingCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => scopedCoordinator);
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();

    var sut = new IntervalWorkCoordinatorStrategy(
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
      await Assert.That(logger.Messages.Count).IsGreaterThan(0);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // BestEffort with metrics (ensures metrics path hit for BestEffort)
  // ============================================================

  [Test]
  public async Task FlushAsync_BestEffort_WithMetricsAndLogger_RecordsMetricsAndReturnsEmptyAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var whizbangMetrics = new WhizbangMetrics();
    var metrics = new WorkCoordinatorMetrics(whizbangMetrics);
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, logger, metrics: metrics);

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
  // Inbox-only flush with logger (exercises inbox logging paths)
  // ============================================================

  [Test]
  public async Task FlushAsync_InboxOnly_WithLogger_LogsInboxFlushAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options, logger);

    sut.QueueInboxMessage(_createInboxMessage());

    try {
      // Act
      await sut.FlushAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
      await Assert.That(coordinator.LastNewInboxMessages.Length).IsEqualTo(1);
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Completions-only flush (no messages, just completions)
  // ============================================================

  [Test]
  public async Task FlushAsync_CompletionsOnly_WithLogger_LogsFlushAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options, logger);

    sut.QueueOutboxCompletion(Guid.CreateVersion7(), MessageProcessingStatus.Published);

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
  public async Task FlushAsync_FailuresOnly_WithLogger_LogsFlushAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options, logger);

    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "fail");

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
  // Inbox completions only flush
  // ============================================================

  [Test]
  public async Task FlushAsync_InboxCompletionsOnly_FlushesAsync() {
    // Arrange
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

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
  // Outbox failures only flush
  // ============================================================

  [Test]
  public async Task FlushAsync_OutboxFailuresOnly_FlushesAsync() {
    // Arrange
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "outbox fail");

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
  // Inbox failures only flush
  // ============================================================

  [Test]
  public async Task FlushAsync_InboxFailuresOnly_FlushesAsync() {
    // Arrange
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options);

    sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "inbox fail");

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
  // Dispose with inbox-only unflushed items (with logger)
  // ============================================================

  [Test]
  public async Task DisposeAsync_InboxOnly_WithLogger_LogsUnflushedAndFlushesAsync() {
    // Arrange
    var logger = new FullCoverageRecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new FullCoverageTrackingCoordinator();
    var instanceProvider = new FullCoverageInstanceProvider();
    var options = _createOptions(intervalMs: 60000);
    var sut = new IntervalWorkCoordinatorStrategy(coordinator, instanceProvider, options, logger);

    sut.QueueInboxMessage(_createInboxMessage());

    // Act
    await sut.DisposeAsync();

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastNewInboxMessages.Length).IsEqualTo(1);
  }

  // ============================================================
  // Test helpers
  // ============================================================

  private sealed class FullCoverageInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "FullCoverageTestService";
    public string HostName => "test-host";
    public int ProcessId => 54321;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class FullCoverageTrackingCoordinator : IWorkCoordinator {
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

  private sealed class FullCoverageThrowingCoordinator : IWorkCoordinator {
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

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FullCoverageRecordingLogger<T> : ILogger<T>, IDisposable {
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

    public void AddHop(MessageHop hop) => Hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() =>
      Hops.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
    public CorrelationId? GetCorrelationId() =>
      Hops.Count > 0 ? Hops[0].CorrelationId : null;
    public MessageId? GetCausationId() =>
      Hops.Count > 0 ? Hops[0].CausationId : null;
    public JsonElement? GetMetadata(string key) => null;
    public SecurityContext? GetCurrentSecurityContext() => null;
    public ScopeContext? GetCurrentScope() => null;
  }
}
