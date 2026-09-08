using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The one place the stale threshold is derived from the heartbeat cadence. Readers (the lifecycle
/// monitor, the heartbeat's own peer reap) and the writer (the watchdog beat) all take it from
/// here, so an operator who changes the cadence cannot leave a reader behind on a constant that
/// no longer has any margin. The threshold is two of the slowest cadence plus one fast interval:
/// a beat may be up to one whole slow interval late before the second is even due, and the fast
/// interval on top is the latency allowance for a commit stall.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/HeartbeatLivenessThreshold.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class HeartbeatLivenessThresholdTests {

  [Test]
  public async Task Defaults_TwoSlowIntervalsPlusOneFastAsync() {
    // 30 s fast, 60 s slow: 2 * 60 + 30 = 150 s. The old monitor used 30 s against a writer that
    // beats every 60 s under the alive-lock, so every slow beat was a coin toss with death.
    var options = new HeartbeatWorkerOptions();

    await Assert.That(HeartbeatLivenessThreshold.StaleThreshold(options)).IsEqualTo(TimeSpan.FromSeconds(150))
      .Because("the slowest cadence the writer may legitimately use is the one the reader must tolerate twice");
    await Assert.That(HeartbeatLivenessThreshold.StaleThresholdSeconds(options)).IsEqualTo(150);
  }

  [Test]
  public async Task HeartbeatTableOnly_IgnoresTheSlowCadenceAsync() {
    // In table-only mode the worker never slows down, so the slow interval is not a cadence the
    // reader will ever see; deriving from it would only widen failover for nothing.
    var options = new HeartbeatWorkerOptions {
      IntervalSeconds = 30,
      SlowIntervalSeconds = 60,
      LivenessSourceMode = HeartbeatLivenessSourceMode.HeartbeatTableOnly,
    };

    await Assert.That(HeartbeatLivenessThreshold.StaleThreshold(options)).IsEqualTo(TimeSpan.FromSeconds(90))
      .Because("2 * 30 + 30: only the fast cadence exists in this mode");
  }

  [Test]
  public async Task SlowIntervalBelowFast_UsesTheFastIntervalAsTheSlowestAsync() {
    // A misconfiguration where the "slow" interval is shorter than the fast one must not shrink
    // the threshold below what the fast cadence needs.
    var options = new HeartbeatWorkerOptions { IntervalSeconds = 30, SlowIntervalSeconds = 5 };

    await Assert.That(HeartbeatLivenessThreshold.StaleThreshold(options)).IsEqualTo(TimeSpan.FromSeconds(90))
      .Because("the slowest cadence is max(fast, slow), never less than fast");
  }

  [Test]
  public async Task NonPositiveInterval_IsFlooredAtOneSecondAsync() {
    // A zero interval would give a zero threshold and every instance would be dead on arrival.
    var options = new HeartbeatWorkerOptions { IntervalSeconds = 0, SlowIntervalSeconds = 0 };

    await Assert.That(HeartbeatLivenessThreshold.StaleThreshold(options)).IsEqualTo(TimeSpan.FromSeconds(3))
      .Because("2 * 1 + 1 with both intervals floored at one second");
    await Assert.That(HeartbeatLivenessThreshold.WatchdogLead(options)).IsEqualTo(TimeSpan.FromSeconds(1));
  }

  [Test]
  public async Task WatchdogLead_IsOneFastIntervalAsync() {
    // The writer forces a beat one fast interval before the threshold: enough room for one more
    // round trip to land before any reader could call the instance dead.
    var options = new HeartbeatWorkerOptions { IntervalSeconds = 20, SlowIntervalSeconds = 90 };

    await Assert.That(HeartbeatLivenessThreshold.WatchdogLead(options)).IsEqualTo(TimeSpan.FromSeconds(20));
    await Assert.That(HeartbeatLivenessThreshold.StaleThreshold(options)).IsEqualTo(TimeSpan.FromSeconds(200));
  }

  [Test]
  public async Task NullOptions_ThrowAsync() {
    await Assert.That(() => HeartbeatLivenessThreshold.StaleThreshold(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => HeartbeatLivenessThreshold.StaleThresholdSeconds(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => HeartbeatLivenessThreshold.WatchdogLead(null!)).Throws<ArgumentNullException>();
  }
}
