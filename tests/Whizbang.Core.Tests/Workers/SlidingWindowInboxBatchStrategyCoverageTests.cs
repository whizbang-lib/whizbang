using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="SlidingWindowInboxBatchStrategy"/> paths the primary suite
/// (<see cref="SlidingWindowInboxBatchStrategyTests"/>) doesn't reach: the hard-shutdown branch
/// of <see cref="SlidingWindowInboxBatchStrategy.FlushAndStopAsync"/> — a caller-supplied
/// cancellation token firing while a per-stream buffer's flush is still hung — and the drain
/// loop's own cooperative return once that forced cancellation lands.
/// </summary>
/// <docs>extending/internals/event-ordering-invariant</docs>
public class SlidingWindowInboxBatchStrategyCoverageTests {
  private readonly Uuid7IdProvider _idProvider = new();

  private InboxMessage _makeMessage(Guid? streamId = null) {
    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<JsonElement>(
      MessageId.From(messageId),
      JsonDocument.Parse("{}").RootElement,
      []);
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "test",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
    };
  }

  /// <summary>Captures error-level messages — used to prove a shutdown-forced cancellation of an
  /// in-flight flush is never mistaken for a flush failure.</summary>
  private sealed class _RecordingLogger : ILogger<SlidingWindowInboxBatchStrategy> {
    private readonly Lock _lock = new();
    private readonly List<string> _errors = [];

    public List<string> Errors {
      get { lock (_lock) { return [.. _errors]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Error) {
        lock (_lock) { _errors.Add(formatter(state, exception)); }
      }
    }
  }

  /// <summary>
  /// Covers the hard-shutdown pairing: when the caller's <c>FlushAndStopAsync</c> token fires
  /// while a per-stream buffer's flush is still hung, the strategy must force-cancel its
  /// internal token to unstick the drain task rather than waiting for it forever, AND the drain
  /// task's own OperationCanceledException-during-shutdown catch must return quietly instead of
  /// falling into the generic exception handler.
  /// </summary>
  /// <remarks>
  /// If the force-cancel regressed, a hard-shutdown deadline would leave a hung drain task
  /// running forever with nothing left to unstick it — <c>FlushAndStopAsync</c> would still
  /// return (its own wait already threw), but the leaked task's eventual outcome is lost and the
  /// process cannot exit cleanly while it survives. If the drain loop's own cooperative return
  /// regressed instead, that same forced cancellation would be logged as a flush FAILURE —
  /// turning an intentional, already-handled shutdown into false-positive error-log noise that
  /// pages an operator for nothing.
  /// </remarks>
  [Test]
  [Timeout(30000)]
  public async Task FlushAndStopAsync_CallerTokenFiresWhileFlushIsHung_ForceCancelsWithoutLoggingFailureAsync(
      CancellationToken testToken) {
    var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new _RecordingLogger();

    var sut = new SlidingWindowInboxBatchStrategy(
      flush: async (msgs, ct) => {
        flushStarted.TrySetResult();
        // Hangs until the strategy's own internal cancellation source is force-canceled by
        // FlushAndStopAsync's hard-shutdown branch — never completes on its own.
        await Task.Delay(Timeout.Infinite, ct);
      },
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
      },
      logger: logger);

    await sut.AppendAsync(_makeMessage(), testToken);
    await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    using var callerCts = new CancellationTokenSource();
    await callerCts.CancelAsync();

    // The caller's token is already canceled and the flush is still hung, so awaiting the
    // drain workers with that token must throw immediately — caught internally, forcing the
    // strategy's own hard-cancel — rather than this call ever throwing out to us.
    await sut.FlushAndStopAsync(callerCts.Token).WaitAsync(TimeSpan.FromSeconds(10), testToken);

    // Give the drain task's own catch (now unblocked by the forced cancellation) a moment to
    // run — it either returns quietly or, if regressed, logs a spurious failure.
    await Task.Delay(200, testToken);

    await Assert.That(logger.Errors).IsEmpty()
      .Because("a shutdown-forced cancellation of an in-flight flush is not a flush failure and must never be logged as one");
  }

  // Target: SlidingWindowInboxBatchStrategy.cs:170 — the `_disposed` guard at the top of
  // _runIdleSweepAsync.
  //
  // Shutdown sets the disposed flag FIRST and only then disposes the sweep timer, so a tick
  // already on its way runs with the flag set. That tick must do nothing. If it proceeded, it
  // would walk _streams and, for every idle buffer, TryRemove it, complete its writer and await
  // its worker — concurrently with FlushAndStopAsync doing exactly the same thing to the same
  // buffers. Two threads racing the same removal is how a shutdown drops inbox messages that were
  // still queued in a per-stream buffer: the sweep removes the entry before the stop path
  // snapshots the workers, so the stop path never waits for that stream's drain at all.
  //
  // The interleaving is forced rather than raced. The injected time provider hands the strategy a
  // timer whose DisposeAsync parks, so FlushAndStopAsync is held at exactly the point after the
  // flag is set and before the timer is gone, and the test fires the tick by hand from there.
  //
  // IdleEvictionWindow is zero on purpose: every buffer is then past its cutoff, so an unguarded
  // sweep WOULD evict. The stream surviving the tick is therefore attributable to the guard and
  // nothing else.
  [Test]
  [Timeout(30000)]
  public async Task IdleSweep_TickArrivingDuringShutdown_DoesNotTouchTheBuffersTheStopPathIsDrainingAsync(
      CancellationToken testToken) {
    var timeProvider = new _parkingSweepTimerProvider();
    var sut = new SlidingWindowInboxBatchStrategy(
      flush: (_, _) => Task.CompletedTask,
      options: new SlidingWindowInboxOptions {
        SlidingWindow = TimeSpan.FromSeconds(30),
        MaxWait = TimeSpan.FromSeconds(60),
        MaxSize = 100,
        IdleEvictionWindow = TimeSpan.Zero,
        IdleSweepInterval = TimeSpan.FromSeconds(10),
      },
      timeProvider: timeProvider);

    await sut.AppendAsync(_makeMessage(), testToken);
    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("the sweep needs something to evict for its absence to mean anything");

    var stop = Task.Run(() => sut.FlushAndStopAsync(CancellationToken.None), testToken);
    // Shutdown is now parked inside the sweep timer's DisposeAsync: the disposed flag is set and
    // the timer has not yet been torn down — the exact window a real tick lands in.
    await timeProvider.SweepTimer.DisposeStarted.WaitAsync(testToken);

    timeProvider.SweepTimer.Fire();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("a sweep tick that lands after shutdown has begun must return immediately; evicting "
             + "here would remove the buffer out from under the stop path, which then never waits "
             + "for that stream's drain and loses whatever was still queued in it");

    timeProvider.SweepTimer.ReleaseDispose();
    await stop.WaitAsync(TimeSpan.FromSeconds(20), testToken);
    await Assert.That(stop.IsCompletedSuccessfully).IsTrue()
      .Because("the stop path owns the drain and must complete it once the timer is gone");
  }

  /// <summary>
  /// Hands out one controllable timer — the strategy's idle-sweep timer, which is the first one it
  /// creates — and delegates every later timer to the system provider so the per-stream batchers
  /// keep their real behavior.
  /// </summary>
  private sealed class _parkingSweepTimerProvider : TimeProvider {
    private int _created;
    public _parkingTimer SweepTimer { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      if (Interlocked.Exchange(ref _created, 1) == 0) {
        SweepTimer.Arm(callback, state);
        return SweepTimer;
      }
      return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }
  }

  /// <summary>
  /// A timer that never fires on its own. <see cref="Fire"/> invokes the callback synchronously,
  /// and <see cref="ITimer.DisposeAsync"/> parks until <see cref="ReleaseDispose"/> is called,
  /// which is what holds a shutdown open at a chosen instruction.
  /// </summary>
  private sealed class _parkingTimer : ITimer {
    private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimerCallback? _callback;
    private object? _state;

    public Task DisposeStarted => _disposeStarted.Task;
    public void ReleaseDispose() => _releaseDispose.TrySetResult();
    public void Arm(TimerCallback callback, object? state) {
      _callback = callback;
      _state = state;
    }
    public void Fire() => _callback?.Invoke(_state);

    public bool Change(TimeSpan dueTime, TimeSpan period) => true;
    public void Dispose() => _releaseDispose.TrySetResult();

    public async ValueTask DisposeAsync() {
      _disposeStarted.TrySetResult();
      await _releaseDispose.Task.ConfigureAwait(false);
    }
  }
}
