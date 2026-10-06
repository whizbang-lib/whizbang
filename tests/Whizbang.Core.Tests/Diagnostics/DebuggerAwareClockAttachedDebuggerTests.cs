// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// The attached-debugger modes and the active-time cap, driven through the clock's seams: whether a
/// debugger is attached, the CPU clock, and the wall clock the sampler measures its interval on are
/// all the test's to give, so each decision is a fact rather than a property of the test runner.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Diagnostics/DebuggerAwareClock.cs</code-under-test>
[Category("Core")]
[Category("Diagnostics")]
public class DebuggerAwareClockAttachedDebuggerTests {
  // Long enough that the sampling timer Auto and CpuTimeSampling start never ticks during a test.
  private static readonly TimeSpan _quietSampling = TimeSpan.FromHours(1);

  // Comfortably past the sampler's 200 ms minimum interval.
  private static readonly TimeSpan _oneSampleInterval = TimeSpan.FromSeconds(1);

  [Test]
  [Arguments(DebuggerDetectionMode.DebuggerAttached)]
  [Arguments(DebuggerDetectionMode.Auto)]
  public async Task SampleCpuTime_DebuggerAttachedAndCpuFrozen_ReportsPausedAsync(DebuggerDetectionMode mode) {
    var time = new FakeTimeProvider();
    using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
      Mode = mode,
      SamplingInterval = _quietSampling,
      CpuTimeSource = () => TimeSpan.FromSeconds(1),
      DebuggerAttachedSource = () => true,
      WallClockSource = time.GetUtcNow,
    });

    time.Advance(_oneSampleInterval);
    clock.SampleCpuTime(null);

    await Assert.That(clock.IsPaused).IsTrue()
      .Because("with a debugger attached, a second of wall time without CPU progress is a breakpoint");
  }

  [Test]
  [Arguments(DebuggerDetectionMode.DebuggerAttached)]
  [Arguments(DebuggerDetectionMode.Auto)]
  public async Task SampleCpuTime_NoDebuggerAttached_NeverReportsPausedAsync(DebuggerDetectionMode mode) {
    var time = new FakeTimeProvider();
    using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
      Mode = mode,
      SamplingInterval = _quietSampling,
      CpuTimeSource = () => TimeSpan.FromSeconds(1),
      DebuggerAttachedSource = () => false,
      WallClockSource = time.GetUtcNow,
    });

    time.Advance(_oneSampleInterval);
    clock.SampleCpuTime(null);

    await Assert.That(clock.IsPaused).IsFalse()
      .Because("these modes attribute a frozen CPU to a debugger only when one is attached");
  }

  [Test]
  public async Task ActiveElapsed_PausedButCpuRanAhead_IsCappedAtWallTimeAsync() {
    var time = new FakeTimeProvider();
    var cpu = TimeSpan.FromSeconds(1);
    using var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = _quietSampling,
      CpuTimeSource = () => cpu,
      WallClockSource = time.GetUtcNow,
    });
    time.Advance(_oneSampleInterval);
    clock.SampleCpuTime(null);
    await Assert.That(clock.IsPaused).IsTrue()
      .Because("the setup must put the clock in the paused state");

    var stopwatch = clock.StartNew();
    // An hour of CPU across many threads, in a stopwatch a test runs for milliseconds.
    cpu += TimeSpan.FromHours(1);
    var active = stopwatch.ActiveElapsed;
    var wall = stopwatch.WallElapsed;

    await Assert.That(active).IsLessThan(TimeSpan.FromHours(1))
      .Because("CPU time that outruns the wall clock is capped at the wall clock");
    await Assert.That(active).IsLessThanOrEqualTo(wall);
  }
}
