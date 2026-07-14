using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Unit tests for <see cref="ScheduleTimer"/> using <see cref="FakeTimeProvider"/> (fully deterministic —
/// no real delays): the timer rings the doorbell exactly when time reaches the armed fire moment, not
/// before; <c>null</c> disarms; a later re-arm replaces an earlier one and an earlier re-arm replaces a
/// later one (arm-on-mutation); a past time wakes promptly.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public class ScheduleTimerTests {
  private static readonly DateTimeOffset _t0 = new(2026, 07, 13, 12, 00, 00, TimeSpan.Zero);

  private static (ScheduleTimer Timer, FakeTimeProvider Clock, Func<int> Fired) _create() {
    var clock = new FakeTimeProvider(_t0);
    var count = 0;
    var timer = new ScheduleTimer(clock, () => { _ = Interlocked.Increment(ref count); return ValueTask.CompletedTask; });
    return (timer, clock, () => Volatile.Read(ref count));
  }

  [Test]
  public async Task ArmFor_FiresWhenTimeReachesItAsync() {
    var (timer, clock, fired) = _create();
    timer.ArmFor(_t0.AddSeconds(10));

    clock.Advance(TimeSpan.FromSeconds(9));
    await Assert.That(fired()).IsEqualTo(0);   // not yet

    clock.Advance(TimeSpan.FromSeconds(1));     // now at +10s
    await Assert.That(fired()).IsEqualTo(1);
    await Assert.That(timer.WakeCount).IsEqualTo(1L);
    await Assert.That(timer.ArmedFor).IsNull();   // consumed
    timer.Dispose();
  }

  [Test]
  public async Task ArmFor_Null_DisarmsAsync() {
    var (timer, clock, fired) = _create();
    timer.ArmFor(_t0.AddSeconds(10));
    timer.ArmFor(null);

    clock.Advance(TimeSpan.FromSeconds(30));
    await Assert.That(fired()).IsEqualTo(0);
    await Assert.That(timer.ArmedFor).IsNull();
    timer.Dispose();
  }

  [Test]
  public async Task ArmFor_LaterTime_ReplacesEarlierAsync() {
    // Steady state: after a drain the new minimum can be LATER (the earliest schedule fired + advanced).
    var (timer, clock, fired) = _create();
    timer.ArmFor(_t0.AddSeconds(5));
    timer.ArmFor(_t0.AddSeconds(20));   // replace with a later time

    clock.Advance(TimeSpan.FromSeconds(10));   // past the OLD arm, before the new one
    await Assert.That(fired()).IsEqualTo(0);   // old arm was replaced, so no fire

    clock.Advance(TimeSpan.FromSeconds(10));   // now at +20s
    await Assert.That(fired()).IsEqualTo(1);
    timer.Dispose();
  }

  [Test]
  public async Task ArmFor_EarlierTime_ReplacesLaterAsync() {
    // Arm-on-mutation: a freshly-created near-term schedule must pull the wake earlier.
    var (timer, clock, fired) = _create();
    timer.ArmFor(_t0.AddSeconds(20));
    timer.ArmFor(_t0.AddSeconds(5));   // new near-term schedule

    clock.Advance(TimeSpan.FromSeconds(5));
    await Assert.That(fired()).IsEqualTo(1);
    timer.Dispose();
  }

  [Test]
  public async Task ArmFor_PastTime_FiresPromptlyAsync() {
    var (timer, clock, fired) = _create();
    timer.ArmFor(_t0.AddSeconds(-5));   // already past

    clock.Advance(TimeSpan.FromMilliseconds(1));
    await Assert.That(fired()).IsEqualTo(1);
    timer.Dispose();
  }

  [Test]
  public async Task ReArmAfterFire_FiresAgainAsync() {
    var (timer, clock, fired) = _create();
    timer.ArmFor(_t0.AddSeconds(10));
    clock.Advance(TimeSpan.FromSeconds(10));
    await Assert.That(fired()).IsEqualTo(1);

    // Worker re-arms for the next occurrence after draining.
    timer.ArmFor(clock.GetUtcNow().AddSeconds(10));
    clock.Advance(TimeSpan.FromSeconds(10));
    await Assert.That(fired()).IsEqualTo(2);
    await Assert.That(timer.WakeCount).IsEqualTo(2L);
    timer.Dispose();
  }

  [Test]
  public async Task Disposed_ArmIsNoOpAsync() {
    var (timer, clock, fired) = _create();
    timer.Dispose();
    timer.ArmFor(_t0.AddSeconds(1));

    clock.Advance(TimeSpan.FromSeconds(5));
    await Assert.That(fired()).IsEqualTo(0);
  }
}
