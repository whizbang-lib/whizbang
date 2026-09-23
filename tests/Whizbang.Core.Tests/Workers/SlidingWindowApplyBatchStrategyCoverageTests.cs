using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
  private sealed class RecordingLogger : ILogger<SlidingWindowApplyBatchStrategy> {
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
    var logger = new RecordingLogger();

    var sut = new SlidingWindowApplyBatchStrategy(
      flush: async (_, _, ct) => {
        flushStarted.TrySetResult();
        // Hangs until the strategy's own internal cancellation source is force-canceled by
        // FlushAndStopAsync's hard-shutdown branch — never completes on its own.
        await Task.Delay(Timeout.Infinite, ct);
      },
      logger: logger,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
      });

    await sut.AppendAsync(Guid.CreateVersion7(), testToken);
    await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    using var callerCts = new CancellationTokenSource();
    await callerCts.CancelAsync();

    // The caller's token is already canceled and the flush is still hung, so awaiting the drain
    // workers with that token must throw immediately — caught internally, forcing the strategy's
    // own hard-cancel — rather than this call ever throwing out to us.
    await sut.FlushAndStopAsync(callerCts.Token).WaitAsync(TimeSpan.FromSeconds(10), testToken);

    // Give the drain task's own exception handler, now unblocked by the forced cancellation, a moment to run
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
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
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

  /// <summary>
  /// The sweep timer is periodic and its callback is fire-and-forget, so a tick can land after
  /// <see cref="SlidingWindowApplyBatchStrategy.FlushAndStopAsync"/> has already completed every
  /// writer and disposed the strategy's internal cancellation source. If the sweep went ahead
  /// anyway it would re-complete completed writers and await already-finished workers on a torn-
  /// down object — during host shutdown, which is exactly when nobody is watching. The disposed
  /// guard is what makes that tick a no-op.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task IdleSweep_AfterShutdown_LeavesTheBuffersAloneAsync(CancellationToken testToken) {
    var clock = new FakeTimeProvider();
    var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => Task.CompletedTask,
      logger: NullLogger<SlidingWindowApplyBatchStrategy>.Instance,
      options: new SlidingWindowApplyOptions {
        SlidingWindow = TimeSpan.FromMilliseconds(10),
        MaxWait = TimeSpan.FromMilliseconds(50),
        MaxSize = 100,
        // Long enough that nothing fires on its own — the sweep under test is driven explicitly.
        IdleSweepInterval = TimeSpan.FromMinutes(5),
        IdleEvictionWindow = TimeSpan.FromSeconds(30),
      },
      timeProvider: clock);

    await sut.AppendAsync(Guid.CreateVersion7(), testToken);
    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("the buffer has to be really mapped, or the assertion below would hold vacuously");

    await sut.FlushAndStopAsync(CancellationToken.None);
    // Every mapped buffer is now far past its eviction window: a running sweep WOULD evict it.
    clock.Advance(TimeSpan.FromMinutes(10));

    await sut.RunIdleSweepNowForTestAsync();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(1)
      .Because("a sweep that lands after shutdown must return without touching the buffers — "
             + "the clock was advanced past the eviction window, so without the disposed guard "
             + "this stream would have been evicted and its finished worker awaited again");
  }

  // ============================================================
  // The drain loop's outer catch, and the sweep's tolerance of a dead worker
  // ============================================================

  // Both tests below work by making the LOGGING SINK throw. The error path a drain worker takes
  // when a flush fails is not itself infallible: a sink bound to the host's shutdown token throws
  // OperationCanceledException as the host stops, and a sink whose provider has already been torn
  // down throws something else. Those are the only exceptions that escape the flush-failure
  // handler, and so the only way anything reaches the loop's outer catch at all.

  // A cancellation that escapes the flush-failure handler must end the drain quietly. If it
  // escaped the loop as well, the worker task would fault, and FlushAndStopAsync folds a faulted
  // worker into the same catch it uses for a caller-canceled shutdown — so the fault would be
  // swallowed there too and surface only as an unobserved exception, long after the shutdown that
  // caused it and with nothing tying it back.
  [Test]
  [Timeout(30000)]
  public async Task DrainWorker_CancellationEscapingTheFlushFailureHandler_EndsTheLoopQuietlyAsync(
      CancellationToken cancellationToken) {
    var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => Task.FromException(new InvalidOperationException("apply unavailable")),
      logger: new ThrowingSink(() => new OperationCanceledException("log sink shutting down")),
      options: _oneSignalPerBatch(),
      timeProvider: new FakeTimeProvider());

    await sut.AppendAsync(Guid.CreateVersion7(), cancellationToken);

    await Assert.That(async () => await sut.WhenWorkersStoppedForTests()).ThrowsNothing()
      .Because("the drain loop absorbs a cancellation rather than faulting; a faulted worker here "
             + "is an unobserved exception that nothing in this type ever reports");
  }

  // The idle sweep is the only thing that bounds this type's memory. It awaits each evicted
  // stream's worker to let in-flight work finish, and a worker that died is exactly what it will
  // meet after a storm of flush failures — if that killed the sweep, every stream after the dead
  // one would stay mapped forever and the leak the sweep exists to prevent comes back.
  [Test]
  [Timeout(30000)]
  public async Task IdleSweep_WorkersDied_StillEvictsEveryIdleStreamAsync(
      CancellationToken cancellationToken) {
    var clock = new FakeTimeProvider();
    var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => Task.FromException(new InvalidOperationException("apply unavailable")),
      logger: new ThrowingSink(() => new InvalidOperationException("log provider disposed")),
      options: _oneSignalPerBatch(),
      timeProvider: clock);

    await sut.AppendAsync(Guid.CreateVersion7(), cancellationToken);
    await sut.AppendAsync(Guid.CreateVersion7(), cancellationToken);

    await Assert.That(async () => await sut.WhenWorkersStoppedForTests())
      .Throws<InvalidOperationException>()
      .Because("this arranges the precondition the sweep has to survive — a worker whose own error "
             + "path threw, so its task is faulted rather than finished");

    clock.Advance(TimeSpan.FromMinutes(10));
    await sut.RunIdleSweepNowForTestAsync();

    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0)
      .Because("the sweep has to keep going past a dead worker; stopping at the first one would "
             + "leave every later stream mapped for the life of the process");
  }

  private static SlidingWindowApplyOptions _oneSignalPerBatch() => new() {
    // One signal per batch: the batcher yields on the size bound without ever consulting the
    // clock, so the flush below runs with no wall-clock wait and no fake-timer choreography.
    MaxSize = 1,
    SlidingWindow = TimeSpan.FromMilliseconds(50),
    MaxWait = TimeSpan.FromSeconds(1),
    IdleSweepInterval = TimeSpan.FromMinutes(5),
    IdleEvictionWindow = TimeSpan.FromSeconds(30),
  };

  /// <summary>A logging sink that throws whatever it is told to throw.</summary>
  private sealed class ThrowingSink(Func<Exception> failure) : ILogger<SlidingWindowApplyBatchStrategy> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
      => throw failure();
  }
}
