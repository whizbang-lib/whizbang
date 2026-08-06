using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for <see cref="ReceiveLivenessWatchdog"/> — the consume-liveness guard that
/// detects a receiver that has gone silent while its subscription still has a backlog
/// (a dropped session receiver link presents exactly this way: no errors, no receives,
/// healthy process) and triggers subscription recovery.
///
/// All time flows through FakeTimeProvider and the backlog probe / recovery callbacks are
/// counting fakes, so every test is deterministic — no real clocks, no broker.
/// </summary>
[Timeout(10_000)]
public class ReceiveLivenessWatchdogTests {
  private static readonly TimeSpan _threshold = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

  // ===== ProbeAsync: silence/backlog decision matrix =====

  [Test]
  public async Task ProbeAsync_ActivityWithinThreshold_DoesNotProbeBacklogOrRecoverAsync() {
    var fixture = new WatchdogFixture(backlogCount: 5);
    fixture.Watchdog.Track("topic-a", "sub-a");
    fixture.Time.Advance(_threshold - TimeSpan.FromSeconds(1));

    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.BacklogProbeCalls).IsEqualTo(0)
      .Because("a subscription inside its silence threshold is presumed live — the admin plane should not be queried");
    await Assert.That(fixture.RecoveryCalls).IsEqualTo(0);
  }

  [Test]
  public async Task ProbeAsync_SilentPastThresholdWithBacklog_TriggersRecoveryAsync() {
    var fixture = new WatchdogFixture(backlogCount: 5);
    fixture.Watchdog.Track("topic-a", "sub-a");
    fixture.Time.Advance(_threshold + TimeSpan.FromSeconds(1));

    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.RecoveryCalls).IsEqualTo(1)
      .Because("silent past the threshold with messages waiting is the silent-receiver-stall signature — recovery must fire");
  }

  [Test]
  public async Task ProbeAsync_SilentPastThresholdWithoutBacklog_RebaselinesInsteadOfRecoveringAsync() {
    var fixture = new WatchdogFixture(backlogCount: 0);
    fixture.Watchdog.Track("topic-a", "sub-a");
    fixture.Time.Advance(_threshold + TimeSpan.FromSeconds(1));

    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.RecoveryCalls).IsEqualTo(0)
      .Because("an idle subscription with an empty backlog is healthy, not stalled");
    await Assert.That(fixture.BacklogProbeCalls).IsEqualTo(1);

    // The empty-backlog check re-baselines the subscription: an immediate second sweep
    // must not query the admin plane again.
    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.BacklogProbeCalls).IsEqualTo(1)
      .Because("a healthy-idle verdict resets the silence window so quiet services do not hammer the admin API every sweep");
  }

  [Test]
  public async Task RecordActivity_ResetsSilenceWindowAsync() {
    var fixture = new WatchdogFixture(backlogCount: 5);
    fixture.Watchdog.Track("topic-a", "sub-a");
    fixture.Time.Advance(_threshold + TimeSpan.FromMinutes(30));

    fixture.Watchdog.RecordActivity("topic-a", "sub-a");
    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.BacklogProbeCalls).IsEqualTo(0);
    await Assert.That(fixture.RecoveryCalls).IsEqualTo(0)
      .Because("a received message proves the receiver is alive regardless of how long it was previously silent");
  }

  [Test]
  public async Task Track_ExistingSubscription_ResetsBaselineAsync() {
    var fixture = new WatchdogFixture(backlogCount: 5);
    fixture.Watchdog.Track("topic-a", "sub-a");
    fixture.Time.Advance(_threshold + TimeSpan.FromSeconds(1));

    fixture.Watchdog.Track("topic-a", "sub-a");
    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.RecoveryCalls).IsEqualTo(0)
      .Because("re-tracking happens when a subscription is re-established — the new receiver starts with a fresh silence window");
  }

  [Test]
  public async Task ProbeAsync_NothingTracked_NoOpAsync() {
    var fixture = new WatchdogFixture(backlogCount: 5);

    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.BacklogProbeCalls).IsEqualTo(0);
    await Assert.That(fixture.RecoveryCalls).IsEqualTo(0);
  }

  // ===== ProbeAsync: multi-subscription and failure behavior =====

  [Test]
  public async Task ProbeAsync_MultipleStalledSubscriptions_SingleRecoveryPerSweepAsync() {
    var fixture = new WatchdogFixture(backlogCount: 5);
    fixture.Watchdog.Track("topic-a", "sub-a");
    fixture.Watchdog.Track("topic-b", "sub-b");
    fixture.Time.Advance(_threshold + TimeSpan.FromSeconds(1));

    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.RecoveryCalls).IsEqualTo(1)
      .Because("recovery re-establishes every subscription, so one detection per sweep is sufficient");
  }

  [Test]
  public async Task ProbeAsync_AfterRecovery_RebaselinesAllTrackedSubscriptionsAsync() {
    var fixture = new WatchdogFixture(backlogCount: 5);
    fixture.Watchdog.Track("topic-a", "sub-a");
    fixture.Watchdog.Track("topic-b", "sub-b");
    fixture.Time.Advance(_threshold + TimeSpan.FromSeconds(1));
    await fixture.Watchdog.ProbeAsync();

    await fixture.Watchdog.ProbeAsync();

    await Assert.That(fixture.RecoveryCalls).IsEqualTo(1)
      .Because("recovery resets every silence window — an immediate second sweep must not re-trigger while the new receivers spin up");
  }

  [Test]
  public async Task ProbeAsync_BacklogProbeThrows_ContinuesToNextSubscriptionWithoutRecoveryAsync() {
    var probeCalls = 0;
    var recoveryCalls = 0;
    var time = new FakeTimeProvider();
    var watchdog = new ReceiveLivenessWatchdog(
      new AzureServiceBusOptions {
        ReceiveLivenessSilenceThreshold = _threshold,
        ReceiveLivenessProbeInterval = _interval
      },
      (topic, _, _) => {
        probeCalls++;
        return topic == "topic-broken"
          ? Task.FromException<long>(new InvalidOperationException("admin plane unavailable"))
          : Task.FromResult(5L);
      },
      _ => { recoveryCalls++; return Task.CompletedTask; },
      time,
      NullLogger.Instance);
    // Ordinal key order makes "topic-broken" probe before "topic-live".
    watchdog.Track("topic-broken", "sub-a");
    watchdog.Track("topic-live", "sub-b");
    time.Advance(_threshold + TimeSpan.FromSeconds(1));

    await watchdog.ProbeAsync();

    await Assert.That(probeCalls).IsEqualTo(2)
      .Because("an admin-plane failure on one subscription must not mask a genuine stall on another");
    await Assert.That(recoveryCalls).IsEqualTo(1)
      .Because("the failed probe is inconclusive (no recovery); the successful probe with backlog still detects the stall");
  }

  // ===== Periodic loop lifecycle =====

  [Test]
  public async Task Start_TimerTick_RunsSweepAndRecoversStalledSubscriptionAsync() {
    var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var time = new FakeTimeProvider();
    var watchdog = new ReceiveLivenessWatchdog(
      new AzureServiceBusOptions {
        // Threshold below the interval so a single tick observes a stalled subscription.
        ReceiveLivenessSilenceThreshold = TimeSpan.FromSeconds(30),
        ReceiveLivenessProbeInterval = _interval
      },
      (_, _, _) => Task.FromResult(5L),
      _ => { recovered.TrySetResult(); return Task.CompletedTask; },
      time,
      NullLogger.Instance);
    watchdog.Track("topic-a", "sub-a");

    watchdog.Start();
    time.Advance(_interval);

    await recovered.Task;
    await watchdog.DisposeAsync();
  }

  [Test]
  public async Task Start_CalledTwice_IsIdempotentAsync() {
    var fixture = new WatchdogFixture(backlogCount: 0);

    fixture.Watchdog.Start();
    fixture.Watchdog.Start();

    await fixture.Watchdog.DisposeAsync();
    // Reaching this point without a hang or exception is the assertion: a second Start
    // must not spawn a second loop that dispose would fail to stop.
    await Assert.That(fixture.RecoveryCalls).IsEqualTo(0);
  }

  [Test]
  public async Task DisposeAsync_AfterStart_StopsTheLoopAsync() {
    var fixture = new WatchdogFixture(backlogCount: 5);
    fixture.Watchdog.Track("topic-a", "sub-a");
    fixture.Watchdog.Start();

    await fixture.Watchdog.DisposeAsync();
    fixture.Time.Advance(_threshold + _interval + _interval);

    await Assert.That(fixture.BacklogProbeCalls).IsEqualTo(0)
      .Because("after DisposeAsync completes the loop has exited — later clock movement must not produce sweeps");
  }

  [Test]
  public async Task DisposeAsync_CalledTwice_IsIdempotentAsync() {
    var fixture = new WatchdogFixture(backlogCount: 0);
    fixture.Watchdog.Start();

    await fixture.Watchdog.DisposeAsync();
    await fixture.Watchdog.DisposeAsync();

    await Assert.That(fixture.RecoveryCalls).IsEqualTo(0);
  }

  /// <summary>
  /// Watchdog wired to counting fakes: a constant-backlog probe, a counting recovery
  /// callback, and a FakeTimeProvider the test advances explicitly.
  /// </summary>
  private sealed class WatchdogFixture {
    private int _backlogProbeCalls;
    private int _recoveryCalls;

    public WatchdogFixture(long backlogCount) {
      Time = new FakeTimeProvider();
      Watchdog = new ReceiveLivenessWatchdog(
        new AzureServiceBusOptions {
          ReceiveLivenessSilenceThreshold = _threshold,
          ReceiveLivenessProbeInterval = _interval
        },
        (_, _, _) => {
          Interlocked.Increment(ref _backlogProbeCalls);
          return Task.FromResult(backlogCount);
        },
        _ => {
          Interlocked.Increment(ref _recoveryCalls);
          return Task.CompletedTask;
        },
        Time,
        NullLogger.Instance);
    }

    public FakeTimeProvider Time { get; }
    public ReceiveLivenessWatchdog Watchdog { get; }
    public int BacklogProbeCalls => _backlogProbeCalls;
    public int RecoveryCalls => _recoveryCalls;
  }
}
