using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Auto-repair must not mistake a BACKLOG for a data loss, and must stop asking when asking is not
/// helping.
/// </summary>
/// <remarks>
/// <para>
/// Checkpoint gap detection compares an origin's per-window event count against what a consumer has
/// received, and confirms a deficit that survives two consecutive checkpoints. That confirmation
/// window is the checkpoint cadence — 60 seconds by default. A consumer running minutes or hours
/// behind therefore reports a deficit on both checks and CONFIRMS a gap while nothing whatsoever
/// has been lost.
/// </para>
/// <para>
/// The scheduled deep audit already guards against precisely this: it folds only events older than
/// a settle window, documented as "an in-flight delivery must never read as divergence." The
/// checkpoint path has no equivalent. That asymmetry is the defect.
/// </para>
/// <para>
/// The consequence is not a wasted request, it is a runaway. Repair re-delivers the window; the
/// re-delivered events land at the back of the same backlog and cannot arrive within the next
/// cadence either; the deficit re-confirms; repair fires again. Each cycle ADDS load, which
/// increases lag, which manufactures more false gaps. Observed in production as a consumer emitting
/// roughly thirty times the events its producer did, from a workload that had previously run in
/// minutes.
/// </para>
/// <para>
/// The per-checkpoint cap does not bound this. It limits one checkpoint's requests; checkpoints keep
/// arriving on cadence, pendings are re-added from the current window as fast as they are drained,
/// and nothing remembers that a given window was already requested or whether the last request
/// helped.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityRepairPolicy.cs</code-under-test>
[Category("Messaging")]
public class IntegrityRepairSpinTests {

  private static IntegrityRepairPolicy.GapObservation _deficit(
      long from = 100, long to = 200, int expected = 500, int actual = 40,
      int backlogDepth = 0, TimeSpan? consumerLag = null, int activeLeases = 0)
    => new(
      OriginServiceId: Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
      EventType: "Contracts+ThingHappenedEvent, Contracts",
      TenantScope: null,
      FromCommitSequence: from,
      ToCommitSequence: to,
      ExpectedCount: expected,
      ActualCount: actual,
      ServiceBacklogDepth: backlogDepth,
      ConsumerLag: consumerLag ?? TimeSpan.Zero,
      ActiveLeaseCount: activeLeases);

  // ---- Settledness is a SERVICE property, never an instance one. ----

