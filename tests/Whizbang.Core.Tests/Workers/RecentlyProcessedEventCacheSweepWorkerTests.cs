using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The background sweep that keeps the recently-processed cache from growing without bound.
/// <para>
/// The cache exists to suppress duplicate work, so entries accumulate for as long as the process
/// runs; this loop is the only thing that bounds them. Its quiet paths matter more than the happy
/// one — the disabled case must park rather than exit so the host does not read it as a crashed
/// service, shutdown must end the loop silently because a deploy is not a sweep failure, and a
/// sweep that throws must not take the loop down with it.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/RecentlyProcessedEventCacheSweepWorker.cs</code-under-test>
public class RecentlyProcessedEventCacheSweepWorkerTests {

  private static RecentlyProcessedEventCacheSweepWorker _worker(
      RecentlyProcessedEventCache cache, bool enabled, int intervalSeconds = 1) =>
    new(cache, Options.Create(new RecentlyProcessedEventCacheOptions {
      Enabled = enabled,
      SweepIntervalSeconds = intervalSeconds,
    }));

  /// <summary>
  /// A clock the test can move forward and break on demand. A sweep reads it exactly once per pass,
  /// so counting reads gives the test a real completion signal rather than a sleep.
  /// </summary>
  private sealed class SteppableClock : ITimeProvider {
    private readonly TaskCompletionSource _secondSweepStarted =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now = new(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);
    private int _sweepReads;
    private volatile bool _counting;
    private volatile bool _failNextRead;

    /// <summary>Completes once a sweep has read the clock a second time.</summary>
    public Task SecondSweepStarted => _secondSweepStarted.Task;

    /// <summary>Clock reads taken since <see cref="StartCountingSweeps"/>.</summary>
    public int SweepReads => Volatile.Read(ref _sweepReads);

    public void AdvanceBy(TimeSpan delta) => _now = _now.Add(delta);

    /// <summary>Begins counting reads, so setup reads are not mistaken for sweeps.</summary>
    public void StartCountingSweeps() {
      Interlocked.Exchange(ref _sweepReads, 0);
      _counting = true;
    }

    public void FailNextRead() => _failNextRead = true;

    public DateTimeOffset GetUtcNow() {
      if (_counting && Interlocked.Increment(ref _sweepReads) >= 2) {
        _secondSweepStarted.TrySetResult();
      }
      if (_failNextRead) {
        _failNextRead = false;
        throw new InvalidOperationException("clock unavailable");
      }
      return _now;
    }

    public DateTimeOffset GetLocalNow() => GetUtcNow();
    public long GetTimestamp() => _now.UtcTicks;
    public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.Zero;
    public long TimestampFrequency => TimeSpan.TicksPerSecond;
  }

  /// <summary>
  /// Records the worker's log events and, more importantly, lets a test wait for one.
  /// </summary>
  /// <remarks>
  /// <see cref="BackgroundService.StartAsync"/> returning does not mean <c>ExecuteAsync</c> has
  /// run: the host starts it on the thread pool, so under a saturated pool -- which a full test
  /// project reliably produces -- the body may not have executed yet. A test that stops the worker
  /// at that point cancels one that never started, and every "did it shut down cleanly?" assertion
  /// it then makes is answered by the wrong thing. The worker's own first log line is the signal
  /// that the loop is actually established.
  /// </remarks>
  private sealed class RecordingLogger : ILogger<RecentlyProcessedEventCacheSweepWorker> {
    private const int STARTED_EVENT_ID = 1;
    private const int DISABLED_EVENT_ID = 3;
    private readonly List<string> _messages = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disabled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the worker has logged that its sweep loop started.</summary>
    public Task Started => _started.Task;

    /// <summary>Completes once the worker has logged that it is disabled and parking.</summary>
    public Task Disabled => _disabled.Task;

