using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Splits one global row budget across streams without starving either the shallow or the deep.
/// </summary>
/// <remarks>
/// <para>
/// Throughput is a property of TOTAL rows moved, not of any single stream, so the budget is
/// denominated globally and then divided. A per-stream cap cannot express this: it fixes the wrong
/// quantity, so the total swings with however many streams happen to be active and no single
/// setting suits both a thousand one-row streams and one stream holding thousands.
/// </para>
/// <para>
/// Dividing it invites starvation from two opposite directions, and a scheme that only guards one
/// simply relocates the problem:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Breadth starvation</b> — a deep stream absorbs the whole budget and every other stream waits,
/// so unrelated work queues behind one large aggregate that happens to be mid-drain.
/// </description></item>
/// <item><description>
/// <b>Depth starvation</b> — the budget is spread evenly, every stream creeps forward a few rows per
/// cycle, and a stream holding thousands never finishes. Even division looks the fairest and is the
/// worst outcome for the work that most needs to complete.
/// </description></item>
/// </list>
/// <para>
/// So: a floor per admitted stream buys breadth, the remainder is weighted by depth to buy
/// completion, and when the floor alone cannot cover every stream the admitted SET rotates — a
/// subset served now is fine, the same subset served forever is not.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/StreamFairShareAllocator.cs</code-under-test>
[Category("Workers")]
public class StreamFairShareAllocatorTests {

  private static readonly Guid[] _ids =
    [.. Enumerable.Range(1, 40).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}"))];

  private static StreamFairShareAllocator _alloc(int minPerStream = 10)
    => new(new StreamFairShareAllocator.Settings { MinRowsPerStream = minPerStream });

  private static List<StreamDemand> _demands(params (int Index, int Depth)[] spec)
    => [.. spec.Select(s => new StreamDemand(_ids[s.Index], s.Depth))];

  [Test]
  public async Task TheTotalNeverExceedsTheGlobalBudgetAsync() {
    var plan = _alloc().Allocate(totalBudget: 100,
      _demands((0, 5000), (1, 5000), (2, 5000), (3, 5000)));

    await Assert.That(plan.Sum(a => a.Rows)).IsLessThanOrEqualTo(100)
      .Because("the budget is the whole point — it is what makes throughput a fixed, tunable "
             + "quantity instead of a function of how many streams happen to be active");
  }

  [Test]
  public async Task ADeepStreamGetsMoreThanTheFloorAsync() {
    var plan = _alloc(minPerStream: 10).Allocate(totalBudget: 1000,
      _demands((0, 5000), (1, 10), (2, 10)));

    var deep = plan.Single(a => a.StreamId == _ids[0]).Rows;
    await Assert.That(deep).IsGreaterThan(10)
      .Because("if depth earned nothing beyond the floor, a stream holding thousands would advance "
             + "ten rows a cycle and never finish — that is depth starvation, and even division is "
             + "exactly what causes it");
  }

  [Test]
  public async Task ShallowStreamsAreNotStarvedByADeepOneAsync() {
    var plan = _alloc(minPerStream: 10).Allocate(totalBudget: 1000,
      _demands((0, 100_000), (1, 4), (2, 4), (3, 4)));

    foreach (var i in new[] { 1, 2, 3 }) {
      await Assert.That(plan.Single(a => a.StreamId == _ids[i]).Rows).IsGreaterThan(0)
        .Because("one huge aggregate mid-drain must not park every unrelated stream behind it — "
               + "that is breadth starvation, and it is what makes a service look wedged while it "
               + "is in fact busy");
    }
  }

  [Test]
  public async Task AStreamIsNeverGivenMoreThanItHoldsAsync() {
    var plan = _alloc(minPerStream: 50).Allocate(totalBudget: 1000, _demands((0, 3), (1, 5000)));

    await Assert.That(plan.Single(a => a.StreamId == _ids[0]).Rows).IsEqualTo(3)
      .Because("handing a three-row stream fifty rows of budget wastes forty-seven that the deep "
             + "stream could have used — the floor is a guarantee, not an allotment to burn");
  }

