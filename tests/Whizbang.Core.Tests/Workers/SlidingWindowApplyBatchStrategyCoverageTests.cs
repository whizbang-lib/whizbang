using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="SlidingWindowApplyBatchStrategy"/> paths the primary suite
/// (<see cref="SlidingWindowApplyBatchStrategyTests"/> and
/// <see cref="SlidingWindowApplyFailurePathTests"/>) doesn't reach: the hard-shutdown pairing in
/// <see cref="SlidingWindowApplyBatchStrategy.FlushAndStopAsync"/> — a caller-supplied
/// cancellation token firing while a per-stream buffer's flush is still hung — and the idle sweep
/// skipping a buffer that is still within its eviction window while evicting a stale one.
/// </summary>
public class SlidingWindowApplyBatchStrategyCoverageTests {

  /// <summary>Captures error-level messages — used to prove a shutdown-forced cancellation of an
  /// in-flight flush is never mistaken for a flush failure.</summary>
  private sealed class _recordingLogger : ILogger<SlidingWindowApplyBatchStrategy> {
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
  /// If the strategy's own catch stopped forwarding this flush's cancellation into a quiet
  /// return, a shutdown-forced cancellation of an in-flight flush would be logged as a flush
  /// FAILURE — turning an intentional, already-handled shutdown into false-positive error-log
  /// noise that pages an operator for nothing.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task FlushAndStopAsync_CallerTokenFiresWhileFlushIsHung_ForceCancelsWithoutLoggingFailureAsync(
      CancellationToken testToken) {
    var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new _recordingLogger();

    var sut = new SlidingWindowApplyBatchStrategy(
      flush: async (_, _, ct) => {
        flushStarted.TrySetResult();
        // Hangs until the strategy's own internal cancellation source is force-canceled by
        // FlushAndStopAsync's hard-shutdown branch — never completes on its own.
        await Task.Delay(Timeout.Infinite, ct);
      },
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
      },
      logger: logger);

    await sut.AppendAsync(Guid.CreateVersion7(), testToken);
    await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    using var callerCts = new CancellationTokenSource();
    await callerCts.CancelAsync();

    // The caller's token is already canceled and the flush is still hung, so awaiting the drain
    // workers with that token must throw immediately — caught internally, forcing the strategy's
    // own hard-cancel — rather than this call ever throwing out to us.
    await sut.FlushAndStopAsync(callerCts.Token).WaitAsync(TimeSpan.FromSeconds(10), testToken);

    // Give the drain task's own catch (now unblocked by the forced cancellation) a moment to run
    // — it either returns quietly or, if regressed, logs a spurious failure.
    await Task.Delay(200, testToken);

    await Assert.That(logger.Errors).IsEmpty()
      .Because("a shutdown-forced cancellation of an in-flight flush is not a flush failure and "
             + "must never be logged as one");
  }

  /// <summary>
  /// If the idle sweep stopped skipping still-active buffers (or started evicting everything
  /// regardless of age), a stream mid-burst could have its buffer torn out from under it while a
  /// genuinely idle stream never gets reclaimed — inverting the memory bound the sweep exists to
  /// provide.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task IdleSweep_SkipsAStreamStillWithinItsWindowWhileEvictingAStaleOneAsync(CancellationToken testToken) {
    var staleStream = Guid.CreateVersion7();

    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => Task.CompletedTask,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
        IdleSweepInterval = TimeSpan.FromMilliseconds(50),
        IdleEvictionWindow = TimeSpan.FromMilliseconds(300),
      });

    await sut.AppendAsync(staleStream, testToken);
    // Let the stale stream age well past the eviction window before the active one even exists.
    await Task.Delay(350, testToken);
    var activeStream = Guid.CreateVersion7();
    await sut.AppendAsync(activeStream, testToken);

    // Poll for the sweep to evict exactly the stale stream — the active one (appended moments
    // ago) is still comfortably inside its own 300ms window on every sweep tick that follows.
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
    while (sut.ActiveStreamCount > 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20, testToken);
    }

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("the sweep must evict the stale stream while leaving the still-active one mapped — "
             + "evicting everything would tear a live buffer out from under a mid-burst stream, and "
             + "evicting nothing would defeat the memory bound the sweep exists to provide");
  }
}
