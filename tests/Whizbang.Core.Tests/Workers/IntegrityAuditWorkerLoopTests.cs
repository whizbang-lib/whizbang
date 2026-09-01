using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The audit worker's scheduling loop. <c>ComputeNextAuditDelay</c> is unit-tested on its own;
/// what was never run is the loop that feeds it, and the loop is what decides which delay a real
/// host actually waits.
/// <para>
/// The default is startup-first: the first audit fires after a jittered window with a
/// thirty-second floor, so historical divergence heals shortly after a deploy and a fleet rollout
/// does not have every replica audit at the same instant. <c>AuditOnStartup = false</c> is the
/// documented opt-out that restores interval-first scheduling. Those are opposite behaviors on the
/// very first cycle, which is the only cycle a freshly deployed service gets to show.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
public class IntegrityAuditWorkerLoopTests {

  [Test]
  [Timeout(30000)]
  public async Task StartupFirstScheduling_HoldsTheFirstAuditBackByAtLeastTheFloorAsync(
      CancellationToken cancellationToken) {
    // Jitter set to zero leaves only the floor. A worker that audits immediately on start has lost
    // the de-synchronization the window exists for: every replica in a rollout would sweep at
    // once, which is the load spike the jitter was added to avoid.
    var scopes = new CountingScopeFactory(signalAfter: 1);
    var worker = _worker(scopes, new StreamIntegrityOptions {
      AuditEnabled = true,
      AuditOnStartup = true,
      StartupAuditMaxJitterSeconds = 0,
    });

    await worker.StartAsync(CancellationToken.None);
    var firstToFinish = await Task.WhenAny(
      scopes.Reached.Task, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(ReferenceEquals(firstToFinish, scopes.Reached.Task)).IsFalse();
    await Assert.That(scopes.Count).IsEqualTo(0)
      .Because("the startup window has a thirty-second floor — an audit inside three seconds means "
             + "the floor is gone and a rollout sweeps in lockstep");
  }

  [Test]
  [Timeout(30000)]
  public async Task IntervalFirstScheduling_UsesTheIntervalForTheFirstDelayTooAsync(
      CancellationToken cancellationToken) {
    // The opt-out's whole effect is on the FIRST delay: interval-first means the interval governs
    // from the start rather than the startup window. With the interval at zero that is observable
    // immediately, and it is the same knob an operator sets to disable startup auditing.
    var scopes = new CountingScopeFactory(signalAfter: 1);
    var worker = _worker(scopes, new StreamIntegrityOptions {
      AuditEnabled = true,
      AuditOnStartup = false,
      AuditIntervalMinutes = 0,
    });

    await worker.StartAsync(CancellationToken.None);
    try {
      await scopes.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(scopes.Count).IsGreaterThanOrEqualTo(1)
      .Because("interval-first means the first delay is the interval; still waiting on the "
             + "startup window here would be the opt-out failing to opt out");
  }

  [Test]
  [Timeout(30000)]
  public async Task AuditDisabled_NeverOpensAScopeAsync(CancellationToken cancellationToken) {
    // The opt-out has to stop the work, not merely stop the logging. Interval-first with a zero
    // interval, so anything other than the disabled check would audit at once.
    var scopes = new CountingScopeFactory(signalAfter: 1);
    var worker = _worker(scopes, new StreamIntegrityOptions {
      AuditEnabled = false,
      AuditOnStartup = false,
      AuditIntervalMinutes = 0,
    });

    await worker.StartAsync(CancellationToken.None);
    var firstToFinish = await Task.WhenAny(
      scopes.Reached.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(ReferenceEquals(firstToFinish, scopes.Reached.Task)).IsFalse();
    await Assert.That(scopes.Count).IsEqualTo(0)
      .Because("AuditEnabled = false is an operator switching the sweep off, not turning its log "
             + "line down");
  }

  private static IntegrityAuditWorker _worker(IServiceScopeFactory scopeFactory, StreamIntegrityOptions options) =>
    new(scopeFactory,
        SchemaReadyGate.AlreadyReady(),
        Options.Create(options),
        NullLogger<IntegrityAuditWorker>.Instance);

  /// <summary>
  /// Hands out real (empty) scopes and counts them. One scope per audit cycle, so the count is the
  /// cycle count — an observable the loop cannot fake. An empty scope also means the cycle finds
  /// no coordinator and returns early, which is the documented "no cross-service infrastructure"
  /// case rather than a cold start.
  /// </summary>
  private sealed class CountingScopeFactory(int signalAfter) : IServiceScopeFactory {
    private readonly ServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
    private int _count;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Count => Volatile.Read(ref _count);

    public IServiceScope CreateScope() {
      if (Interlocked.Increment(ref _count) >= signalAfter) {
        Reached.TrySetResult();
      }
      return _provider.CreateScope();
    }
  }
}
