using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A bounded population of failing rows must not consume the whole claim working set.
/// </summary>
/// <remarks>
/// <para>
/// Rows that fail are re-claimed when their lease lapses. Because they are re-claimed continuously
/// they always occupy the working set, so the claim never reaches rows behind them. The dead-letter
/// path does retire them, but slowly enough that fresh failures replace them at about the
/// retirement rate — so the set never frees up and healthy work is never claimed.
/// </para>
/// <para>
/// Measured side by side on identical framework and configuration: a consumer whose working set had
/// been retried into the teens held ~10,000 leases and drained ~29 rows/min, with ~199,000 rows
/// (95% of its inbox) never claimed at all. A comparison consumer whose rows were all at attempt 1
/// drained the same shape of backlog at ~8,000 rows/min. A ~275x difference, from the attempt
/// distribution alone.
/// </para>
/// <para>
/// The presentation is the worst part: modest CPU, no errors beyond dead-letter warnings, no
/// restarts. It looks like an idle service rather than a stalled one.
/// </para>
/// <para>
/// Widening the claim does not help — verified live, where raising the claim floor tenfold on a
/// stalled consumer changed its drain rate by less than noise. The constraint is WHAT occupies the
/// set, not how large the set is.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/PoisonAdmissionPolicy.cs</code-under-test>
[Category("Workers")]
public class PoisonAdmissionPolicyTests {

  [Test]
  public async Task FirstDeliveryRowsAreAlwaysAdmittedAsync() {
    var policy = new PoisonAdmissionPolicy(new PoisonAdmissionPolicy.Settings { MaxAttempts = 10 });

    var decision = policy.Evaluate(attempts: 1, workingSetSize: 100, highAttemptShare: 0.0);

    await Assert.That(decision.Admit).IsTrue()
      .Because("healthy work must never be gated — this policy exists to protect it, not to ration it");
  }

  [Test]
  public async Task HighAttemptRowsYieldOnceTheyDominateTheSetAsync() {
    var policy = new PoisonAdmissionPolicy(new PoisonAdmissionPolicy.Settings {
      MaxAttempts = 10,
      HighAttemptThreshold = 5,
      MaxHighAttemptShare = 0.5,
    });

    // The measured pathology: the working set is overwhelmingly retried rows. This row is still
    // within its ceiling, so the ONLY thing that can withhold it is set saturation.
    var decision = policy.Evaluate(attempts: 8, workingSetSize: 10_000, highAttemptShare: 0.95);

    await Assert.That(decision.Admit).IsFalse()
      .Because("when nearly the whole set is doomed rows, admitting another one keeps healthy work "
             + "unreachable — this is the exact state where 95% of an inbox went never-claimed");
    await Assert.That(decision.Reason).IsEqualTo(PoisonAdmissionPolicy.Verdict.SetSaturatedByRetries);
  }

  [Test]
  public async Task HighAttemptRowsAreStillAdmittedWhenThereIsRoomAsync() {
    var policy = new PoisonAdmissionPolicy(new PoisonAdmissionPolicy.Settings {
      MaxAttempts = 10,
      HighAttemptThreshold = 5,
      MaxHighAttemptShare = 0.5,
    });

    var decision = policy.Evaluate(attempts: 7, workingSetSize: 10_000, highAttemptShare: 0.10);

    await Assert.That(decision.Admit).IsTrue()
      .Because("retried rows are not banned — starving them would stop them ever reaching their "
             + "attempt ceiling, so they would never retire and the condition would become permanent");
  }

  [Test]
  public async Task RowsPastTheCeilingAreRetiredNotReadmittedAsync() {
    var policy = new PoisonAdmissionPolicy(new PoisonAdmissionPolicy.Settings { MaxAttempts = 10 });

    var decision = policy.Evaluate(attempts: 11, workingSetSize: 100, highAttemptShare: 0.0);

    await Assert.That(decision.Admit).IsFalse();
    await Assert.That(decision.Reason).IsEqualTo(PoisonAdmissionPolicy.Verdict.PastAttemptCeiling)
      .Because("rows were observed at attempts 17 and 21 against a max of 10 — overshooting the "
             + "ceiling keeps doomed work in the set long after it should have been retired");
  }

  [Test]
  public async Task AnEmptyWorkingSetAdmitsAnythingAsync() {
    var policy = new PoisonAdmissionPolicy(new PoisonAdmissionPolicy.Settings {
      MaxAttempts = 10,
      HighAttemptThreshold = 5,
      MaxHighAttemptShare = 0.5,
    });

    var decision = policy.Evaluate(attempts: 9, workingSetSize: 0, highAttemptShare: 1.0);

    await Assert.That(decision.Admit).IsTrue()
      .Because("with nothing in flight there is nothing to starve, and refusing here would deadlock "
             + "a consumer whose entire remaining backlog is retried rows");
  }

  [Test]
  public async Task TheShareIsReportedSoTheConditionIsVisibleAsync() {
    var policy = new PoisonAdmissionPolicy(new PoisonAdmissionPolicy.Settings {
      MaxAttempts = 10,
      HighAttemptThreshold = 5,
      MaxHighAttemptShare = 0.5,
    });

    var decision = policy.Evaluate(attempts: 9, workingSetSize: 10_000, highAttemptShare: 0.95);

    await Assert.That(decision.ObservedHighAttemptShare).IsEqualTo(0.95);
    await Assert.That(decision.Admit).IsFalse()
      .Because("'the working set is dominated by retried rows while unclaimed work exists' is a "
             + "precise, cheap condition that nothing reports today — an operator sees only low CPU "
             + "and a large backlog");
  }

  [Test]
  public async Task RejectsANonsensicalShareAsync() {
    var policy = new PoisonAdmissionPolicy(new PoisonAdmissionPolicy.Settings());
    await Assert.That(() => policy.Evaluate(attempts: 1, workingSetSize: 10, highAttemptShare: 1.5))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a share above one means the caller computed it wrong, and silently clamping would "
             + "hide a miscount in the very signal the gate depends on");
  }
}