    public string Recorded { get { lock (_messages) { return string.Join("|", _messages); } } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add($"e{eventId.Id}"); }
      if (eventId.Id == STARTED_EVENT_ID) {
        _started.TrySetResult();
      } else if (eventId.Id == DISABLED_EVENT_ID) {
        _disabled.TrySetResult();
      }
    }
  }

  [Test]
  [Timeout(30_000)]
  public async Task ASweepThatThrows_DoesNotStopLaterSweepsFromReclaimingMemoryAsync(
      CancellationToken cancellationToken) {
    // The cache only stops growing because this loop keeps sweeping. If one failed sweep ended the
    // loop, the process would hold every processed work id until restart -- the unbounded growth
    // the worker exists to prevent -- and nothing would report it beyond a single warning.
    var clock = new SteppableClock();
    var cache = new RecentlyProcessedEventCache(clock, ttl: TimeSpan.FromMinutes(5));
    cache.MarkProcessed((Guid)TrackedGuid.NewMedo());
    await Assert.That(cache.Count).IsEqualTo(1);

    clock.AdvanceBy(TimeSpan.FromMinutes(10));  // the entry is now well past its TTL
    clock.StartCountingSweeps();
    clock.FailNextRead();                       // ... and the first sweep to notice will throw

    var worker = _worker(cache, enabled: true);
    await worker.StartAsync(CancellationToken.None);
    await clock.SecondSweepStarted.WaitAsync(cancellationToken);
    // StopAsync awaits the execute task, so the second sweep has run to completion by the time it
    // returns -- no polling for the eviction to become visible.
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(cache.Count).IsEqualTo(0)
      .Because("a sweep that throws is logged and skipped, so the following tick still evicts the "
             + "expired entry; were the loop to end instead, the cache would grow until restart");
    await Assert.That(worker.ExecuteTask!.IsCompletedSuccessfully).IsTrue()
      .Because("the sweep failure belongs inside the loop, not surfaced as a crashed worker");
  }

  [Test]
  [Timeout(30_000)]
  public async Task WhenDisabled_TheWorkerParksAndNeverTouchesTheCacheAsync(
      CancellationToken cancellationToken) {
    // Returning immediately would let the host see a BackgroundService complete on its own, which
    // reads as a crashed worker rather than a deliberately-off one.
    var clock = new SteppableClock();
    var log = new RecordingLogger();
    var worker = new RecentlyProcessedEventCacheSweepWorker(
      new RecentlyProcessedEventCache(clock),
      Options.Create(new RecentlyProcessedEventCacheOptions { Enabled = false, SweepIntervalSeconds = 1 }),
      log);
    clock.StartCountingSweeps();

    await worker.StartAsync(CancellationToken.None);
    // Without this wait the assertions below are answered by a worker that has not run at all --
    // an execute task that has not started is also "not completed", so the test would pass while
    // proving nothing.
    await log.Disabled.WaitAsync(cancellationToken);

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a disabled worker parks on its stopping token; completing early is precisely how "
             + "the host detects a BackgroundService that has crashed");
    await Assert.That(clock.SweepReads).IsEqualTo(0)
      .Because("disabled means the cache is never swept at all, not merely swept less often");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30_000)]
  public async Task ShutdownEndsTheSweepLoopWithoutReportingFailureAsync(
      CancellationToken cancellationToken) {
    // The loop parks on a PeriodicTimer; stopping cancels that wait. A deploy is not a sweep
    // failure, so the cancellation must be absorbed rather than left as a faulted worker.
    var log = new RecordingLogger();
    var worker = new RecentlyProcessedEventCacheSweepWorker(
      new RecentlyProcessedEventCache(new SystemTimeProvider()),
      Options.Create(new RecentlyProcessedEventCacheOptions { Enabled = true, SweepIntervalSeconds = 1 }),
      log);

    await worker.StartAsync(CancellationToken.None);
    await log.Started.WaitAsync(cancellationToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("StopAsync never observes a faulted execute task, so an escaping "
             + "OperationCanceledException would show up only as an unobserved exception");
  }

  [Test]
  public async Task SweepingAnEmptyCache_IsSafeAsync() {
    // The worker calls this on a timer from the moment it starts, so the very first sweep usually
    // runs against a cache nothing has been recorded into yet. A throw there would be caught and
    // logged as a sweep failure on every service start.
    var cache = new RecentlyProcessedEventCache(new SystemTimeProvider());

    await Assert.That(() => cache.SweepExpired()).ThrowsNothing()
      .Because("the first sweep after startup runs against an empty cache, and a throw there "
             + "reports a failure on every service start");
  }
}
