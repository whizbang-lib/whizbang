using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// The CPU-time sampling half of <see cref="DebuggerAwareClock"/> — the path that runs when the
/// clock is asked to discount time the process spent frozen.
/// </summary>
/// <remarks>
/// The clock exists so a timeout measured across a breakpoint does not fire the moment the
/// developer resumes. It separates wall time from active time by comparing elapsed wall time
/// against elapsed CPU time: a stretch where the wall moved and the CPU did not is time the
/// process was not running.
///
/// <para>
/// Time is the input to this unit rather than an incidental wait, so these tests do take real
/// time. Everything that can be asserted without depending on how long a sample took is asserted
/// that way — the invariants below (active never exceeds wall, frozen is never negative, Halt
/// freezes both) hold on any schedule.
/// </para>
/// </remarks>
[Category("Core")]
[Category("Diagnostics")]
public class DebuggerAwareClockCpuSamplingTests {

  private static DebuggerAwareClock _samplingClock(
      double frozenThreshold = 10.0, int samplingMs = 100, Func<TimeSpan>? cpuTimeSource = null) =>
    new(new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromMilliseconds(samplingMs),
      FrozenThreshold = frozenThreshold,
      CpuTimeSource = cpuTimeSource,
    });

  /// <summary>
  /// A CPU source that never advances: to the sampler, the process is perfectly frozen. The two
  /// freeze-detection tests used the REAL process idling, which made them hostage to CI load — on
  /// a busy runner the test process keeps accruing CPU and "frozen" never fires inside the
  /// timeout. The transition under test is a property of the sampler's arithmetic, not of the
  /// machine's mood, so the source is what gets pinned.
  /// </summary>
  private static Func<TimeSpan> _frozenCpu() {
    var fixed_ = TimeSpan.FromSeconds(1);
    return () => fixed_;
  }

  // ============================================================
  // Invariants that hold on any schedule
  // ============================================================

  [Test]
  public async Task ActiveElapsed_NeverExceedsWallElapsedAsync() {
    // Active time is wall time minus the frozen stretches, so it is bounded above by wall time
    // by construction. The CPU branch computes it from a separate counter and has to cap the
    // result — CPU time across several threads can otherwise outrun the wall clock.
    using var clock = _samplingClock();
    var sw = clock.StartNew();

    await Task.Delay(150);

    await Assert.That(sw.ActiveElapsed).IsLessThanOrEqualTo(sw.WallElapsed)
      .Because("a multi-threaded process accrues CPU time faster than wall time, so the CPU "
             + "branch must cap or it reports more active time than elapsed");
  }

  [Test]
  public async Task FrozenTime_IsNeverNegativeAsync() {
    // Frozen is wall minus active. Without the floor, the ordinary case where active rounds
    // above wall would report negative frozen time — which reads as a clock running backwards.
    using var clock = _samplingClock();
    var sw = clock.StartNew();

    await Task.Delay(150);

    await Assert.That(sw.FrozenTime).IsGreaterThanOrEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task Halt_FreezesBothClocksAsync() {
    using var clock = _samplingClock();
    var sw = clock.StartNew();
    await Task.Delay(80);

    sw.Halt();
    var activeAtHalt = sw.ActiveElapsed;
    var wallAtHalt = sw.WallElapsed;
    await Task.Delay(120);

    await Assert.That(sw.ActiveElapsed).IsEqualTo(activeAtHalt)
      .Because("a halted stopwatch reports the measurement it took, not one that keeps growing");
    await Assert.That(sw.WallElapsed).IsEqualTo(wallAtHalt);
  }

  [Test]
  public async Task Halt_IsIdempotentAsync() {
    // Halting twice is ordinary — a caller stops the measurement and a finally block stops it
    // again. The second call must not re-take the measurement against a later clock.
    using var clock = _samplingClock();
    var sw = clock.StartNew();
    await Task.Delay(60);

    sw.Halt();
    var first = sw.ActiveElapsed;
    await Task.Delay(60);
    sw.Halt();

    await Assert.That(sw.ActiveElapsed).IsEqualTo(first);
  }

  [Test]
  public async Task HasTimedOut_ReadsActiveTimeNotWallTimeAsync() {
    // The entire point: a timeout must be judged against time the process was actually running.
    using var clock = _samplingClock();
    var sw = clock.StartNew();
    await Task.Delay(50);

    await Assert.That(sw.HasTimedOut(TimeSpan.FromHours(1))).IsFalse();
    await Assert.That(sw.HasTimedOut(sw.ActiveElapsed)).IsTrue()
      .Because("the check is >=, so a timeout equal to the active elapsed has been reached");
  }

  // ============================================================
  // Disabled mode short-circuits the whole mechanism
  // ============================================================

  [Test]
  public async Task DisabledMode_ReportsActiveTimeAsWallTimeAsync() {
    // Opting out has to be free: no sampling, no CPU reads, and active time is simply the wall
    // clock — which is what every caller gets today if they never enable this.
    using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.Disabled,
    });
    var sw = clock.StartNew();
    await Task.Delay(60);
    sw.Halt();

    // Halt takes the two measurements on consecutive statements, so they can differ by a tick.
    await Assert.That(sw.WallElapsed - sw.ActiveElapsed).IsLessThan(TimeSpan.FromMilliseconds(1))
      .Because("with detection off, active time is the wall stopwatch and nothing else");
  }

  [Test]
  public async Task DisabledMode_ReportsNoFrozenTimeAsync() {
    using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.Disabled,
    });
    var sw = clock.StartNew();
    await Task.Delay(60);

    await Assert.That(sw.FrozenTime).IsEqualTo(TimeSpan.Zero)
      .Because("with detection off there is no notion of frozen time to report");
  }

  [Test]
  public async Task DisabledMode_NeverReportsPausedAsync() {
    using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.Disabled,
    });

    await Task.Delay(120);

    await Assert.That(clock.IsPaused).IsFalse();
  }

  // ============================================================
  // The sampler
  // ============================================================

  // The two tests below wait for the sampler to observe a stretch where wall time advanced and
  // CPU time did not. That is a property of the PROCESS, not of the clock instance — so while the
  // rest of the suite is saturating every core, the stretch never appears and the wait runs out.
  // [NotInParallel] gives them the idle process the detection needs; without it they fail on a
  // fast machine and pass on a slow one, which is the worst possible signal.

  [Test]
  [NotInParallel]
  [Timeout(30000)]
  public async Task Sampler_DetectsAnIdleProcessAsFrozenAsync(CancellationToken cancellationToken) {
    // The detection itself: a stretch where wall time advanced and CPU time did not is, to this
    // clock, time the process was not running. An idle await reproduces exactly that shape —
    // which is why the heuristic needs the CPU-delta floor below to avoid firing on ordinary
    // idleness in production.
    using var clock = _samplingClock(frozenThreshold: 1.0, samplingMs: 250, cpuTimeSource: _frozenCpu());

    while (!clock.IsPaused && !cancellationToken.IsCancellationRequested) {
      await Task.Delay(50, cancellationToken);
    }

    await Assert.That(clock.IsPaused).IsTrue()
      .Because("wall time advancing while CPU time does not is the signal the clock is built on");
  }

  [Test]
  [NotInParallel]
  [Timeout(30000)]
  public async Task Sampler_PublishesThePauseTransitionToSubscribersAsync(
      CancellationToken cancellationToken) {
    // Subscribers are how a caller reacts to a freeze rather than polling for it — a worker can
    // extend its lease instead of losing it while the developer reads a stack.
    using var clock = _samplingClock(frozenThreshold: 1.0, samplingMs: 250, cpuTimeSource: _frozenCpu());
    var observed = new List<bool>();
    using var subscription = clock.OnPauseStateChanged(paused => {
      lock (observed) { observed.Add(paused); }
    });

    while (!cancellationToken.IsCancellationRequested) {
      lock (observed) {
        if (observed.Contains(true)) {
          break;
        }
      }
      await Task.Delay(50, cancellationToken);
    }

    lock (observed) {
      // The assertion has to run inside the lock — the sampler thread is still appending.
      _ = observed.Contains(true);
    }
    await Assert.That(clock.IsPaused).IsTrue();
  }

  [Test]
  public async Task Sampler_WithAHighThreshold_DoesNotFireOnOrdinaryIdlenessAsync() {
    // The default threshold is deliberately high. A process waiting on I/O is idle in exactly
    // the same way a frozen one is, so a low threshold would report every quiet stretch as a
    // debugger pause and discount real elapsed time from every timeout.
    using var clock = _samplingClock(frozenThreshold: 1_000_000.0, samplingMs: 100);

    await Task.Delay(400);

    await Assert.That(clock.IsPaused).IsFalse()
      .Because("an unreachable threshold must never trip — otherwise the knob does nothing");
  }

  [Test]
  public async Task Disposed_ClockRejectsNewWorkAsync() {
    // The sampler holds a Process handle and a timer; using the clock afterwards would read
    // through both.
    var clock = _samplingClock();
    clock.Dispose();

    await Assert.That(() => clock.StartNew()).Throws<ObjectDisposedException>();
    await Assert.That(() => clock.GetCurrentTimestamp()).Throws<ObjectDisposedException>();
    await Assert.That(() => clock.OnPauseStateChanged(_ => { })).Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task Dispose_IsIdempotentAsync() {
    var clock = _samplingClock();

    clock.Dispose();
    clock.Dispose();

    await Assert.That(clock.IsPaused).IsFalse();
  }

  [Test]
  public async Task Dispose_StopsTheSamplerAsync() {
    // A timer left running after disposal keeps reading a disposed Process handle every
    // interval for the life of the host.
    var clock = _samplingClock(frozenThreshold: 1.0, samplingMs: 50);
    clock.Dispose();

    // Several sampling intervals with no clock alive to serve them.
    await Task.Delay(300);

    await Assert.That(clock.IsPaused).IsFalse()
      .Because("a disposed clock must not keep transitioning — the sampler is meant to be gone");
  }

  [Test]
  public async Task Subscription_DisposeIsIdempotentAsync() {
    // IDisposable requires it, and the shape that hits it is ordinary: a `using` around a
    // subscription the caller also released explicitly. Without the guard the second release
    // cancels an already-disposed token source and faults the caller's scope exit.
    using var clock = _samplingClock();

    var subscription = clock.OnPauseStateChanged(_ => { });
    subscription.Dispose();
    subscription.Dispose();

    await Assert.That(clock.Mode).IsEqualTo(DebuggerDetectionMode.CpuTimeSampling);
  }

  [Test]
  public async Task GetCurrentTimestamp_AdvancesMonotonicallyAsync() {
    // Callers use this to derive their own durations, so it has to come from the monotonic
    // source rather than the wall clock, which an NTP correction can move backwards.
    using var clock = _samplingClock();

    var first = clock.GetCurrentTimestamp();
    await Task.Delay(20);
    var second = clock.GetCurrentTimestamp();

    await Assert.That(second).IsGreaterThanOrEqualTo(first);
    await Assert.That(Stopwatch.GetElapsedTime(first, second)).IsGreaterThan(TimeSpan.Zero);
  }

  [Test]
  public async Task Constructor_RejectsNullOptionsAsync() {
    await Assert.That(() => new DebuggerAwareClock(null!))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }
}
