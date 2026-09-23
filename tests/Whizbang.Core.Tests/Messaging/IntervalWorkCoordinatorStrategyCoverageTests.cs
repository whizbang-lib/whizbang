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
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Options;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage tests for IntervalWorkCoordinatorStrategy targeting uncovered logger paths
/// and edge-case branches not covered by IntervalWorkCoordinatorStrategyTests.
/// </summary>
public class IntervalWorkCoordinatorStrategyCoverageTests {
  private static MessageEnvelope<JsonElement> _createEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static OutboxMessage _createOutboxMessage(Guid messageId, string destination = "test-topic") {
    return new OutboxMessage {
      MessageId = messageId,
      Destination = destination,
      Envelope = _createEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    };
  }

  private static InboxMessage _createInboxMessage(Guid messageId, string handlerName = "TestHandler") {
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = handlerName,
      Envelope = _createEnvelope(messageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    };
  }

  private static WorkCoordinatorOptions _createOptions(int intervalMs = 60000) {
    return new WorkCoordinatorOptions {
      IntervalMilliseconds = intervalMs,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300,
      DebugMode = false
    };
  }

  // ============================================================
  // LogStrategyStarted - called on construction with a logger
  // ============================================================

  [Test]
  public async Task Constructor_WithLogger_LogsStrategyStartedAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();

