using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Expanding one composite must not be able to bury a consumer.
/// </summary>
/// <remarks>
/// <para>
/// Composite fan-out expands a received composite into child inbox rows. The expansion happens
/// INSIDE the consumer, after admission control has already accepted the message, so a single
/// accepted message can add an unbounded number of rows to the inbox.
/// </para>
/// <para>
/// Observed in production: single composites expanding into tens of thousands of child rows, the
/// largest past 200,000. A consumer's inbox climbed for hours with an empty broker and no upstream
/// producer — the growth was entirely local expansion of work it already held. Producers were
/// emitting composites of at most 16 rows, so the inflation happened in transit rather than at
/// emission.
/// </para>
/// <para>
/// This defeats every downstream bound. Claim windows, outstanding-row budgets and lease sizing are
/// all calibrated in inbox rows, and one admitted message can add six figures of them.
/// </para>
/// <para>
/// The budget below is deliberately about CONSUMER CAPACITY rather than message validity. A
/// composite can be perfectly well-formed and still be more than this consumer should absorb in one
/// step.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeExpansionBudget.cs</code-under-test>
[Category("Messaging")]
public class CompositeExpansionBudgetTests {

  [Test]
  public async Task AnOrdinaryCompositeExpandsWholeAsync() {
    var budget = new CompositeExpansionBudget(maxChildrenPerExpansion: 500);

    var plan = budget.Plan(innerEventCount: 40);

    await Assert.That(plan.Chunks).IsEqualTo(1);
    await Assert.That(plan.ChunkSize).IsEqualTo(40);
    await Assert.That(plan.ExceedsBudget).IsFalse()
      .Because("normal composites must be untouched — a bound that reshapes ordinary traffic costs "
             + "more than the pathology it prevents");
  }

  [Test]
  public async Task AnOversizedCompositeIsChunkedNotDroppedAsync() {
    var budget = new CompositeExpansionBudget(maxChildrenPerExpansion: 500);

    var plan = budget.Plan(innerEventCount: 200_000);

    await Assert.That(plan.ExceedsBudget).IsTrue();
    await Assert.That(plan.Chunks).IsEqualTo(400);
    await Assert.That(plan.ChunkSize).IsEqualTo(500)
      .Because("the events are real work and must still be delivered — rejecting the composite "
             + "would turn an absorption problem into data loss");
  }

  [Test]
  public async Task ChunkingCoversEveryInnerEventExactlyOnceAsync() {
    var budget = new CompositeExpansionBudget(maxChildrenPerExpansion: 300);

    var plan = budget.Plan(innerEventCount: 1_001);

    await Assert.That(plan.Chunks).IsEqualTo(4)
      .Because("1001 over 300 needs four chunks — a floor division would silently drop the last row");
    await Assert.That(plan.Chunks * plan.ChunkSize).IsGreaterThanOrEqualTo(1_001);
    await Assert.That((plan.Chunks - 1) * plan.ChunkSize).IsLessThan(1_001)
      .Because("no chunk may be entirely empty; that would publish a batch carrying nothing");
  }

  [Test]
  public async Task ABoundaryCompositeIsNotChunkedAsync() {
    var budget = new CompositeExpansionBudget(maxChildrenPerExpansion: 500);

    var plan = budget.Plan(innerEventCount: 500);

    await Assert.That(plan.Chunks).IsEqualTo(1);
    await Assert.That(plan.ExceedsBudget).IsFalse()
      .Because("exactly at the budget is within it — an off-by-one here doubles the round trips for "
             + "every composite sized to the documented limit");
  }

  [Test]
  public async Task AnEmptyCompositeYieldsNoWorkAsync() {
    var budget = new CompositeExpansionBudget(maxChildrenPerExpansion: 500);

    var plan = budget.Plan(innerEventCount: 0);

    await Assert.That(plan.Chunks).IsEqualTo(0)
      .Because("expanding nothing must produce nothing, not one empty chunk that costs a write and "
             + "a publish for zero events");
    await Assert.That(plan.ExceedsBudget).IsFalse();
  }

  [Test]
  public async Task TheBudgetIsReportedForTheOperatorAsync() {
    var budget = new CompositeExpansionBudget(maxChildrenPerExpansion: 500);

    var plan = budget.Plan(innerEventCount: 67_971);

    await Assert.That(plan.InnerEventCount).IsEqualTo(67_971);
    await Assert.That(plan.ExceedsBudget).IsTrue()
      .Because("a consumer whose inbox grows by tens of thousands of rows from one message has "
             + "nothing in its logs attributing the growth; the plan carries what a log line needs");
  }

  [Test]
  public async Task RejectsANonPositiveBudgetAsync() {
    await Assert.That(() => new CompositeExpansionBudget(maxChildrenPerExpansion: 0))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a zero budget would chunk every composite into infinitely many empty pieces");
  }

  [Test]
  public async Task RejectsANegativeInnerCountRatherThanTreatingItAsEmptyAsync() {
    var budget = new CompositeExpansionBudget(maxChildrenPerExpansion: 500);
    await Assert.That(() => budget.Plan(innerEventCount: -1))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a negative count means the caller miscounted; treating it as empty would silently "
             + "discard a composite instead of surfacing the bug");
  }
}
