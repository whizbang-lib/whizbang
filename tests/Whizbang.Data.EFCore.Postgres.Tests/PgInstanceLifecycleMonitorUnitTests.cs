using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The lifecycle monitor's two derived numbers, provable without a database. The stale threshold
/// comes from the heartbeat options through the same helper the writer uses, so the reader can
/// never be tighter than the cadence it is judging. The tick interval adapts: a fast five second
/// scan while any registered instance is more than half way to the threshold, relaxing toward a
/// sixth of the threshold (bounded at thirty seconds) while the whole fleet is fresh. Failover
/// detection latency is unchanged when it matters and the idle scan rate falls by a factor of
/// five when it does not.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgInstanceLifecycleMonitor.cs</code-under-test>
[Category("Shard1")]
public class PgInstanceLifecycleMonitorUnitTests {
  private sealed class NullBus : ISignalBus {
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal => new NoopSub();
    private sealed class NoopSub : ISignalSubscription { public void Dispose() { } }
  }

  private static PgInstanceLifecycleMonitor _monitor(HeartbeatWorkerOptions? heartbeat) => new(
    Options.Create(new WhizbangNotificationOptions()),
    new ConfigurationBuilder().Build(),
    new NullBus(),
    NullLogger<PgInstanceLifecycleMonitor>.Instance,
    heartbeatOptions: heartbeat is null ? null : Options.Create(heartbeat));

  [Test]
  public async Task StaleThreshold_DefaultHeartbeatOptions_IsOneHundredFiftySecondsAsync() {
    // The old monitor used a hard 30 s against a writer that beats every 60 s under the
    // alive-lock. Every slow beat was therefore a coin toss with a false death announcement.
    var monitor = _monitor(new HeartbeatWorkerOptions());

    await Assert.That(monitor.StaleThreshold).IsEqualTo(TimeSpan.FromSeconds(150))
      .Because("two of the slowest cadence plus one fast interval of latency allowance");
    await Assert.That(monitor.StaleThresholdSeconds).IsEqualTo(150);
  }

  [Test]
  public async Task StaleThreshold_WithoutHeartbeatOptionsRegistered_UsesTheDefaultsAsync() {
    var monitor = _monitor(null);

    await Assert.That(monitor.StaleThreshold).IsEqualTo(HeartbeatLivenessThreshold.StaleThreshold(new HeartbeatWorkerOptions()))
      .Because("a host that never configured the worker still gets a threshold that matches the worker's defaults");
  }

  [Test]
  public async Task StaleThreshold_FollowsAConfiguredCadenceAsync() {
    var monitor = _monitor(new HeartbeatWorkerOptions { IntervalSeconds = 10, SlowIntervalSeconds = 20 });

    await Assert.That(monitor.StaleThreshold).IsEqualTo(TimeSpan.FromSeconds(50));
  }

  [Test]
  public async Task ComputeTickInterval_FleetFresh_RelaxesToASixthOfTheThresholdAsync() {
    var next = PgInstanceLifecycleMonitor.ComputeTickInterval(oldestAge: TimeSpan.FromSeconds(10), threshold: TimeSpan.FromSeconds(150));

    await Assert.That(next).IsEqualTo(TimeSpan.FromSeconds(25))
      .Because("150 / 6: six looks per threshold is plenty when nobody is close to it");
  }

  [Test]
  public async Task ComputeTickInterval_RelaxedInterval_IsBoundedAtThirtySecondsAsync() {
    var next = PgInstanceLifecycleMonitor.ComputeTickInterval(oldestAge: TimeSpan.Zero, threshold: TimeSpan.FromMinutes(10));

    await Assert.That(next).IsEqualTo(PgInstanceLifecycleMonitor.RelaxedTickCeiling);
    await Assert.That(next).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task ComputeTickInterval_RelaxedInterval_NeverFallsBelowTheFastTickAsync() {
    var next = PgInstanceLifecycleMonitor.ComputeTickInterval(oldestAge: TimeSpan.Zero, threshold: TimeSpan.FromSeconds(6));

    await Assert.That(next).IsEqualTo(PgInstanceLifecycleMonitor.FastTick);
  }

  [Test]
  public async Task ComputeTickInterval_SomeoneHalfwayToTheThreshold_TicksFastAsync() {
    // An instance 75 s into a 150 s threshold: the monitor must be looking every five seconds so
    // that when the threshold passes, the death is announced within one fast tick as before.
    var next = PgInstanceLifecycleMonitor.ComputeTickInterval(oldestAge: TimeSpan.FromSeconds(75), threshold: TimeSpan.FromSeconds(150));

    await Assert.That(next).IsEqualTo(PgInstanceLifecycleMonitor.FastTick)
      .Because("failover latency is unchanged whenever it could matter");
  }

  [Test]
  public async Task ComputeTickInterval_NoRegisteredInstances_RelaxesAsync() {
    var next = PgInstanceLifecycleMonitor.ComputeTickInterval(oldestAge: null, threshold: TimeSpan.FromSeconds(150));

    await Assert.That(next).IsEqualTo(TimeSpan.FromSeconds(25))
      .Because("an empty registry has nobody to watch closely");
  }

  [Test]
  public async Task ComputeTickInterval_NonPositiveThreshold_ThrowsAsync() {
    await Assert.That(() => PgInstanceLifecycleMonitor.ComputeTickInterval(null, TimeSpan.Zero))
      .Throws<ArgumentOutOfRangeException>();
  }
}
