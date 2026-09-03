using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The background sweep that keeps the recently-processed cache from growing without bound, and the
/// two ways it is asked to do nothing.
/// <para>
/// The cache exists to suppress duplicate work, so entries accumulate for as long as the process
/// runs; the sweep is what bounds it. Both of its quiet paths had never run — the disabled case,
/// where the worker must park rather than exit so the host does not treat it as a crashed service,
/// and shutdown, which must end the loop silently because a deploy is not a sweep failure.
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

  [Test]
  public async Task WhenDisabled_TheWorkerParksInsteadOfExitingAsync() {
    // Returning immediately would let the host see a BackgroundService complete on its own, which
    // reads as a crashed worker rather than a deliberately-off one.
    var worker = _worker(new RecentlyProcessedEventCache(new SystemTimeProvider()), enabled: false);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ShutdownEndsTheSweepLoopWithoutReportingFailureAsync() {
    // The loop parks on a PeriodicTimer; stopping cancels that wait. A deploy is not a sweep
    // failure, so the cancellation must be absorbed rather than logged as one.
    var worker = _worker(new RecentlyProcessedEventCache(new SystemTimeProvider()), enabled: true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);
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
