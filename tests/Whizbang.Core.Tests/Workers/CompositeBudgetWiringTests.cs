using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The consumer-side expansion budget as the dispatch worker applies it.
/// </summary>
/// <remarks>
/// <para>
/// A composite's own <c>MaxInnerEventsAllowed</c> is declared by the MESSAGE, so one carrying a
/// hundred thousand inner events simply declares a cap that large and passes. Expansion then happens
/// INSIDE the consumer, after admission control already accepted it, so a single accepted message
/// can add six figures of inbox rows and invalidate every downstream bound at once.
/// </para>
/// <para>
/// Reporting is unconditional once over budget; refusal is opt-in. That asymmetry is the whole
/// design: the absence of any signal at expansion is what made an inbox growing by tens of
/// thousands of rows per minute impossible to attribute, while refusing an expansion changes
/// delivery for workloads that function today.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDispatchWorker.cs</code-under-test>
[Category("Workers")]
public class CompositeBudgetWiringTests {

  [Test]
  public async Task AnOrdinaryExpansionIsNeitherReportedNorRefusedAsync() {
    var v = InboxDispatchWorker.EvaluateExpansionBudgetForTest(childCount: 40, budget: 5000, enforce: true);

    await Assert.That(v.OverBudget).IsFalse();
    await Assert.That(v.Refuse).IsFalse()
      .Because("normal traffic must be untouched — a budget that reshapes ordinary expansions costs "
             + "more than the pathology it prevents");
  }

  [Test]
  public async Task ExactlyAtBudgetIsWithinItAsync() {
    var v = InboxDispatchWorker.EvaluateExpansionBudgetForTest(childCount: 5000, budget: 5000, enforce: true);

    await Assert.That(v.OverBudget).IsFalse()
      .Because("an off-by-one here would refuse every composite sized precisely to the documented "
             + "limit, which is the one size operators will deliberately choose");
  }

  [Test]
  public async Task AnOversizedExpansionIsReportedButNotRefusedByDefaultAsync() {
    var v = InboxDispatchWorker.EvaluateExpansionBudgetForTest(childCount: 200_000, budget: 5000, enforce: false);

    await Assert.That(v.OverBudget).IsTrue();
    await Assert.That(v.Refuse).IsFalse()
      .Because("report-only is the default: a package upgrade must not start dead-lettering "
             + "composites that were being delivered yesterday");
    await Assert.That(v.Chunks).IsEqualTo(40)
      .Because("the step count tells an operator how far past the budget this went, which is the "
             + "number that makes the report actionable");
  }

  [Test]
  public async Task EnforcementRefusesTheOversizedExpansionAsync() {
    var v = InboxDispatchWorker.EvaluateExpansionBudgetForTest(childCount: 67_971, budget: 5000, enforce: true);

    await Assert.That(v.OverBudget).IsTrue();
    await Assert.That(v.Refuse).IsTrue()
      .Because("with enforcement on, one message must not be able to add tens of thousands of rows "
             + "to a consumer that never agreed to absorb them");
  }

  [Test]
  public async Task ZeroBudgetDisablesTheCheckEntirelyAsync() {
    var v = InboxDispatchWorker.EvaluateExpansionBudgetForTest(childCount: 200_000, budget: 0, enforce: true);

    await Assert.That(v.OverBudget).IsFalse()
      .Because("zero is the documented off switch; honouring enforcement while the budget is "
             + "disabled would refuse expansions against a limit nobody set");
    await Assert.That(v.Refuse).IsFalse();
  }

  [Test]
  public async Task ANegativeBudgetIsTreatedAsDisabledNotAsZeroAsync() {
    var v = InboxDispatchWorker.EvaluateExpansionBudgetForTest(childCount: 10, budget: -5, enforce: true);

    await Assert.That(v.OverBudget).IsFalse()
      .Because("a negative budget is misconfiguration; refusing every expansion because of it would "
             + "take delivery down over a typo");
  }
}
