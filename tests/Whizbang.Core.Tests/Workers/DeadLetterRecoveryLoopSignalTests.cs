using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Pins the discriminator between a genuine dead-letter backlog and a recovery loop feeding itself.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/DeadLetterRecoveryLoopSignal.cs</code-under-test>
public class DeadLetterRecoveryLoopSignalTests {

  private static readonly DateTimeOffset _scanStart = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task Measure_AllRowsPredateTheLastScan_IsNotLoopingAsync() {
    // A real backlog: every row was already there before recovery last ran. Draining it is the
    // worker doing its job, however many cycles it takes.
    DateTimeOffset[] rows = [
      _scanStart.AddHours(-9), _scanStart.AddHours(-4), _scanStart.AddMinutes(-30),
    ];

    var m = DeadLetterRecoveryLoopSignal.Measure(rows, _scanStart, freshFraction: 0.5);

    await Assert.That(m.Fresh).IsEqualTo(0);
    await Assert.That(m.Considered).IsEqualTo(3);
    await Assert.That(m.IsSelfInflicted).IsFalse();
  }

  [Test]
  public async Task Measure_EveryRowAppearedAfterTheLastScan_IsLoopingAsync() {
    // The signature of the loop: this cycle's whole batch was dead-lettered after the previous
    // scan began, which means the previous scan's own recoveries produced them.
    DateTimeOffset[] rows = [
      _scanStart.AddSeconds(5), _scanStart.AddSeconds(30), _scanStart.AddMinutes(2),
    ];

    var m = DeadLetterRecoveryLoopSignal.Measure(rows, _scanStart, freshFraction: 0.5);

    await Assert.That(m.Fresh).IsEqualTo(3);
    await Assert.That(m.IsSelfInflicted).IsTrue();
  }

  [Test]
  public async Task Measure_FreshMinorityBelowThreshold_IsNotLoopingAsync() {
    // New dead letters arriving WHILE a real backlog drains is normal traffic, not a loop.
    DateTimeOffset[] rows = [
      _scanStart.AddHours(-3), _scanStart.AddHours(-2), _scanStart.AddHours(-1), _scanStart.AddSeconds(10),
    ];

    var m = DeadLetterRecoveryLoopSignal.Measure(rows, _scanStart, freshFraction: 0.5);

    await Assert.That(m.Fresh).IsEqualTo(1);
    await Assert.That(m.IsSelfInflicted).IsFalse();
  }

  [Test]
  public async Task Measure_ExactlyAtThreshold_IsLoopingAsync() {
    // The threshold is inclusive: at half the batch regenerating, recovery is already at best
    // breaking even, and breaking even forever is the failure this exists to stop.
    DateTimeOffset[] rows = [
      _scanStart.AddHours(-1), _scanStart.AddSeconds(10),
    ];

    var m = DeadLetterRecoveryLoopSignal.Measure(rows, _scanStart, freshFraction: 0.5);

    await Assert.That(m.IsSelfInflicted).IsTrue();
  }

  [Test]
  public async Task Measure_NoBaselineYet_IsNotLoopingAsync() {
    // First scan of the process has nothing to compare against. Treating "no baseline" as
    // evidence of a loop would trip the breaker on every cold start, which is when a real
    // backlog most needs draining.
    DateTimeOffset[] rows = [_scanStart.AddSeconds(5), _scanStart.AddSeconds(6)];

    var m = DeadLetterRecoveryLoopSignal.Measure(rows, previousScanStartedAt: null, freshFraction: 0.5);

    await Assert.That(m.IsSelfInflicted).IsFalse();
  }

  [Test]
  public async Task Measure_EmptyBatch_IsNotLoopingAsync() {
    // Nothing to recover is the healthy steady state, not a loop.
    var m = DeadLetterRecoveryLoopSignal.Measure([], _scanStart, freshFraction: 0.5);

    await Assert.That(m.Considered).IsEqualTo(0);
    await Assert.That(m.IsSelfInflicted).IsFalse();
  }

  [Test]
  public void Measure_NullRows_ThrowsAsync() {
    Assert.Throws<ArgumentNullException>(
      () => DeadLetterRecoveryLoopSignal.Measure(null!, _scanStart, 0.5));
  }
}
