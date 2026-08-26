using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A publish batch must fill ACROSS streams, or high stream cardinality degrades bulk publish to
/// one message per round trip.
/// </summary>
/// <remarks>
/// <para>
/// The outbox drain assembles its publish batch from a single stream's newly-claimed rows. That is
/// fine when work concentrates in few streams and pathological when it does not: a bulk operation
/// spreads across thousands of streams holding one or two rows each, so nearly every "bulk" publish
/// carries exactly one message.
/// </para>
/// <para>
/// Measured on a producer mid-import: 88% of publish batches carried a single message, against a
/// configured cap of 25, because 98% of streams held exactly one pending row (1.41 rows per stream
/// across ~18,000 streams). The drain sustained roughly five broker round trips per second while
/// tens of thousands of rows waited.
/// </para>
/// <para>
/// Raising stream concurrency does not help — it produces more concurrent SINGLE-message publishes,
/// spending broker connections without raising messages per round trip, which is the bound that
/// actually binds.
/// </para>
/// <para>
/// The invariant that is real is per-stream ORDERING: a stream's messages must reach the broker in
/// sequence. Per-stream BATCHING is not implied by it. One publish may carry rows from many streams
/// so long as each stream's rows stay correctly ordered relative to one another.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxPublishBatcher.cs</code-under-test>
[Category("Workers")]
public class OutboxPublishBatcherTests {

  private static readonly long[] _oneTwoThree = [1L, 2L, 3L];
  private static readonly long[] _oneToFour = [1L, 2L, 3L, 4L];
  private static readonly Guid _streamA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
  private static readonly Guid _streamB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
  private static readonly Guid _streamC = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

  [Test]
  public async Task FillsAcrossStreamsRatherThanEmittingSingletonsAsync() {
    var batcher = new OutboxPublishBatcher(maxBatchSize: 25);
    var flushed = new List<IReadOnlyList<OutboxPublishBatcher.Entry>>();

    // The measured production shape: many streams, one row each.
    for (var i = 0; i < 25; i++) {
      var stream = Guid.NewGuid();
      foreach (var b in batcher.Add(new OutboxPublishBatcher.Entry(stream, i, $"msg{i}"))) {
        flushed.Add(b);
      }
    }
    foreach (var b in batcher.Flush()) { flushed.Add(b); }

    await Assert.That(flushed.Count).IsEqualTo(1)
      .Because("25 single-row streams must become ONE publish of 25, not 25 publishes of one — "
             + "that ratio is the entire defect");
    await Assert.That(flushed[0].Count).IsEqualTo(25);
  }

  [Test]
  public async Task NeverExceedsTheCapAsync() {
    var batcher = new OutboxPublishBatcher(maxBatchSize: 10);
    var flushed = new List<IReadOnlyList<OutboxPublishBatcher.Entry>>();

    for (var i = 0; i < 47; i++) {
      foreach (var b in batcher.Add(new OutboxPublishBatcher.Entry(Guid.NewGuid(), i, $"m{i}"))) {
        flushed.Add(b);
      }
    }
    foreach (var b in batcher.Flush()) { flushed.Add(b); }

    await Assert.That(flushed.All(b => b.Count <= 10)).IsTrue()
      .Because("the cap bounds the broker payload; exceeding it trades one throughput problem for "
             + "a message-size rejection");
    await Assert.That(flushed.Sum(b => b.Count)).IsEqualTo(47)
      .Because("batching must not lose rows — every added entry has to appear in exactly one batch");
  }

  [Test]
  public async Task PreservesPerStreamOrderWithinABatchAsync() {
    var batcher = new OutboxPublishBatcher(maxBatchSize: 100);
    var flushed = new List<IReadOnlyList<OutboxPublishBatcher.Entry>>();

    // Interleave three streams, each with an increasing sequence.
    foreach (var seq in new[] { 1, 2, 3 }) {
      foreach (var s in new[] { _streamA, _streamB, _streamC }) {
        foreach (var b in batcher.Add(new OutboxPublishBatcher.Entry(s, seq, $"{s:N}-{seq}"))) {
          flushed.Add(b);
        }
      }
    }
    foreach (var b in batcher.Flush()) { flushed.Add(b); }

    var all = flushed.SelectMany(b => b).ToList();
    foreach (var s in new[] { _streamA, _streamB, _streamC }) {
      var seqs = all.Where(e => e.StreamId == s).Select(e => e.Sequence).ToList();
      await Assert.That(seqs).IsEquivalentTo(_oneTwoThree)
        .Because($"stream {s:N} must retain its order — mixing streams into one publish is safe "
               + "ONLY while each stream's own sequence is preserved");
    }
  }

  [Test]
  public async Task AStreamsOrderSurvivesABatchBoundaryAsync() {
    var batcher = new OutboxPublishBatcher(maxBatchSize: 2);
    var flushed = new List<IReadOnlyList<OutboxPublishBatcher.Entry>>();

    for (var seq = 1; seq <= 4; seq++) {
      foreach (var b in batcher.Add(new OutboxPublishBatcher.Entry(_streamA, seq, $"a{seq}"))) {
        flushed.Add(b);
      }
    }
    foreach (var b in batcher.Flush()) { flushed.Add(b); }

    var seqs = flushed.SelectMany(b => b).Where(e => e.StreamId == _streamA).Select(e => e.Sequence).ToList();
    await Assert.That(seqs).IsEquivalentTo(_oneToFour)
      .Because("splitting one stream across batches is allowed, reordering it is not — batches are "
             + "published in the order they are emitted, so the sequence must survive the boundary");
  }

  [Test]
  public async Task FlushEmitsThePartialRemainderAsync() {
    var batcher = new OutboxPublishBatcher(maxBatchSize: 25);
    foreach (var _ in batcher.Add(new OutboxPublishBatcher.Entry(_streamA, 1, "only"))) { }

    var tail = batcher.Flush().ToList();

    await Assert.That(tail.Count).IsEqualTo(1);
    await Assert.That(tail[0].Count).IsEqualTo(1)
      .Because("a lone row at the end of a drain cycle must still ship — holding it for a batch "
             + "that never fills would stall the last message of every quiet stream indefinitely");
  }

  [Test]
  public async Task FlushOnAnEmptyBatcherEmitsNothingAsync() {
    var batcher = new OutboxPublishBatcher(maxBatchSize: 25);
    await Assert.That(batcher.Flush().ToList().Count).IsEqualTo(0)
      .Because("an empty flush must not publish an empty batch — brokers reject them and it wastes "
             + "a round trip on every idle cycle");
  }

  [Test]
  public async Task RejectsANonPositiveCapAsync() {
    await Assert.That(() => new OutboxPublishBatcher(maxBatchSize: 0))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a zero cap would either publish nothing or publish singletons forever, which is the "
             + "bug this type exists to remove");
  }
}
