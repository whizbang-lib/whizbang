using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Coverage-round-23 targets for <see cref="DebuggerAwareClock"/>: the sampler's own exception
/// handling, the "process is not frozen" fallthrough, and the active-stopwatch's resilience to a
/// broken CPU-time source.
/// </summary>
/// <remarks>
/// <para>
/// Three of these were once recorded here as unreachable. Two of them, the
/// <c>DebuggerDetectionMode.DebuggerAttached</c> arm of the sampler's switch and that switch's
/// <c>_ =&gt; false</c> default arm, were unreachable only because the sampler's sole caller was
/// the private timer the constructor creates just for <c>CpuTimeSampling</c> and <c>Auto</c>.
/// <see cref="DebuggerAwareClock.SampleCpuTime"/> is now internal, so a tick can be driven for
/// any configured mode and the answer each arm gives is asserted below rather than reasoned
/// about.
/// </para>
/// <para>
/// The third, the pause-state read loop's <c>catch (ChannelClosedException)</c>, is still
/// unreachable through <c>OnPauseStateChanged</c>: the clock only ever completes its channel
/// gracefully, and a graceful completion ends the enumeration instead of throwing. It is
/// asserted through <see cref="DebuggerAwareClock.RunPauseStateReadLoopAsync"/> — the narrowest
/// seam onto the loop — because a subscription that leaves an unobserved faulted task behind is
/// the failure the clause exists to prevent, and nothing else in the class would notice.
/// </para>
/// </remarks>
/// <docs>extending/features/debugger-aware-clock</docs>
[Category("Core")]
[Category("Diagnostics")]
public class DebuggerAwareClockCoverageTests {

  // The timeout mechanism this clock exists for depends on the sampler surviving a process that
  // is mid-exit or otherwise cannot report CPU time. If the sampler let that exception escape
  // instead of skipping the sample, an unhandled exception on the timer's background thread
  // would crash the whole host — turning a benign "process exiting" race into an outage.
  [Test]
  public async Task Sampler_WhenCpuTimeSourceThrows_SkipsTheSampleWithoutCrashingAsync() {
    var callCount = 0;
    var sampled = new SemaphoreSlim(0, int.MaxValue);
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromMilliseconds(10),
      CpuTimeSource = () => {
        var call = Interlocked.Increment(ref callCount);
        if (call == 1) {
          return TimeSpan.Zero; // the constructor's own initial read — must not throw
        }
        sampled.Release();
        throw new InvalidOperationException("process is exiting; CPU time unavailable");
      }
    };
    using var clock = new DebuggerAwareClock(options);

