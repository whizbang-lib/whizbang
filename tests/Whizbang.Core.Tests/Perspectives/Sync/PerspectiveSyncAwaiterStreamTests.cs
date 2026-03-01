using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="IPerspectiveSyncAwaiter.WaitForStreamAsync"/> method.
/// The stream-based approach extracts StreamId from the message and queries the database.
/// </summary>
/// <remarks>
/// These tests expect the new constructor signature without IScopedEventTracker.
/// They will fail (RED) until the implementation is updated (GREEN).
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class PerspectiveSyncAwaiterStreamTests {
  /// <summary>
  /// Dummy perspective type for tests.
  /// </summary>
  private sealed class TestPerspective { }

  /// <summary>
  /// Dummy event type for filter tests.
  /// </summary>
  private sealed record TestEvent(Guid Id);

  // ==========================================================================
  // WaitForStreamAsync - Basic Success/Failure Tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_ReturnsSync_WhenNoPendingEventsAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 0, processedCount: 0);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    // This constructor call expects the NEW signature (without IScopedEventTracker)
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  [Test]
  public async Task WaitForStreamAsync_ReturnsSync_WhenAllEventsProcessedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 0, processedCount: 5);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(5);
  }

  [Test]
  public async Task WaitForStreamAsync_ReturnsTimedOut_WhenEventsPendingAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 3, processedCount: 2);
    var clock = new StubDebuggerAwareClock(timedOut: true);
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromMilliseconds(1));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
  }

  // ==========================================================================
  // WaitForStreamAsync - Cancellation Tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_RespectsCancellationAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 99, processedCount: 0);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger);
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await awaiter.WaitForStreamAsync(
            typeof(TestPerspective),
            streamId,
            eventTypes: null,
            timeout: TimeSpan.FromSeconds(5),
            ct: cts.Token));
  }

  // ==========================================================================
  // WaitForStreamAsync - Event Type Filter Tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_WithNullEventTypes_WaitsForAllAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 0, processedCount: 3);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  // ==========================================================================
  // WaitForStreamAsync - Result Properties Tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_ReturnsProcessedCountAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 0, processedCount: 7);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.EventsAwaited).IsEqualTo(7);
  }

  [Test]
  public async Task WaitForStreamAsync_ReturnsElapsedTimeAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 0, processedCount: 1);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.ElapsedTime).IsGreaterThanOrEqualTo(TimeSpan.Zero);
  }

  // ==========================================================================
  // Constructor Tests - New Signature (without IScopedEventTracker)
  // ==========================================================================

  [Test]
  public async Task Constructor_ThrowsOnNullCoordinatorAsync() {
    // Arrange
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(new PerspectiveSyncAwaiter(null!, clock, logger)));
  }

  [Test]
  public async Task Constructor_ThrowsOnNullClockAsync() {
    // Arrange
    var coordinator = new StubWorkCoordinator(0, 0);
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(new PerspectiveSyncAwaiter(coordinator, null!, logger)));
  }

  [Test]
  public async Task Constructor_ThrowsOnNullLoggerAsync() {
    // Arrange
    var coordinator = new StubWorkCoordinator(0, 0);
    var clock = new StubDebuggerAwareClock();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(new PerspectiveSyncAwaiter(coordinator, clock, null!)));
  }

  // ==========================================================================
  // Stub Implementations
  // ==========================================================================

  /// <summary>
  /// Stub work coordinator that returns configured sync results.
  /// </summary>
  private sealed class StubWorkCoordinator : IWorkCoordinator {
    private readonly int _pendingCount;
    private readonly int _processedCount;

    public StubWorkCoordinator(int pendingCount, int processedCount) {
      _pendingCount = pendingCount;
      _processedCount = processedCount;
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = Guid.NewGuid(),
            StreamId = Guid.NewGuid(),
            PendingCount = _pendingCount,
            ProcessedCount = _processedCount
          }
        ]
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCheckpointCompletion completion, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCheckpointFailure failure, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
        => Task.FromResult<PerspectiveCheckpointInfo?>(null);
  }

  /// <summary>
  /// Stub debugger-aware clock for tests.
  /// </summary>
  private sealed class StubDebuggerAwareClock : IDebuggerAwareClock {
    private readonly bool _timedOut;

    public StubDebuggerAwareClock(bool timedOut = false) => _timedOut = timedOut;

    public DebuggerDetectionMode Mode => DebuggerDetectionMode.Disabled;
    public bool IsPaused => false;

    public IActiveStopwatch StartNew() => new StubActiveStopwatch(_timedOut);

    public IDisposable OnPauseStateChanged(Action<bool> handler) => new NoOpDisposable();

    public long GetCurrentTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    public void Dispose() { }

    private sealed class NoOpDisposable : IDisposable {
      public void Dispose() { }
    }
  }

  /// <summary>
  /// Stub active stopwatch for tests.
  /// </summary>
  private sealed class StubActiveStopwatch : IActiveStopwatch {
    private readonly bool _timedOut;

    public StubActiveStopwatch(bool timedOut) => _timedOut = timedOut;

    public TimeSpan ActiveElapsed => _timedOut ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(50);
    public TimeSpan WallElapsed => ActiveElapsed;
    public TimeSpan FrozenTime => TimeSpan.Zero;

    public bool HasTimedOut(TimeSpan timeout) => _timedOut;
    public void Halt() { }
  }

  /// <summary>
  /// Stub logger that discards log entries.
  /// </summary>
  private sealed class StubLogger<T> : ILogger<T> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
  }
}