  [Test]
  public async Task AnIdleInstanceMustNotRepairWhileASiblingIsWorkingAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings());

    // This instance has finished its own claimed streams — locally it looks completely idle. But
    // peers still hold live leases on the shared inbox, so the service is mid-drain.
    var decision = policy.Evaluate(_deficit(backlogDepth: 0, consumerLag: TimeSpan.Zero, activeLeases: 4_100));

    await Assert.That(decision.ShouldRequestRepair).IsFalse()
      .Because("a service runs many instances, and one finishing its slice says nothing about the "
             + "others — an instance that repairs off its LOCAL view re-requests events its own "
             + "peers are actively processing, which is the storm reappearing from the one replica "
             + "that happened to be free");
    await Assert.That(decision.Reason).IsEqualTo(IntegrityRepairPolicy.Verdict.ConsumerBehind);
  }

  [Test]
  public async Task RepairProceedsOnlyWhenNoInstanceHoldsWorkAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings());

    // Nothing queued anywhere, no live lease held by any instance, no lag: the whole service is at
    // rest, so a residual deficit really is missing data.
    var decision = policy.Evaluate(_deficit(backlogDepth: 0, consumerLag: TimeSpan.Zero, activeLeases: 0));

    await Assert.That(decision.ShouldRequestRepair).IsTrue()
      .Because("requiring quiet across every instance is the point — but it must still resolve to "
             + "'repair' once the service genuinely settles, or self-healing never runs on a busy "
             + "deployment");
  }

  // ---- Q3: what is actually failing? Often nothing — the consumer is merely behind. ----

  [Test]
  public async Task ABacklogIsNotAGapAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings());

    // 460 events "missing" — and 120,000 rows queued that have not been processed yet.
    var decision = policy.Evaluate(_deficit(backlogDepth: 120_000, consumerLag: TimeSpan.FromMinutes(14)));

    await Assert.That(decision.ShouldRequestRepair).IsFalse()
      .Because("the events are not lost, they are QUEUED — re-requesting them appends duplicates to "
             + "the very backlog that produced the apparent deficit, which is how a lagging "
             + "consumer turns into a self-sustaining message storm");
    await Assert.That(decision.Reason).IsEqualTo(IntegrityRepairPolicy.Verdict.ConsumerBehind);
  }

  [Test]
  public async Task ADrainedConsumerWithARealDeficitStillRepairsAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings());

    // Nothing queued, nothing in flight, and the count is still short: genuinely missing.
    var decision = policy.Evaluate(_deficit(backlogDepth: 0, consumerLag: TimeSpan.Zero));

    await Assert.That(decision.ShouldRequestRepair).IsTrue()
      .Because("suppressing repair whenever a deficit appears would disable self-healing entirely — "
             + "the fix must distinguish 'behind' from 'lost', not stop repairing");
  }

  [Test]
  public async Task LagAloneIsEnoughToWithholdEvenWithAnEmptyQueueAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings());

    // Queue momentarily drained, but this consumer is known to be far behind the origin's clock.
    var decision = policy.Evaluate(_deficit(backlogDepth: 0, consumerLag: TimeSpan.FromMinutes(30)));

    await Assert.That(decision.ShouldRequestRepair).IsFalse()
      .Because("depth is a snapshot and can read zero between claim cycles; lag is the durable "
             + "signal that in-flight work has not landed yet");
  }

  // ---- Q1/Q2: repeated failure must be detected, not repeated forever. ----

  [Test]
  public async Task RepeatedIneffectiveRepairBacksOffAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings { MaxAttemptsPerWindow = 3 });
    var gap = _deficit(backlogDepth: 0);

    var granted = 0;
    for (var i = 0; i < 20; i++) {
      var d = policy.Evaluate(gap);
      if (d.ShouldRequestRepair) { granted++; policy.RecordRequested(gap); }
    }

    await Assert.That(granted).IsLessThanOrEqualTo(3)
      .Because("the SAME window failing to heal after repeated requests means re-requesting is not "
             + "the remedy; continuing is how a bounded per-checkpoint cap becomes an unbounded "
             + "aggregate rate");
    await Assert.That(granted).IsGreaterThanOrEqualTo(1)
      .Because("a first attempt must still happen — genuine single-message loss heals on one retry");
  }

  [Test]
  public async Task ASuppressedWindowReportsWhyAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings { MaxAttemptsPerWindow = 2 });
    var gap = _deficit(backlogDepth: 0);

    for (var i = 0; i < 5; i++) {
      var d = policy.Evaluate(gap);
      if (d.ShouldRequestRepair) { policy.RecordRequested(gap); }
    }
    var final = policy.Evaluate(gap);

    await Assert.That(final.ShouldRequestRepair).IsFalse();
    await Assert.That(final.Reason).IsEqualTo(IntegrityRepairPolicy.Verdict.AttemptsExhausted)
      .Because("an operator needs to see that repair GAVE UP on a window rather than silently "
             + "stopping — a gap that outlives its repair budget is a real incident");
  }

  [Test]
  public async Task ProgressResetsTheAttemptBudgetAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings { MaxAttemptsPerWindow = 2 });
    var gap = _deficit(actual: 40, backlogDepth: 0);

    policy.Evaluate(gap); policy.RecordRequested(gap);
    policy.Evaluate(gap); policy.RecordRequested(gap);
    await Assert.That(policy.Evaluate(gap).ShouldRequestRepair).IsFalse();

    // The window is healing — more events arrived than last time.
    var healing = _deficit(actual: 300, backlogDepth: 0);
    var decision = policy.Evaluate(healing);

    await Assert.That(decision.ShouldRequestRepair).IsTrue()
      .Because("repair that is demonstrably working must not be cut off by a budget meant to stop "
             + "repair that is not");
  }

  // ---- Q2: the aggregate rate, not just the per-checkpoint one. ----

  [Test]
  public async Task DistinctWindowsShareOneGlobalBudgetAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings {
      MaxAttemptsPerWindow = 5,
      MaxConcurrentWindowsUnderRepair = 4,
    });

    var granted = 0;
    for (var w = 0; w < 50; w++) {
      var gap = _deficit(from: w * 100, to: (w * 100) + 99, backlogDepth: 0);
      var d = policy.Evaluate(gap);
      if (d.ShouldRequestRepair) { granted++; policy.RecordRequested(gap); }
    }

    await Assert.That(granted).IsLessThanOrEqualTo(4)
      .Because("a per-checkpoint cap bounds ONE checkpoint; with checkpoints arriving on a cadence "
             + "and pendings re-added as fast as they drain, only a global bound limits the rate "
             + "at which repair adds load to an already-struggling consumer");
  }

  [Test]
  public async Task TheGlobalBudgetFreesUpWhenAWindowHealsAsync() {
    var settings = new IntegrityRepairPolicy.Settings { MaxConcurrentWindowsUnderRepair = 1 };
    var policy = new IntegrityRepairPolicy(settings);

    var first = _deficit(from: 0, to: 99, backlogDepth: 0);
    var second = _deficit(from: 100, to: 199, backlogDepth: 0);

    policy.Evaluate(first); policy.RecordRequested(first);
    await Assert.That(policy.Evaluate(second).ShouldRequestRepair).IsFalse()
      .Because("the budget is occupied");

    policy.RecordHealed(first);

    await Assert.That(policy.Evaluate(second).ShouldRequestRepair).IsTrue()
      .Because("a permanent block would be worse than the storm — once a window heals its slot must "
             + "return, or the first gaps seen after startup would monopolize repair forever");
  }

  [Test]
  public async Task NullObservationIsRejectedRatherThanTreatedAsHealthyAsync() {
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings());

    await Assert.That(() => policy.Evaluate(null!)).Throws<ArgumentNullException>()
      .Because("a silently-ignored malformed observation reads as 'no gap', which is the one wrong "
             + "answer that produces no evidence it was ever asked");
  }
}