  [Test]
  public async Task ASingleDeepStreamMayUseTheWholeBudgetAsync() {
    var plan = _alloc().Allocate(totalBudget: 500, _demands((0, 100_000)));

    await Assert.That(plan.Single().Rows).IsEqualTo(500)
      .Because("with nothing to be fair to, withholding budget would just be throughput left on "
             + "the floor");
  }

  [Test]
  public async Task WhenTheFloorCannotCoverEveryStreamASubsetIsServedAsync() {
    // 40 streams x floor 10 = 400 needed, budget is 100.
    var plan = _alloc(minPerStream: 10).Allocate(totalBudget: 100,
      [.. Enumerable.Range(0, 40).Select(i => new StreamDemand(_ids[i], 500))]);

    await Assert.That(plan.Sum(a => a.Rows)).IsLessThanOrEqualTo(100);
    await Assert.That(plan.Count).IsLessThan(40)
      .Because("serving all forty would mean under two rows each — slicing below the floor is how "
             + "every stream advances and none completes");
    await Assert.That(plan.All(a => a.Rows >= 10)).IsTrue()
      .Because("whoever IS admitted must get a useful amount, or admission is meaningless");
  }

  [Test]
  public async Task TheAdmittedSetRotatesSoNobodyStarvesForeverAsync() {
    var allocator = _alloc(minPerStream: 10);
    var demands = Enumerable.Range(0, 40).Select(i => new StreamDemand(_ids[i], 500)).ToList();

    var firstServed = allocator.Allocate(100, demands).Select(a => a.StreamId).ToHashSet();
    var secondServed = allocator.Allocate(100, demands).Select(a => a.StreamId).ToHashSet();

    await Assert.That(secondServed.SetEquals(firstServed)).IsFalse()
      .Because("a subset served this cycle is correct; the SAME subset served every cycle is "
             + "permanent starvation for the rest, and it looks identical to healthy throughput "
             + "from every aggregate metric");
  }

  [Test]
  public async Task RotationEventuallyCoversEveryStreamAsync() {
    var allocator = _alloc(minPerStream: 10);
    var demands = Enumerable.Range(0, 40).Select(i => new StreamDemand(_ids[i], 500)).ToList();

    var seen = new HashSet<Guid>();
    for (var cycle = 0; cycle < 40; cycle++) {
      foreach (var a in allocator.Allocate(100, demands)) { seen.Add(a.StreamId); }
    }

    await Assert.That(seen.Count).IsEqualTo(40)
      .Because("rotation has to actually come back around — a cursor that advances but never wraps "
             + "starves the tail just as completely as no rotation at all");
  }

  [Test]
  public async Task APerStreamCeilingBoundsHowMuchOneStreamCanHoldAsync() {
    var allocator = new StreamFairShareAllocator(new StreamFairShareAllocator.Settings {
      MinRowsPerStream = 10,
      MaxRowsPerStream = 200,
    });

    var plan = allocator.Allocate(totalBudget: 1000, _demands((0, 100_000), (1, 100_000)));

    await Assert.That(plan.All(a => a.Rows <= 200)).IsTrue()
      .Because("a wider page holds its lease longer, so the ceiling is what keeps one stream from "
             + "taking a lease it cannot drain inside the window");
  }

  [Test]
  public async Task NoBudgetMeansNoAllocationAsync() {
    var plan = _alloc().Allocate(totalBudget: 0, _demands((0, 500)));
    await Assert.That(plan.Count).IsEqualTo(0);
  }

  [Test]
  public async Task NoDemandMeansNoAllocationAsync() {
    var plan = _alloc().Allocate(totalBudget: 500, []);
    await Assert.That(plan.Count).IsEqualTo(0);
  }

  [Test]
  public async Task StreamsWithNothingQueuedAreNotAdmittedAsync() {
    var plan = _alloc().Allocate(totalBudget: 500, _demands((0, 0), (1, 40)));

    await Assert.That(plan.Any(a => a.StreamId == _ids[0])).IsFalse()
      .Because("spending a floor on an empty stream takes it from one with work, and empty streams "
             + "are the common case on an idle service");
  }

  [Test]
  public async Task NullDemandsAreRejectedAsync()
    => await Assert.That(() => _alloc().Allocate(100, null!)).Throws<ArgumentNullException>();
}
