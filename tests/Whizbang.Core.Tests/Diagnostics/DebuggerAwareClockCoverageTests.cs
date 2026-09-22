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
/// Three of the nine target lines are left uncovered deliberately — they are unreachable given
/// the current wiring, not merely untested:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Line 132 (the <c>DebuggerDetectionMode.DebuggerAttached</c> arm inside <c>_sampleCpuTime</c>'s
/// switch) and line 137 (that switch's <c>_ =&gt; false</c> default arm) can only execute while
/// <c>_sampleCpuTime</c> is running. Its only caller is the private <c>_sampler</c> timer, which
/// the constructor creates only <c>if (_shouldUseCpuSampling())</c> — and that method returns
/// <c>true</c> only for <c>Mode == CpuTimeSampling</c> or <c>Mode == Auto</c>. So whenever
/// <c>_sampleCpuTime</c> actually runs, <c>_options.Mode</c> can only be <c>CpuTimeSampling</c> or
/// <c>Auto</c> — the two arms <c>_sampleCpuTime</c>'s own switch already handles explicitly. The
/// <c>DebuggerAttached</c> arm and the default arm can never be reached; <c>Mode</c> is fixed for
/// the clock's lifetime (no setter), so this isn't a timing race, it's structural.
/// </description></item>
/// <item><description>
/// Line 326 (the closing brace of <c>PauseStateSubscription._readLoopAsync</c>'s
/// <c>catch (ChannelClosedException)</c>) requires <c>reader.ReadAllAsync(ct)</c> to throw
/// <see cref="System.Threading.Channels.ChannelClosedException"/>. <c>ReadAllAsync</c> is
/// implemented over <c>WaitToReadAsync</c>/<c>TryRead</c>, and the only place the clock ever
/// completes the channel is <c>Dispose()</c>'s parameterless <c>Writer.TryComplete()</c> — a
/// graceful completion that makes <c>WaitToReadAsync</c> return <c>false</c> and the enumeration
/// end normally, never throw. A <c>ChannelClosedException</c> would require completing the
/// channel WITH an exception (<c>TryComplete(Exception)</c>), which nothing in this class does.
/// This catch clause is defensive code for a completion shape the class never produces.
/// </description></item>
/// </list>
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
}