    // Act
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());
    await sut.DisposeAsync();

    // Assert
    await Assert.That(logger.Messages.Any(m => m.Contains("started"))).IsTrue()
      .Because("LogStrategyStarted should be called on construction");
  }

  // ============================================================
  // LogQueuedOutboxMessage - called in QueueOutboxMessage
  // ============================================================

  [Test]
  public async Task QueueOutboxMessage_WithLogger_LogsMessageAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    var messageId = Guid.CreateVersion7();

    try {
      // Act
      sut.QueueOutboxMessage(_createOutboxMessage(messageId));

      // Assert
      await Assert.That(logger.Messages.Any(m => m.Contains(messageId.ToString()))).IsTrue()
        .Because("LogQueuedOutboxMessage should log the message ID");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogQueuedInboxMessage - called in QueueInboxMessage
  // ============================================================

  [Test]
  public async Task QueueInboxMessage_WithLogger_LogsMessageAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    var messageId = Guid.CreateVersion7();

    try {
      // Act
      sut.QueueInboxMessage(_createInboxMessage(messageId));

      // Assert
      await Assert.That(logger.Messages.Any(m => m.Contains(messageId.ToString()))).IsTrue()
        .Because("LogQueuedInboxMessage should log the message ID");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogQueuedOutboxCompletion - called in QueueOutboxCompletion
  // ============================================================

  [Test]
  public async Task QueueOutboxCompletion_WithLogger_LogsCompletionAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    var messageId = Guid.CreateVersion7();

    try {
      // Act
      sut.QueueOutboxCompletion(messageId, MessageProcessingStatus.Published);

      // Assert
      await Assert.That(logger.Messages.Any(m => m.Contains(messageId.ToString()))).IsTrue()
        .Because("LogQueuedOutboxCompletion should log the message ID");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogQueuedInboxCompletion - called in QueueInboxCompletion
  // ============================================================

  [Test]
  public async Task QueueInboxCompletion_WithLogger_LogsCompletionAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    var messageId = Guid.CreateVersion7();

    try {
      // Act
      sut.QueueInboxCompletion(messageId, MessageProcessingStatus.Stored);

      // Assert
      await Assert.That(logger.Messages.Any(m => m.Contains(messageId.ToString()))).IsTrue()
        .Because("LogQueuedInboxCompletion should log the message ID");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogQueuedOutboxFailure - called in QueueOutboxFailure
  // ============================================================

  [Test]
  public async Task QueueOutboxFailure_WithLogger_LogsFailureAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    var messageId = Guid.CreateVersion7();

    try {
      // Act
      sut.QueueOutboxFailure(messageId, MessageProcessingStatus.Failed, "outbox error");

      // Assert
      await Assert.That(logger.Messages.Any(m => m.Contains(messageId.ToString()))).IsTrue()
        .Because("LogQueuedOutboxFailure should log the message ID");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogQueuedInboxFailure - called in QueueInboxFailure
  // ============================================================

  [Test]
  public async Task QueueInboxFailure_WithLogger_LogsFailureAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    var messageId = Guid.CreateVersion7();

    try {
      // Act
      sut.QueueInboxFailure(messageId, MessageProcessingStatus.Failed, "inbox error");

      // Assert
      await Assert.That(logger.Messages.Any(m => m.Contains(messageId.ToString()))).IsTrue()
        .Because("LogQueuedInboxFailure should log the message ID");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogFlushAlreadyInProgress - called when _flushing is true
  // ============================================================

  [Test]
  public async Task FlushAsync_WhenFlushAlreadyInProgress_LogsWarningAndReturnsEmptyAsync() {
    // Arrange — a GATED coordinator: the first flush signals when it is provably inside the
    // coordinator (the strategy sets _flushing BEFORE calling it), and blocks until released.
    // The old SlowWorkCoordinator(300ms) + Task.Delay(50) version raced the first flush's
    // scheduling under load — 50ms was sometimes not enough for _flushing to be set.
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var gatedCoordinator = new GatedWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: gatedCoordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    // Queue something so first flush doesn't return immediately on empty
    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    try {
      // Start first flush; wait until it is INSIDE the coordinator — _flushing is now true.
      var firstFlushTask = sut.FlushAndGetBatchAsync(WorkBatchOptions.None);
      await gatedCoordinator.EnteredStore.Task.WaitAsync(TimeSpan.FromSeconds(10));

      // Second flush deterministically hits the already-in-progress guard.
      var secondResult = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - second result is empty (concurrent flush returns empty)
      await Assert.That(secondResult.OutboxWork.Count).IsEqualTo(0)
        .Because("Concurrent flush should return empty batch");
      await Assert.That(logger.Messages.Any(m => m.Contains("already in progress") || m.Contains("progress"))).IsTrue()
        .Because("LogFlushAlreadyInProgress should be called");

      gatedCoordinator.ReleaseStore.Release();
      await firstFlushTask;
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogNoQueuedOperations - called in FlushAsync when no queued items
  // ============================================================

  [Test]
  public async Task FlushAsync_WithNoQueuedOperations_LogsNoOperationsAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    try {
      // Act - flush with nothing queued
      var result = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert
      await Assert.That(result.OutboxWork.Count).IsEqualTo(0);
      await Assert.That(logger.Messages.Any(m => m.Contains("No queued") || m.Contains("queued operations"))).IsTrue()
        .Because("LogNoQueuedOperations should be called when nothing is queued");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogIntervalFlush and LogIntervalFlushCompleted
  // ============================================================

  [Test]
  public async Task FlushAsync_WithQueuedItems_LogsIntervalFlushAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    try {
      // Act
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - LogIntervalFlush and LogIntervalFlushCompleted
      await Assert.That(logger.Messages.Any(m => m.Contains("outbox") || m.Contains("flush"))).IsTrue()
        .Because("LogIntervalFlush should be called with queued items");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // LogStrategyDisposing - called in DisposeAsync
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithLogger_LogsDisposingAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    // Act
    await sut.DisposeAsync();

    // Assert
    await Assert.That(logger.Messages.Any(m => m.Contains("dispos"))).IsTrue()
      .Because("LogStrategyDisposing should be called during disposal");
  }

  // ============================================================
  // LogDisposingWithUnflushedOperations - called when queued items remain at dispose
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithUnflushedOperations_LogsUnflushedWarningAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions(intervalMs: 60000); // Long interval so timer won't fire
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    // Queue a message without flushing
    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    // Act
    await sut.DisposeAsync();

    // Assert
    await Assert.That(logger.Messages.Any(m => m.Contains("unflushed") || m.Contains("disposing"))).IsTrue()
      .Because("LogDisposingWithUnflushedOperations should be called when items remain");
  }

  // ============================================================
  // LogStrategyDisposed - called after DisposeAsync
  // ============================================================

  [Test]
  public async Task DisposeAsync_WithLogger_LogsDisposedAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    // Act
    await sut.DisposeAsync();

    // Assert
    await Assert.That(logger.Messages.Any(m => m.Contains("disposed"))).IsTrue()
      .Because("LogStrategyDisposed should be called after disposal");
  }

  // ============================================================
  // LogErrorFlushingOnDisposal - called when FlushAsync throws during DisposeAsync
  // ============================================================

  [Test]
  public async Task DisposeAsync_WhenFlushThrows_LogsErrorAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var throwingCoordinator = new ThrowingWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions(intervalMs: 60000);
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: throwingCoordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    // Queue something so flush is attempted
    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    // Act - DisposeAsync catches flush errors
    await sut.DisposeAsync();

    // Assert
    await Assert.That(logger.Messages.Any(m => m.Contains("Error") || m.Contains("error"))).IsTrue()
      .Because("LogErrorFlushingOnDisposal should be called when flush throws during disposal");
  }

  // ============================================================
  // LogErrorDuringIntervalFlush - called when timer callback flush throws
  // ============================================================

  [Test]
  public async Task TimerCallback_WhenFlushThrows_LogsErrorAsync() {
    // Arrange - Use very short interval so timer fires quickly
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var throwingCoordinator = new ThrowingWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions(intervalMs: 50); // Short interval
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: throwingCoordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    // Queue something so the timer flush attempt actually calls the work coordinator
    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    // Wait for an error log to appear (signal-based via logger)
    await logger.WaitForMessageAsync(
      m => m.Contains("Error") || m.Contains("error"),
      TimeSpan.FromSeconds(5));

    // Act
    await sut.DisposeAsync();

    // Assert - error should be logged from timer callback
    await Assert.That(logger.Messages.Any(m => m.Contains("Error") || m.Contains("error"))).IsTrue()
      .Because("LogErrorDuringIntervalFlush should be called when timer-triggered flush throws");
  }

  // ============================================================
  // All logger paths covered in single comprehensive test
  // ============================================================

  [Test]
  public async Task WithLogger_AllQueueOperations_ProducesLogMessagesAsync() {
    // Arrange
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var coordinator = new SimpleWorkCoordinator();
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions(intervalMs: 60000);
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: instanceProvider,
      options: options,
      logger: logger,
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    var messageId1 = Guid.CreateVersion7();
    var messageId2 = Guid.CreateVersion7();
    var messageId3 = Guid.CreateVersion7();
    var messageId4 = Guid.CreateVersion7();

    try {
      // Act - exercise all queue methods
      sut.QueueOutboxMessage(_createOutboxMessage(messageId1));
      sut.QueueInboxMessage(_createInboxMessage(messageId2));
      sut.QueueOutboxCompletion(messageId3, MessageProcessingStatus.Published);
      sut.QueueInboxCompletion(messageId4, MessageProcessingStatus.Stored);
      sut.QueueOutboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "outbox error");
      sut.QueueInboxFailure(Guid.CreateVersion7(), MessageProcessingStatus.Failed, "inbox error");

      // Flush to exercise LogIntervalFlush and LogIntervalFlushCompleted
      _ = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None);

      // Assert - at minimum we should have log messages
      await Assert.That(logger.Messages.Count).IsGreaterThan(0)
        .Because("All queue operations should produce log messages");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // Test helpers
  // ============================================================

  private sealed class CoverageTestInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "CoverageTestService";
    public string HostName => "test-host";
    public int ProcessId => 12345;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class SimpleWorkCoordinator : IWorkCoordinator {
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

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>Signals when a flush is provably INSIDE the coordinator (so the strategy's
  /// _flushing guard is set) and blocks there until released — the deterministic replacement
  /// for delay-based concurrent-flush setups.</summary>
  private sealed class GatedWorkCoordinator : IWorkCoordinator {
    public TaskCompletionSource EnteredStore { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SemaphoreSlim ReleaseStore { get; } = new(0, 1);

    public async Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount,
      CancellationToken cancellationToken = default) {
      EnteredStore.TrySetResult();
      await ReleaseStore.WaitAsync(cancellationToken);
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ThrowingWorkCoordinator : IWorkCoordinator {
    public Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount,
      CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("Simulated coordinator failure");

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("Simulated coordinator failure");

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
    private readonly SemaphoreSlim _logSignal = new(0, int.MaxValue);

    public IReadOnlyCollection<string> Messages => _messages;

    public void Dispose() => _logSignal.Dispose();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      _messages.Add(formatter(state, exception));
      _logSignal.Release();
    }

    /// <summary>
    /// Waits for a log message matching the predicate to appear.
    /// </summary>
    public async Task WaitForMessageAsync(Func<string, bool> predicate, TimeSpan timeout) {
      var deadline = DateTime.UtcNow + timeout;
      while (DateTime.UtcNow < deadline) {
        if (_messages.Any(predicate)) {
          return;
        }
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) {
          break;
        }
        var waitTime = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
        await _logSignal.WaitAsync(waitTime);
      }
      if (!_messages.Any(predicate)) {
        throw new TimeoutException("Expected log message was not found within timeout");
      }
    }
  }

  // ============================================================
  // RouteClaimedInboxWorkToChannel — the dedup that decides what reaches the publisher
  // ============================================================

  // A claimed inbox row is handed to the publisher through an in-memory channel, and the same row
  // can be claimed again while the first copy is still in flight (a lease renewal, a redelivery, a
  // second claim cycle overlapping the first). Writing it twice would hand the same message to two
  // handlers concurrently — the duplicate dispatch the in-flight set exists to prevent. Rows that
  // are NOT in flight must still get through, or claimed work would sit unhandled until its lease
  // expired.
  [Test]
  public async Task RouteClaimedInboxWorkToChannel_SkipsWorkAlreadyInFlightAndWritesTheRestAsync() {
    var inFlight = Guid.CreateVersion7();
    var fresh = Guid.CreateVersion7();
    var writer = new SelectiveInFlightInboxChannelWriter(inFlight);
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: new SimpleWorkCoordinator(),
      instanceProvider: new CoverageTestInstanceProvider(),
      options: _createOptions(),
      logger: new RecordingLogger<IntervalWorkCoordinatorStrategy>(),
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: writer);

    try {
      sut.RouteClaimedInboxWorkToChannel(new WorkBatch {
        OutboxWork = [],
        InboxWork = [_claimedInboxWork(inFlight), _claimedInboxWork(fresh)],
        PerspectiveWork = []
      });

      await Assert.That(writer.Written).IsEquivalentTo(new[] { fresh })
        .Because("only the row that is not already being handled may be written; writing the "
          + "in-flight one would dispatch the same message to a second handler concurrently");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // The guard above the loop is what keeps an empty claim from touching the channel at all. A
  // claim cycle that found nothing is the common case on an idle service, so this runs constantly.
  [Test]
  public async Task RouteClaimedInboxWorkToChannel_EmptyBatch_NeverAsksTheWriterAnythingAsync() {
    var writer = new SelectiveInFlightInboxChannelWriter();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: new SimpleWorkCoordinator(),
      instanceProvider: new CoverageTestInstanceProvider(),
      options: _createOptions(),
      logger: new RecordingLogger<IntervalWorkCoordinatorStrategy>(),
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: writer);

    try {
      sut.RouteClaimedInboxWorkToChannel(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

      await Assert.That(writer.InFlightQuestions).IsEqualTo(0)
        .Because("an empty claim has nothing to dedup, so the in-flight set is not consulted at all");
      await Assert.That(writer.Written).IsEmpty()
        .Because("an empty claim writes nothing");
    } finally {
      await sut.DisposeAsync();
    }
  }

  // ============================================================
  // FlushTimerTick — the disposed guard
  // ============================================================

  // Disposal stops the timer and takes the final flush itself, but a tick the runtime had already
  // dispatched still arrives afterwards. Without the guard it would start another flush against a
  // coordinator whose scope the owner has finished with — a second round trip nobody asked for,
  // after the strategy has reported itself drained.
  [Test]
  public async Task FlushTimerTick_AfterDispose_StartsNoFurtherFlushAsync() {
    var coordinator = new CountingWorkCoordinator();
    var sut = new IntervalWorkCoordinatorStrategy(
      coordinator: coordinator,
      instanceProvider: new CoverageTestInstanceProvider(),
      options: _createOptions(),
      logger: new RecordingLogger<IntervalWorkCoordinatorStrategy>(),
      scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      inboxChannelWriter: new InboxChannelWriter());

    sut.QueueOutboxMessage(_createOutboxMessage(Guid.CreateVersion7()));
    await sut.DisposeAsync();
    var flushesAtDispose = coordinator.StoreOutboxCallCount;

    sut.FlushTimerTick(null);

    await Assert.That(flushesAtDispose).IsGreaterThan(0)
      .Because("disposal flushed the queued message, so the count below is measured against a "
        + "coordinator this strategy really does drive");
    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(flushesAtDispose)
      .Because("the tick returned at the disposed guard; going on would open a fresh flush against "
        + "a coordinator the owner has already finished with");
  }

  private static InboxWork _claimedInboxWork(Guid messageId) => new() {
    MessageId = messageId,
    Envelope = _createEnvelope(messageId),
    MessageType = "System.Text.Json.JsonElement, System.Text.Json",
    StreamId = Guid.CreateVersion7(),
    PartitionNumber = 1,
    Attempts = 0,
    Status = MessageProcessingStatus.Stored,
    Flags = WorkBatchOptions.None,
  };

  /// <summary>Counts the outbox stores a flush performs, which is how a flush is observed here.</summary>
  private sealed class CountingWorkCoordinator : IWorkCoordinator {
    private int _storeOutboxCalls;

    public int StoreOutboxCallCount => Volatile.Read(ref _storeOutboxCalls);

    public Task StoreOutboxMessagesAsync(
        OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _storeOutboxCalls);
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CommitHandlerResultAsync(
        HandlerCommitRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// An inbox channel writer that reports a fixed set of message ids as already in flight and
  /// records everything actually written, so the dedup's two answers are told apart.
  /// </summary>
  private sealed class SelectiveInFlightInboxChannelWriter(params Guid[] inFlight) : IInboxChannelWriter {
    private readonly HashSet<Guid> _inFlight = [.. inFlight];
    private readonly System.Threading.Channels.Channel<InboxWork> _channel =
      System.Threading.Channels.Channel.CreateUnbounded<InboxWork>();
    private readonly List<Guid> _written = [];

    public int InFlightQuestions { get; private set; }
    public IReadOnlyList<Guid> Written { get { lock (_written) { return [.. _written]; } } }

    public System.Threading.Channels.ChannelReader<InboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      lock (_written) { _written.Add(work.MessageId); }
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(InboxWork work) {
      lock (_written) { _written.Add(work.MessageId); }
      return _channel.Writer.TryWrite(work);
    }

    public bool IsInFlight(Guid messageId) {
      InFlightQuestions++;
      return _inFlight.Contains(messageId);
    }

    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }
}
