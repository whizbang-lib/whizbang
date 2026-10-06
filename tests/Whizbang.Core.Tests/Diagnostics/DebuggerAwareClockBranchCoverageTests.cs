// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Branch coverage for the active stopwatch's <c>FrozenTime</c> clamp: frozen time is wall time
/// minus active time, reported as is when positive and clamped to zero otherwise. A scripted CPU
/// source makes both sides a fact rather than a race: a CPU clock that never advances means every
/// wall tick was frozen, and a mode that measures active time by the wall clock can never report
/// more wall time than active time.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Diagnostics/DebuggerAwareClock.cs</code-under-test>
[Category("Core")]
[Category("Diagnostics")]
public class DebuggerAwareClockBranchCoverageTests {

  // Long enough that the constructor's sampling timer never ticks during a test.
  private static readonly TimeSpan _quietSampling = TimeSpan.FromHours(1);

  [Test]
  public async Task FrozenTime_CpuClockNeverAdvances_ReportsTheWholeWallTimeAsFrozenAsync() {
    using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = _quietSampling,
      CpuTimeSource = () => TimeSpan.FromSeconds(5)
    });
    var stopwatch = clock.StartNew();

    stopwatch.Halt();

    await Assert.That(stopwatch.ActiveElapsed).IsEqualTo(TimeSpan.Zero)
      .Because("the CPU clock did not move, so no time was spent running");
    await Assert.That(stopwatch.FrozenTime).IsEqualTo(stopwatch.WallElapsed)
      .Because("every wall tick with no CPU progress is frozen time, reported unclamped");
  }

  [Test]
  public async Task FrozenTime_ActiveTimeMeasuredByTheWallClock_IsClampedToZeroAsync() {
    // Each read of this CPU clock jumps an hour, far more than the wall clock can advance in a
    // test, so the stopwatch is never judged frozen and measures active time by the wall clock.
    // FrozenTime reads the wall clock first and the active clock second, so active can only be
    // equal or later: the difference is zero or negative, and a negative frozen time must never
    // be reported.
    var cpuReads = 0;
    using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = _quietSampling,
      CpuTimeSource = () => TimeSpan.FromHours(Interlocked.Increment(ref cpuReads))
    });
    var stopwatch = clock.StartNew();

    var frozen = stopwatch.FrozenTime;

    await Assert.That(frozen).IsEqualTo(TimeSpan.Zero)
      .Because("time measured by the wall clock is all active, so none of it is frozen, and the clamp keeps "
        + "the read-order skew from surfacing as negative frozen time");
  }
}
