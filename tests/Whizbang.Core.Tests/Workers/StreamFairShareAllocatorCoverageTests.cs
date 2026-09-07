using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for three <see cref="StreamFairShareAllocator"/> branches the primary suite
/// (<see cref="StreamFairShareAllocatorTests"/>) doesn't reach: an all-empty demand set, a budget
/// too small to seat even one stream's floor, and the depth-weighting pass rounding a stream's
/// share down to zero. A fair-share allocator decides which stream gets served next each cycle —
/// starve one silently and its events stop applying with no error, just a queue that never drains.
/// </summary>
public class StreamFairShareAllocatorCoverageTests {

  private static readonly Guid[] _ids =
    [.. Enumerable.Range(1, 8).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}"))];

  private static StreamFairShareAllocator _alloc(int minPerStream = 10)
    => new(new StreamFairShareAllocator.Settings { MinRowsPerStream = minPerStream });

  /// <summary>What breaks: spending budget on empty streams starves the streams that actually have
  /// work. Every demand reporting zero depth must be a pure no-op cycle, not an allocation of
  /// nothing.</summary>
  [Test]
  public async Task Allocate_AllStreamsEmpty_ReturnsNoAllocationsAsync() {
    var plan = _alloc().Allocate(
      totalBudget: 100,
      [new StreamDemand(_ids[0], 0), new StreamDemand(_ids[1], 0)]);

    await Assert.That(plan).IsEmpty()
      .Because("an idle service's demands all report zero depth — handing out a floor here would spend budget on streams with nothing to fetch");
  }

  /// <summary>What breaks: seating a stream for less than the floor is how every stream advances
  /// and none completes. A budget too small to grant even one full floor must admit nobody rather
  /// than hand out a useless partial share.</summary>
  [Test]
  public async Task Allocate_BudgetBelowTheFloor_AdmitsNoStreamsAsync() {
    var plan = _alloc(minPerStream: 10).Allocate(
      totalBudget: 5,
      [new StreamDemand(_ids[0], 20)]);

    await Assert.That(plan).IsEmpty()
      .Because("granting less than the floor is a useless partial share — the allocator must wait for a cycle with enough budget instead of seating nobody usefully");
  }

  /// <summary>What breaks: after the floor pass, the remainder is weighted by residual depth so a
  /// deep stream completes instead of creeping forward one floor per cycle. Integer-division
  /// rounding can legitimately compute a zero share for a thin residual — that stream must be
  /// skipped, not double-counted or crashed on, and the leftover must still land somewhere via the
  /// largest-first tail distribution rather than being silently dropped.</summary>
  [Test]
  public async Task Allocate_RoundedShareOfZero_SkipsWithoutLosingTheRemainderAsync() {
    // Floor = 1 each for three admitted streams (1+1+1 = 3 of a 4-budget), leaving 1 remaining to
    // weight by residual depth (0, 1, 999). The residual-0 stream is skipped via the earlier
    // continue; both residual>0 streams compute a proportional share that rounds down to zero
    // (remaining=1 spread over a residualTotal of 1000), forcing the "share <= 0" skip on each in
    // turn before the tail distribution hands the whole leftover unit to the deepest stream.
    var plan = _alloc(minPerStream: 1).Allocate(
      totalBudget: 4,
      [new StreamDemand(_ids[0], 1), new StreamDemand(_ids[1], 2), new StreamDemand(_ids[2], 1000)]);

    await Assert.That(plan.Sum(a => a.Rows)).IsEqualTo(4)
      .Because("every unit of the budget must land somewhere — a rounded-to-zero share must not silently vanish");
    await Assert.That(plan.Single(a => a.StreamId == _ids[0]).Rows).IsEqualTo(1)
      .Because("the shallow stream only ever holds 1 row — the floor alone satisfies it");
    await Assert.That(plan.Single(a => a.StreamId == _ids[2]).Rows).IsGreaterThan(1)
      .Because("the deepest stream must absorb the leftover the zero-rounded shares couldn't use — that is the whole point of the largest-first tail distribution");
  }
}
