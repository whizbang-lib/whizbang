// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Branch coverage for <see cref="ScheduleTimer"/>'s logger fallback: a timer constructed with no
/// logger must still absorb a faulting doorbell (falling back to a null logger) and keep working, so
/// a host that wires the timer by hand without logging cannot have one bad wake end all future wakes.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Temporal/ScheduleTimer.cs</code-under-test>
public class ScheduleTimerBranchCoverageTests {
  private static readonly DateTimeOffset _t0 = new(2026, 07, 13, 12, 00, 00, TimeSpan.Zero);

  [Test]
  public async Task Constructor_NullLogger_FaultingWakeIsAbsorbedAndTimerKeepsWakingAsync() {
    var clock = new FakeTimeProvider(_t0);
    var calls = 0;
    using var timer = new ScheduleTimer(clock, () => {
      calls++;
      throw new InvalidOperationException("onDue exploded");
    }, logger: null!);

    timer.ArmFor(_t0.AddSeconds(1));
    // FakeTimeProvider runs due callbacks synchronously inside Advance; onDue throws before any
    // await, so the fault is handled (or escapes) before Advance returns.
    clock.Advance(TimeSpan.FromSeconds(1));

    timer.ArmFor(clock.GetUtcNow().AddSeconds(1));
    clock.Advance(TimeSpan.FromSeconds(1));

    await Assert.That(calls).IsEqualTo(2)
      .Because("a faulted wake with no logger configured must not stop the next armed wake from ringing");
    await Assert.That(timer.WakeCount).IsEqualTo(2L);
    await Assert.That(timer.ArmedFor).IsNull()
      .Because("each wake consumes its arming even when the doorbell faults");
  }
}
