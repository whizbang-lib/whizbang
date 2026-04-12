using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="IPerspectiveSyncAwaiter.WaitForStreamAsync"/> method.
/// With an empty SyncEventTracker, WaitForStreamAsync returns NoPendingEvents immediately.
/// </summary>
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

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task WaitForStreamAsync_ReturnsSync_WhenAllEventsProcessedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 0, processedCount: 5);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    await Assert.That(result.EventsAwaited).IsEqualTo(0);
  }

  [Test]
  public async Task WaitForStreamAsync_ReturnsNoPendingEvents_WhenEventsPendingButNotTrackedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 3, processedCount: 2);
    var clock = new StubDebuggerAwareClock(timedOut: true);
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromMilliseconds(1));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  // ==========================================================================
  // WaitForStreamAsync - Cancellation Tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_ReturnsNoPendingEvents_WithCancelledTokenAndEmptyTrackerAsync() {
    // Arrange - with an empty tracker, NoPendingEvents returns immediately
    // before the cancellation token is ever checked.
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 99, processedCount: 0);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger, new SyncEventTracker());
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5),
        ct: cts.Token);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
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

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
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

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.EventsAwaited).IsEqualTo(0);
  }

  [Test]
  public async Task WaitForStreamAsync_ReturnsNoPendingEvents_WithElapsedTimeAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var coordinator = new StubWorkCoordinator(pendingCount: 0, processedCount: 1);
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, logger, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  // ==========================================================================
  // Constructor Tests
  // ==========================================================================

  [Test]
  public async Task Constructor_ThrowsOnNullCoordinatorAsync() {
    // Arrange
    var clock = new StubDebuggerAwareClock();
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(new PerspectiveSyncAwaiter(null!, clock, logger, new SyncEventTracker())));
  }

  [Test]
  public async Task Constructor_ThrowsOnNullClockAsync() {
    // Arrange
    var coordinator = new StubWorkCoordinator(0, 0);
    var logger = new StubLogger<PerspectiveSyncAwaiter>();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(new PerspectiveSyncAwaiter(coordinator, null!, logger, new SyncEventTracker())));
  }

  [Test]
  public async Task Constructor_ThrowsOnNullLoggerAsync() {
    // Arrange
    var coordinator = new StubWorkCoordinator(0, 0);
    var clock = new StubDebuggerAwareClock();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() =>
        Task.FromResult(new PerspectiveSyncAwaiter(coordinator, clock, null!, new SyncEventTracker())));
  }

  // ==========================================================================
  // Stub Implementations
  // ==========================================================================

  /// <summary>
  /// Stub work coordinator that returns configured sync results.
  /// </summary>
  private sealed class StubWorkCoordinator(int pendingCount, int processedCount) : IWorkCoordinator {
    private readonly int _pendingCount = pendingCount;
    private readonly int _processedCount = processedCount;

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

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
        => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Stub debugger-aware clock for tests.
  /// </summary>
  private sealed class StubDebuggerAwareClock(bool timedOut = false) : IDebuggerAwareClock {
    private readonly bool _timedOut = timedOut;

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
  private sealed class StubActiveStopwatch(bool timedOut) : IActiveStopwatch {
    private readonly bool _timedOut = timedOut;

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
