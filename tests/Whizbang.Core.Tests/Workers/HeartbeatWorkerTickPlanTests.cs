#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The heartbeat worker's per-tick decision, taken as a pure function of the clock and what the
/// worker last recorded. The call-saving slow cadence stays (one beat per slow interval while the
/// alive-lock is held); what is added is a watchdog: the worker tracks the last beat the database
/// accepted and, when that beat is about to age past the derived stale threshold, forces one
/// immediately instead of waiting for the regular cadence. A commit stall that delays one beat
/// can therefore no longer let a live instance be declared dead.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/HeartbeatWorker.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class HeartbeatWorkerTickPlanTests {
  private static readonly DateTimeOffset _t0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task NoBeatYet_BeatsImmediatelyAsInitialAsync() {
    var worker = _worker(lockHeld: false);

    var plan = worker.PlanNextTick(_t0);

    await Assert.That(plan.BeatNow).IsTrue().Because("the registry row must exist before anything else can see the instance");
    await Assert.That(plan.Reason).IsEqualTo(HeartbeatBeatReason.Initial);
    await Assert.That(plan.Wait).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task JustAccepted_FastCadence_WaitsOneIntervalAsync() {
    var worker = _worker(lockHeld: false);
    worker.MarkAcceptedForTests(_t0);

    var plan = worker.PlanNextTick(_t0);

    await Assert.That(plan.BeatNow).IsFalse();
    await Assert.That(plan.Wait).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("with the lock absent the cadence is the fast interval, and nothing is due sooner");
  }

  [Test]
  public async Task JustAccepted_SlowCadence_WaitNeverExceedsOneFastIntervalAsync() {
    // Slow cadence is 60 s, but the worker re-plans every fast interval so a change in the lock
    // state or a late beat is noticed within 30 s rather than 60. The slow cadence still governs
    // when the next beat is SENT; the wait only governs when the worker looks again.
    var worker = _worker(lockHeld: true);
    worker.MarkAcceptedForTests(_t0);

    var plan = worker.PlanNextTick(_t0);

    await Assert.That(plan.BeatNow).IsFalse();
    await Assert.That(plan.Wait).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("the planning wait is capped at the watchdog lead so a late beat is caught in time");
  }

  [Test]
  public async Task SlowCadence_BeforeTheSlowIntervalElapses_DoesNotBeatAsync() {
    // 45 s after an accepted beat under the alive-lock: the fast interval has elapsed but the slow
    // one has not. The call-saving design is preserved: no beat.
    var worker = _worker(lockHeld: true);
    worker.MarkAcceptedForTests(_t0);

    var plan = worker.PlanNextTick(_t0.AddSeconds(45));

    await Assert.That(plan.BeatNow).IsFalse()
      .Because("the lock is the primary liveness signal; the table beat stays on the slow cadence");
    await Assert.That(plan.Wait).IsEqualTo(TimeSpan.FromSeconds(15))
      .Because("the next look is when the slow interval completes");
  }

  [Test]
  public async Task CadenceElapsed_BeatsAsRegularAsync() {
    var worker = _worker(lockHeld: true);
    worker.MarkAcceptedForTests(_t0);

    var plan = worker.PlanNextTick(_t0.AddSeconds(60));

    await Assert.That(plan.BeatNow).IsTrue();
    await Assert.That(plan.Reason).IsEqualTo(HeartbeatBeatReason.Regular);
  }

  [Test]
  public async Task LastAcceptedBeatNearTheThreshold_BeatsAsWatchdogAsync() {
    // Threshold 150 s, lead 30 s: at 120 s since the last ACCEPTED beat the worker forces a beat
    // regardless of cadence. This is the belt to the cadence's suspenders: the regular beat at 60 s
    // was sent but never accepted (a commit stall swallowed it), and without this the instance
    // would sail past 150 s and be announced dead while running.
    var worker = _worker(lockHeld: true);
    worker.MarkAcceptedForTests(_t0);

    var plan = worker.PlanNextTick(_t0.AddSeconds(120));

    await Assert.That(plan.BeatNow).IsTrue();
    await Assert.That(plan.Reason).IsEqualTo(HeartbeatBeatReason.Watchdog)
      .Because("a beat forced because the last accepted one is aging out is a watchdog beat, and is counted as such");
  }

  [Test]
  public async Task WatchdogDeadline_TakesPrecedenceOverRegularAsync() {
    // At exactly the deadline both conditions hold; the reason reported is the one an operator
    // needs to see, because a watchdog beat means a regular one went missing.
    var worker = _worker(lockHeld: false);
    worker.MarkAcceptedForTests(_t0);

    var plan = worker.PlanNextTick(_t0.AddSeconds(200));

    await Assert.That(plan.Reason).IsEqualTo(HeartbeatBeatReason.Watchdog);
  }

  [Test]
  public async Task RegularBeatUnaccepted_WatchdogFiresOneLeadBeforeTheThresholdAsync() {
    // The regular beat at 60 s was sent but never accepted (the coordinator threw, so the failure
    // arm ran). The worker retries on its spacing; if every retry keeps failing, the deadline at
    // 120 s still forces a watchdog beat, and from then on each plan says so.
    var worker = _worker(lockHeld: true);
    worker.MarkAcceptedForTests(_t0);
    worker.MarkFailedAttemptForTests(_t0.AddSeconds(60));

    var retrying = worker.PlanNextTick(_t0.AddSeconds(61));
    var deadline = worker.PlanNextTick(_t0.AddSeconds(120));

    await Assert.That(retrying.BeatNow).IsFalse().Because("one second after a failure the retry spacing holds");
    await Assert.That(deadline.BeatNow).IsTrue();
    await Assert.That(deadline.Reason).IsEqualTo(HeartbeatBeatReason.Watchdog)
      .Because("the last ACCEPTED beat is what ages; failed attempts do not extend the instance's life");
  }

  [Test]
  public async Task FailedAttempt_RetriesAfterHalfTheLeadAsync() {
    // A failed beat must not be retried in a hot loop against a struggling database, and must not
    // wait a whole cadence either: half the watchdog lead (15 s with the defaults) leaves two
    // retries inside the lead.
    var worker = _worker(lockHeld: false);
    worker.MarkFailedAttemptForTests(_t0);

    var plan = worker.PlanNextTick(_t0.AddSeconds(5));

    await Assert.That(plan.BeatNow).IsFalse();
    await Assert.That(plan.Wait).IsEqualTo(TimeSpan.FromSeconds(10))
      .Because("15 s of retry spacing, 5 s already elapsed");
  }

  [Test]
  public async Task FailedAttempt_AfterTheSpacing_BeatsAgainAsync() {
    var worker = _worker(lockHeld: false);
    worker.MarkFailedAttemptForTests(_t0);

    var plan = worker.PlanNextTick(_t0.AddSeconds(15));

    await Assert.That(plan.BeatNow).IsTrue()
      .Because("no beat has ever been accepted, so once the spacing passes the initial beat is due");
    await Assert.That(plan.Reason).IsEqualTo(HeartbeatBeatReason.Initial);
  }

  [Test]
  public async Task LockToggle_ChangesTheCadenceOnTheNextPlanAsync() {
    var lockSource = new ToggleLock(held: true);
    var worker = _worker(lockSource);
    worker.MarkAcceptedForTests(_t0);

    var slow = worker.PlanNextTick(_t0.AddSeconds(45));
    lockSource.IsAliveLockHeld = false;
    var fast = worker.PlanNextTick(_t0.AddSeconds(45));

    await Assert.That(slow.BeatNow).IsFalse().Because("held: 45 s is inside the 60 s slow cadence");
    await Assert.That(fast.BeatNow).IsTrue().Because("released: 45 s is past the 30 s fast cadence");
    await Assert.That(fast.Reason).IsEqualTo(HeartbeatBeatReason.Regular);
  }

  [Test]
  public async Task LastAcceptedAt_IsNullUntilABeatIsAcceptedAsync() {
    var worker = _worker(lockHeld: false);

    await Assert.That(worker.LastAcceptedAt).IsNull();

    worker.MarkAcceptedForTests(_t0);

    await Assert.That(worker.LastAcceptedAt).IsEqualTo(_t0);
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static HeartbeatWorker _worker(bool lockHeld) => _worker(new ToggleLock(lockHeld));

  private static HeartbeatWorker _worker(IInstanceAliveLockSource lockSource) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    var sp = services.BuildServiceProvider();

    return new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
      schemaReadyGate: _readyGate(),
      options: Options.Create(new HeartbeatWorkerOptions { IntervalSeconds = 30, SlowIntervalSeconds = 60 }),
      logger: NullLogger<HeartbeatWorker>.Instance,
      lifecycleState: HeartbeatTestDependencies.LifecycleState,
      libraryVersion: HeartbeatTestDependencies.Version,
      pinnedPool: null,
      aliveLockSource: lockSource);
  }

  private sealed class ToggleLock(bool held) : IInstanceAliveLockSource {
    public bool IsAliveLockHeld { get; set; } = held;
  }

  /// <summary>The worker refuses a null gate; a ready one lets the loop past its startup wait.</summary>
  private static SchemaReadyGate _readyGate() {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return gate;
  }
}
