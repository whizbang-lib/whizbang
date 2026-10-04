using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Unit tests for <see cref="CommitOrderStampBatch"/>: how large the next stamp call asks for, given
/// what the last one returned (#1060). Each call costs a scan of the whole pending set, so the number of
/// calls is what a drain's cost is made of.
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <tests>Whizbang.Core/Notifications/CommitOrderStampBatch.cs</tests>
public class CommitOrderStampBatchTests {
  private const int CONFIGURED = 1_000;
  private const int CEILING = 25_000;

  /// <summary>The sizes a drain climbs through, so the expectation is not a constant array argument.</summary>
  private static readonly int[] _climb = [2_000, 4_000, 8_000, 16_000, 25_000, 25_000, 25_000, 25_000];

  [Test]
  public async Task Next_AFullBatch_GrowsAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(1_000, 1_000, CONFIGURED, CEILING)).IsEqualTo(2_000)
      .Because("a call that filled its limit left rows behind, and the next scan should carry more of "
        + "them, since the scan costs the same either way");

  [Test]
  public async Task Next_AShortBatch_ReturnsToTheConfiguredSizeAsync() {
    await Assert.That(CommitOrderStampBatch.Next(16_000, 9, CONFIGURED, CEILING)).IsEqualTo(CONFIGURED)
      .Because("fewer rows than asked for means the pending set is drained to the fence, so latency "
        + "matters again and a large batch only delays the next stamp");
    await Assert.That(CommitOrderStampBatch.Next(16_000, 0, CONFIGURED, CEILING)).IsEqualTo(CONFIGURED)
      .Because("zero is the fenced case: nothing could be stamped, and growing would not help");
  }

  [Test]
  public async Task Next_GrowthStopsAtTheCeilingAsync() {
    await Assert.That(CommitOrderStampBatch.Next(16_000, 16_000, CONFIGURED, CEILING)).IsEqualTo(CEILING)
      .Because("doubling past the ceiling would overshoot it; the step is clamped, not skipped");
    await Assert.That(CommitOrderStampBatch.Next(CEILING, CEILING, CONFIGURED, CEILING)).IsEqualTo(CEILING)
      .Because("at the ceiling a full batch keeps the ceiling: a batch holds its rows locked for the "
        + "length of its call, so the bound exists to be respected rather than approached");
  }

  [Test]
  public async Task Next_ClimbsFromTheConfiguredSizeToTheCeilingInAFewCallsAsync() {
    // The point of the change, as a sequence: a drain reaches the ceiling quickly rather than spending
    // its scans on small batches.
    var sizes = new List<int>();
    var size = CONFIGURED;
    for (var i = 0; i < 8; i++) {
      size = CommitOrderStampBatch.Next(size, size, CONFIGURED, CEILING);
      sizes.Add(size);
    }

    await Assert.That(sizes).IsEquivalentTo(_climb)
      .Because("five calls reach the ceiling, so a four-million row backlog costs on the order of "
        + "hundreds of scans rather than thousands");
  }

  [Test]
  public async Task Next_ACeilingBelowTheConfiguredSize_HonorsTheConfiguredSizeAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(5_000, 5_000, 5_000, 1_000)).IsEqualTo(5_000)
      .Because("raising the steady-state size past a ceiling nobody revisited must not silently shrink "
        + "every call to the stale ceiling");

  [Test]
  public async Task Next_DoesNotOverflowOnAHugeCurrentAsync() =>
    await Assert.That(CommitOrderStampBatch.Next(int.MaxValue, int.MaxValue, CONFIGURED, CEILING))
      .IsEqualTo(CEILING)
      .Because("the growth is computed wide and clamped, so a size near the maximum cannot wrap to a "
        + "negative batch — which the database would reject rather than ignore");
}
