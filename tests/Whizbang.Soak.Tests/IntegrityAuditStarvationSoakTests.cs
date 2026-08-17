using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Soak.Tests;

/// <summary>
/// The soak scenario for the failure that motivated this project.
/// <para>
/// A live fleet entered a restart loop: the integrity comparator issued one durable outbox write
/// per divergent stream, hundreds of them sequentially from a single handler. The .NET thread pool
/// grows by roughly one thread per second once its minimum is exhausted, so a burst of blocking
/// work parks every available thread and everything behind it queues. What queued was
/// <c>/alive</c> — a liveness check that does no I/O and is Healthy in every lifecycle phase.
/// Kubernetes saw three consecutive timeouts, killed the pod, and it starved again on restart.
/// </para>
/// <para>
/// <see cref="WorkerThreadPoolFloor"/> exists to blunt exactly this: it raises the pool's minimum
/// so a burst is absorbed by threads that already exist rather than by the one-per-second grow
/// rate. That is a real, emergent, wall-clock property — which is why it is asserted here and not
/// in the gate, where a busy runner would make it flap.
/// </para>
/// <para>
/// The deterministic half of the same lesson lives in the normal suites: the fan-out COUNT is
/// capped and mutation-verified, and <c>scripts/Lint-UnboundedFanOut.ps1</c> fails on a new awaited
/// fan-out inside a loop. Prefer those. This measures what a count cannot.
/// </para>
/// </summary>
[Property("Category", "Soak")]
public class IntegrityAuditStarvationSoakTests {

  /// <summary>
  /// Stands in for the liveness endpoint: pure CPU, no I/O, must never be made to wait. Real
  /// <c>/alive</c> does no I/O either, so if this cannot get a thread promptly, neither could it.
  /// </summary>
  private static void _livenessWork() => Thread.SpinWait(50);

  [Test]
  [Property("Soak", "AuditStarvation")]
  public async Task ThreadPoolFloor_AbsorbsAFanOutBurst_LivenessKeepsGettingAThreadAsync() {
    // The floor is process-global and this test measures it, so establish it explicitly rather
    // than depending on whatever else has run first.
    WorkerThreadPoolFloor.Apply();
    ThreadPool.GetMinThreads(out var minWorkers, out _);

    await Assert.That(minWorkers).IsGreaterThanOrEqualTo(WorkerThreadPoolFloor.DefaultFloor)
      .Because("the floor is the mechanism under test; without it the rest measures nothing.");

    // Burst sized to the floor ITSELF. That is the property the floor actually claims: a burst up
    // to its size is absorbed by threads that already exist, instead of waiting on the pool's
    // ~1-thread-per-second injection rate. Deliberately NOT larger -- oversubscribing the floor
    // starves by arithmetic, which would measure the setup rather than the mechanism.
    var burst = minWorkers;
    using var released = new ManualResetEventSlim(false);
    var started = 0;
    var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var work = Enumerable.Range(0, burst).Select(_ => Task.Run(() => {
      if (Interlocked.Increment(ref started) == burst) { allStarted.TrySetResult(); }
      released.Wait(TimeSpan.FromSeconds(30));
    })).ToArray();

    // Every burst item must be RUNNING before probing, or the measurement races the ramp-up and
    // reports the pool growing rather than the pool saturated.
    using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await allStarted.Task.WaitAsync(startCts.Token).ConfigureAwait(false);

    // Probe while the burst holds those threads. Worst case is the number that matters: a pod dies
    // on consecutive probe failures, so the tail decides whether it lives, not the average.
    var worst = TimeSpan.Zero;
    var probes = 0;
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(3)) {
      var sw = Stopwatch.StartNew();
      await Task.Run(_livenessWork).ConfigureAwait(false);
      sw.Stop();
      if (sw.Elapsed > worst) { worst = sw.Elapsed; }
      probes++;
    }

    released.Set();
    await Task.WhenAll(work).ConfigureAwait(false);

    await Assert.That(probes).IsGreaterThan(0)
      .Because("a run that never probed proves nothing about responsiveness.");
    await Assert.That(started).IsEqualTo(burst)
      .Because("the whole burst must be resident, or the pool was never actually saturated.");

    // The real budget is the liveness probe's: 10s timeout, three consecutive failures before the
    // kill. One second is far inside that and far outside what a floored pool should ever need.
    await Assert.That(worst).IsLessThan(TimeSpan.FromSeconds(1))
      .Because($"with {burst} pool threads parked -- exactly the floor -- liveness must still get a "
             + "thread promptly; when it stopped, Kubernetes killed the pod and the fleet entered a "
             + $"restart loop. Worst probe was {worst.TotalMilliseconds:F0}ms over {probes} probes.");
  }
}