    var signaled = await sampled.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(signaled).IsTrue()
      .Because("the sampler timer must actually fire and call the CPU-time source at least once "
        + "for this test to exercise the catch path at all");
    await Assert.That(clock.IsPaused).IsFalse()
      .Because("a sample that failed to read CPU time must not flip pause state — it has no "
        + "wall/CPU delta to reason about, so it has to bail out silently, not guess");
  }

  // The "not frozen" conclusion is the common case: wall time and CPU time both advanced by a
  // normal amount, so the process was actually running. If this fell through to reporting
  // "frozen" instead, every ordinary in-process delay would look like a debugger pause and active
  // time would stop advancing for perfectly healthy work.
  [Test]
  public async Task Sampler_WhenCpuAndWallBothAdvanceNormally_DoesNotReportFrozenAsync() {
    var callCount = 0;
    var sampled = new SemaphoreSlim(0, int.MaxValue);
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromMilliseconds(250), // >= the 200ms wall-delta floor
      CpuTimeSource = () => {
        var call = Interlocked.Increment(ref callCount);
        if (call == 1) {
          return TimeSpan.Zero; // baseline read taken at construction
        }
        sampled.Release();
        return TimeSpan.FromMilliseconds(50); // >= the 10ms cpu-delta floor: comfortably "active"
      }
    };
    using var clock = new DebuggerAwareClock(options);

    var signaled = await sampled.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(signaled).IsTrue()
      .Because("the sampler must have actually compared a wall delta >= 200ms against a CPU "
        + "delta >= 10ms for this test to reach the fallthrough return");
    await Assert.That(clock.IsPaused).IsFalse()
      .Because("both clocks moved together, which is exactly what a healthy, unpaused process "
        + "looks like — reporting frozen here would be a false positive on every ordinary tick");
  }

  // An active stopwatch has to remain usable even when the process's CPU-time accounting is
  // unavailable at the moment it starts (e.g., right as the process is torn down). Losing this
  // fallback turns a diagnostic best-effort read into a hard failure for the caller trying to
  // time an operation.
  [Test]
  public async Task StartNew_WhenCpuTimeSourceThrowsDuringStart_ReturnsAUsableStopwatchAsync() {
    var callCount = 0;
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.Disabled, // keeps the sampler timer out of the picture entirely
      CpuTimeSource = () => {
        var call = Interlocked.Increment(ref callCount);
        return call == 1
          ? TimeSpan.Zero // the constructor's own initial read
          : throw new InvalidOperationException("process info unavailable"); // ActiveStopwatch's read
      }
    };
    using var clock = new DebuggerAwareClock(options);

    var stopwatch = clock.StartNew();

    await Assert.That(stopwatch).IsNotNull()
      .Because("StartNew must not propagate the CPU-time source's exception — a caller starting "
        + "a timing scope should never see an unrelated diagnostics failure");
    await Assert.That(stopwatch.ActiveElapsed).IsGreaterThanOrEqualTo(TimeSpan.Zero)
      .Because("the stopwatch must default to a sane starting CPU time and keep reporting "
        + "elapsed time rather than throwing on every subsequent read");
  }

  // The same resilience has to hold later, not just at construction: a CPU-time read can fail
  // mid-measurement (not only at start), and the caller reading ActiveElapsed must still get a
  // number back instead of an exception bubbling out of what looks like a property getter.
  [Test]
  public async Task ActiveElapsed_WhenCpuTimeSourceThrowsDuringMeasurement_FallsBackToWallElapsedAsync() {
    var callCount = 0;
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromDays(1), // never fires during this test
      CpuTimeSource = () => {
        var call = Interlocked.Increment(ref callCount);
        return call switch {
          1 => TimeSpan.Zero,                 // constructor's initial read
          2 => TimeSpan.FromSeconds(1),        // ActiveStopwatch's start-of-measurement read
          _ => throw new InvalidOperationException("process info unavailable") // the ActiveElapsed read under test
        };
      }
    };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    var active = stopwatch.ActiveElapsed;

    await Assert.That(active).IsGreaterThanOrEqualTo(TimeSpan.Zero)
      .Because("a CPU-time read failure mid-measurement must fall back to wall-clock elapsed, "
        + "not throw out of a property a caller expects to always succeed");
  }

  // The CPU-delta-derived active time is deliberately capped so it can never claim MORE active
  // time than the sampled CPU counter actually shows — including the case where the counter
  // reads BEHIND where it started (a real possibility across process-wide CPU accounting quirks).
  // Reporting bogus active time here would let a caller's timeout wait far longer than intended,
  // because "active" time would appear to move backwards or stall.
  [Test]
  public async Task ActiveElapsed_WhenCpuDeltaIsNegative_ReportsTheNegativeCpuDeltaAsync() {
    var callCount = 0;
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromDays(1), // never fires during this test
      CpuTimeSource = () => {
        var call = Interlocked.Increment(ref callCount);
        return call switch {
          1 => TimeSpan.Zero,                        // constructor's initial read
          2 => TimeSpan.Zero,                          // ActiveStopwatch's start-of-measurement read
          _ => TimeSpan.FromMilliseconds(-100)          // the ActiveElapsed read: behind the start
        };
      }
    };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    var active = stopwatch.ActiveElapsed;

    await Assert.That(active).IsEqualTo(TimeSpan.FromMilliseconds(-100))
      .Because("cpuElapsed (-100ms) is less than wallElapsed (>= 0), so the CPU-derived branch "
        + "must be the one reported here — the arithmetic is exact and does not depend on how "
        + "much real wall-clock time this property read took");
  }

  // ============================================================
  // SampleCpuTime — the disposed guard and the per-mode switch arms
  // ============================================================

  // Dispose stops the sampler timer, but a tick already dispatched can still land afterwards.
  // Without the guard that tick would keep reading CPU time and writing pause state into a
  // channel whose writer is already completed — work whose only possible effect is to keep a
  // disposed clock's state churning after the owner has let go of it.
  [Test]
  public async Task SampleCpuTime_AfterDispose_DoesNotReadTheCpuTimeSourceAsync() {
    var reads = 0;
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromDays(1), // never fires during this test
      CpuTimeSource = () => {
        Interlocked.Increment(ref reads);
        return TimeSpan.Zero;
      }
    };
    var clock = new DebuggerAwareClock(options);
    var readsAfterConstruction = Volatile.Read(ref reads);

    clock.Dispose();
    clock.SampleCpuTime(null);

    await Assert.That(readsAfterConstruction).IsEqualTo(1)
      .Because("the constructor takes the baseline sample, so the count below is measured against "
        + "a known starting point rather than against zero");
    await Assert.That(Volatile.Read(ref reads)).IsEqualTo(1)
      .Because("a tick that lands after Dispose has to return before it touches anything; reading "
        + "CPU time again is the first thing it would do if the guard were gone");
  }

  // The mode decides WHAT counts as frozen. A clock asked for debugger-attached detection must
  // not report paused off CPU accounting alone: on a busy host a process that is merely idle
  // looks exactly like one stopped at a breakpoint, and a false "paused" makes every
  // debugger-aware timeout in the system stop counting down while nothing is actually wrong.
  [Test]
  public async Task SampleCpuTime_DebuggerAttachedMode_RunsTheSampleAndReportsNotPausedAsync() {
    var reads = 0;
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.DebuggerAttached, // no sampler timer is created for this mode
      CpuTimeSource = () => {
        Interlocked.Increment(ref reads);
        return TimeSpan.Zero; // no CPU progress at all between samples
      }
    };
    using var clock = new DebuggerAwareClock(options);

    clock.SampleCpuTime(null);

    await Assert.That(Volatile.Read(ref reads)).IsEqualTo(2)
      .Because("the constructor's baseline plus this tick's read — proving the tick ran its body "
        + "rather than returning early");
    await Assert.That(clock.IsPaused).IsFalse()
      .Because("no debugger is attached to a test run, and this mode makes attachment the "
        + "precondition; reporting paused here would freeze every timeout that consults this clock");
  }

  // A mode value the switch does not know can only arrive from configuration binding or from a
  // later enum member nobody wired up here. The default arm is what makes that case "not paused"
  // rather than whatever the previous tick left behind — the conservative answer, since a clock
  // that reports paused keeps timeouts from ever expiring.
  [Test]
  public async Task SampleCpuTime_ModeOutsideTheEnum_RunsTheSampleAndReportsNotPausedAsync() {
    var reads = 0;
    var options = new DebuggerAwareClockOptions {
      Mode = (DebuggerDetectionMode)(-1),
      CpuTimeSource = () => {
        Interlocked.Increment(ref reads);
        return TimeSpan.Zero;
      }
    };
    using var clock = new DebuggerAwareClock(options);

    clock.SampleCpuTime(null);

    await Assert.That(Volatile.Read(ref reads)).IsEqualTo(2)
      .Because("the tick ran its body for an unrecognized mode instead of throwing on the switch");
    await Assert.That(clock.IsPaused).IsFalse()
      .Because("an unrecognized mode must fall through to not-paused; any other answer would let a "
        + "misconfigured clock suspend every timeout that consults it");
  }

  // ============================================================
  // The pause-state read loop's quiet exits
  // ============================================================

  // The loop runs on a task nobody awaits. If a faulted channel let the exception escape, the
  // task would sit unobserved until finalization and surface as a TaskScheduler unobserved
  // exception far from here — while the subscriber quietly stopped receiving pause changes.
  [Test]
  public async Task RunPauseStateReadLoop_ChannelFaulted_DeliversWhatArrivedThenEndsQuietlyAsync() {
    var channel = System.Threading.Channels.Channel.CreateUnbounded<bool>();
    var received = new List<bool>();
    var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    channel.Writer.TryWrite(true);
    // Completing with a ChannelClosedException is what makes the reader raise exactly that type.
    // Any other completion error is rethrown as itself and would land in a different catch.
    channel.Writer.TryComplete(
      new System.Threading.Channels.ChannelClosedException("channel closed by its owner"));

    var loop = DebuggerAwareClock.RunPauseStateReadLoopAsync(
      channel.Reader,
      isPaused => {
        received.Add(isPaused);
        delivered.TrySetResult();
      },
      CancellationToken.None);

    await delivered.Task;
    await loop;

    await Assert.That(loop.IsCompletedSuccessfully).IsTrue()
      .Because("nothing awaits this task in production, so a faulted channel has to end the loop "
        + "quietly rather than leave an unobserved exception behind");
    bool[] expectedStates = [true];
    await Assert.That(received).IsEquivalentTo(expectedStates)
      .Because("the state that arrived before the fault still has to reach the subscriber — the "
        + "quiet exit is about how the loop ends, not about dropping what it already read");
  }
}
