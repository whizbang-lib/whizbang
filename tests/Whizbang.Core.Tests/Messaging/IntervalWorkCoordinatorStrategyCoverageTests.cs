using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

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
      Hops = []
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
      StaleThresholdSeconds = 300,
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
      coordinator, instanceProvider, options, logger);
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
      coordinator, instanceProvider, options, logger);

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
      coordinator, instanceProvider, options, logger);

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
      coordinator, instanceProvider, options, logger);

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
      coordinator, instanceProvider, options, logger);

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
      coordinator, instanceProvider, options, logger);

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
      coordinator, instanceProvider, options, logger);

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
    // Arrange - Use a slow coordinator that will keep flush in progress long enough
    var logger = new RecordingLogger<IntervalWorkCoordinatorStrategy>();
    var slowCoordinator = new SlowWorkCoordinator(delayMs: 300);
    var instanceProvider = new CoverageTestInstanceProvider();
    var options = _createOptions();
    var sut = new IntervalWorkCoordinatorStrategy(
      slowCoordinator, instanceProvider, options, logger);

    // Queue something so first flush doesn't return immediately on empty
    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    try {
      // Start first flush (which will take 300ms due to slow coordinator)
      var firstFlushTask = sut.FlushAsync(WorkBatchFlags.None);

      // Give the first flush a moment to set _flushing = true
      await Task.Delay(50);

      // Second flush should detect concurrent flush in progress
      var secondResult = await sut.FlushAsync(WorkBatchFlags.None);

      // Assert - second result is empty (concurrent flush returns empty)
      await Assert.That(secondResult.OutboxWork.Count).IsEqualTo(0)
        .Because("Concurrent flush should return empty batch");
      await Assert.That(logger.Messages.Any(m => m.Contains("already in progress") || m.Contains("progress"))).IsTrue()
        .Because("LogFlushAlreadyInProgress should be called");

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
      coordinator, instanceProvider, options, logger);

    try {
      // Act - flush with nothing queued
      var result = await sut.FlushAsync(WorkBatchFlags.None);

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
      coordinator, instanceProvider, options, logger);

    var messageId = Guid.CreateVersion7();
    sut.QueueOutboxMessage(_createOutboxMessage(messageId));

    try {
      // Act
      await sut.FlushAsync(WorkBatchFlags.None);

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
      coordinator, instanceProvider, options, logger);

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
      coordinator, instanceProvider, options, logger);

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
      coordinator, instanceProvider, options, logger);

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
      throwingCoordinator, instanceProvider, options, logger);

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
      throwingCoordinator, instanceProvider, options, logger);

    // Queue something so the timer flush attempt actually calls ProcessWorkBatchAsync
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
      coordinator, instanceProvider, options, logger);

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
      await sut.FlushAsync(WorkBatchFlags.None);

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
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
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
}
