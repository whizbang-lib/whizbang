using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <see cref="OutstandingWork"/> — the figure the claim-outstanding budget is sized against.
/// </summary>
/// <remarks>
/// Its value semantics are part of the contract, not incidental: the worker compares and logs these
/// readings, and a record whose generated members are never exercised is a gap that only shows up
/// when someone later adds a field and the equality quietly stops meaning what it did.
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
[Category("Messaging")]
public class OutstandingWorkTests {

  [Test]
  public async Task Total_SumsAllThreeWorkKindsAsync() {
    var work = new OutstandingWork { InboxRows = 7, OutboxRows = 11, PerspectiveRows = 13 };

    await Assert.That(work.Total).IsEqualTo(31)
      .Because("inbox, outbox and perspective rows are all leased and all charge attempts, so the "
             + "bound has to see them together — counting one column alone would leave the same "
             + "arithmetic free to recur in another rather than stop");
  }

  [Test]
  public async Task Total_IsZeroWhenNothingIsHeldAsync() {
    var work = new OutstandingWork();

    await Assert.That(work.Total).IsEqualTo(0)
      .Because("an instance holding nothing must read zero — and zero is a real measurement, "
             + "distinct from the null that means the figure could not be taken at all");
  }

  [Test]
  public async Task ValueEquality_TreatsIdenticalReadingsAsEqualAsync() {
    var a = new OutstandingWork { InboxRows = 3, OutboxRows = 4, PerspectiveRows = 5 };
    var b = new OutstandingWork { InboxRows = 3, OutboxRows = 4, PerspectiveRows = 5 };

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    await Assert.That(a == b).IsTrue();
    await Assert.That(a != b).IsFalse();
  }

  [Test]
  public async Task ValueEquality_DistinguishesEveryComponentAsync() {
    var baseline = new OutstandingWork { InboxRows = 3, OutboxRows = 4, PerspectiveRows = 5 };

    // Each column checked separately: an equality that ignored one would still pass a test that
    // only varied another, and the ignored column is exactly the one whose growth goes unnoticed.
    await Assert.That(baseline).IsNotEqualTo(baseline with { InboxRows = 99 });
    await Assert.That(baseline).IsNotEqualTo(baseline with { OutboxRows = 99 });
    await Assert.That(baseline).IsNotEqualTo(baseline with { PerspectiveRows = 99 });
    await Assert.That(baseline.Equals(null)).IsFalse();
  }

  [Test]
  public async Task ToString_ReportsEveryColumnSoALogLineIsDiagnosticAsync() {
    var text = new OutstandingWork { InboxRows = 21, OutboxRows = 22, PerspectiveRows = 23 }.ToString();

    // The point of logging this at all is telling an operator WHICH queue is backing up. A rendering
    // that dropped a column would read as complete while hiding the one that mattered.
    await Assert.That(text).Contains("21");
    await Assert.That(text).Contains("22");
    await Assert.That(text).Contains("23");
  }
}
