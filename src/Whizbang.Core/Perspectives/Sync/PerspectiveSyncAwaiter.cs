using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Implementation of <see cref="IPerspectiveSyncAwaiter"/> for local synchronization.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses the scoped event tracker for local (in-memory) event tracking
/// and the sync signaler for notifications when events are processed.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/PerspectiveSyncAwaiterTests.cs</tests>
public sealed class PerspectiveSyncAwaiter : IPerspectiveSyncAwaiter {
  private readonly IScopedEventTracker _tracker;
  private readonly IPerspectiveSyncSignaler _signaler;
  private readonly IDebuggerAwareClock _clock;

  /// <summary>
  /// Initializes a new instance of <see cref="PerspectiveSyncAwaiter"/>.
  /// </summary>
  /// <param name="tracker">The scoped event tracker.</param>
  /// <param name="signaler">The sync signaler.</param>
  /// <param name="clock">The debugger-aware clock.</param>
  public PerspectiveSyncAwaiter(
      IScopedEventTracker tracker,
      IPerspectiveSyncSignaler signaler,
      IDebuggerAwareClock clock) {
    _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    _signaler = signaler ?? throw new ArgumentNullException(nameof(signaler));
    _clock = clock ?? throw new ArgumentNullException(nameof(clock));
  }

  /// <inheritdoc />
  public Task<bool> IsCaughtUpAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);

    var pendingEvents = _tracker.GetEmittedEvents(options.Filter);

    // If no events match the filter, we're caught up
    return Task.FromResult(pendingEvents.Count == 0);
  }

  /// <inheritdoc />
  public async Task<SyncResult> WaitAsync(
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);

    var stopwatch = _clock.StartNew();
    var pendingEvents = _tracker.GetEmittedEvents(options.Filter);

    // If no events match the filter, return immediately
    if (pendingEvents.Count == 0) {
      return new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.ActiveElapsed);
    }

    var eventsToWait = pendingEvents.Count;
    var processedEventIds = new HashSet<Guid>();
    var completionSource = new TaskCompletionSource<bool>();

    // Subscribe to checkpoint signals
    using var subscription = _signaler.Subscribe(perspectiveType, signal => {
      // Check if this signal completes any of our pending events
      lock (processedEventIds) {
        processedEventIds.Add(signal.LastEventId);

        // Check if all events are now processed
        if (_tracker.AreAllProcessed(options.Filter, processedEventIds)) {
          completionSource.TrySetResult(true);
        }
      }
    });

    // Wait for completion or timeout
    var timeoutTask = _createTimeoutTask(options, stopwatch, ct);

    var completedTask = await Task.WhenAny(completionSource.Task, timeoutTask);

    stopwatch.Halt();

    if (completedTask == completionSource.Task && completionSource.Task.Result) {
      return new SyncResult(SyncOutcome.Synced, eventsToWait, stopwatch.ActiveElapsed);
    }

    // Check if cancelled
    ct.ThrowIfCancellationRequested();

    return new SyncResult(SyncOutcome.TimedOut, eventsToWait, stopwatch.ActiveElapsed);
  }

  private static async Task<bool> _createTimeoutTask(
      PerspectiveSyncOptions options,
      IActiveStopwatch stopwatch,
      CancellationToken ct) {
    var timeout = options.Timeout;

    if (options.DebuggerAwareTimeout) {
      // Poll with active time checking
      while (!ct.IsCancellationRequested) {
        if (stopwatch.HasTimedOut(timeout)) {
          return false;
        }

        await Task.Delay(50, ct);
      }
    } else {
      // Simple wall-clock timeout
      try {
        await Task.Delay(timeout, ct);
      } catch (OperationCanceledException) {
        throw;
      }
    }

    return false;
  }
}
