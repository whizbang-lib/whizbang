namespace Whizbang.Core.Workers;

/// <summary>
/// Fills a publish batch ACROSS streams, so bulk publish stays bulk when work is spread thin.
/// </summary>
/// <remarks>
/// <para>
/// The outbox drain assembled its publish batch from one stream's newly-claimed rows. That works
/// when work concentrates in a few streams and fails completely when it does not: a bulk operation
/// spreads across thousands of streams holding one or two rows each, so nearly every "bulk" publish
/// carries a single message and the drain degenerates into one broker round trip per row.
/// </para>
/// <para>
/// Measured on a producer mid-import: 88% of publish batches carried one message against a cap of
/// 25, because 98% of streams held exactly one pending row — about 1.4 rows per stream across some
/// 18,000 streams. The drain sustained roughly five round trips per second while tens of thousands
/// of rows waited.
/// </para>
/// <para>
/// Raising stream concurrency cannot fix it. More concurrency produces more simultaneous
/// single-message publishes, spending broker connections without changing messages-per-round-trip,
/// which is the quantity that binds.
/// </para>
/// <para>
/// The invariant that is real is per-stream ORDERING — a stream's messages must reach the broker in
/// sequence. Per-stream BATCHING is not implied by it: one publish may carry rows from many streams
/// provided each stream's rows stay ordered relative to one another, including across a batch
/// boundary, because batches are published in the order they are emitted.
/// </para>
/// </remarks>
/// <docs>operations/workers/outbox-drain</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OutboxPublishBatcherTests.cs</tests>
public sealed class OutboxPublishBatcher {

  /// <summary>One row awaiting publish.</summary>
  /// <param name="StreamId">The stream the row belongs to; ordering is preserved within it.</param>
  /// <param name="Sequence">Position within the stream.</param>
  /// <param name="Payload">Whatever the caller needs carried through to the publish call.</param>
  public readonly record struct Entry(Guid StreamId, long Sequence, object Payload);

  private readonly int _maxBatchSize;
  private readonly List<Entry> _pending;

  /// <summary>Rows currently held and not yet emitted.</summary>
  public int PendingCount => _pending.Count;

  /// <summary>Initializes a new instance of the <see cref="OutboxPublishBatcher"/> class.</summary>
  /// <param name="maxBatchSize">
  /// Largest batch to emit. Bounds the broker payload; a non-positive value would either publish
  /// nothing or publish singletons forever, which is the behavior this type removes.
  /// </param>
  public OutboxPublishBatcher(int maxBatchSize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
    _maxBatchSize = maxBatchSize;
    _pending = new List<Entry>(maxBatchSize);
  }

  /// <summary>
  /// Adds one row, emitting a full batch when the cap is reached.
  /// </summary>
  /// <param name="entry">The row to add.</param>
  /// <returns>
  /// Zero or one batch. Rows are appended in arrival order and emitted in that same order, so a
  /// caller that adds a stream's rows in sequence gets them published in sequence — whether they
  /// land in one batch or straddle two.
  /// </returns>
  public IEnumerable<IReadOnlyList<Entry>> Add(Entry entry) {
    _pending.Add(entry);
    if (_pending.Count >= _maxBatchSize) {
      yield return _take();
    }
  }

  /// <summary>
  /// Emits whatever remains.
  /// </summary>
  /// <returns>
  /// Zero or one batch. A partial remainder must ship at the end of a drain cycle: holding it for a
  /// batch that may never fill would stall the last message of every quiet stream indefinitely.
  /// An empty batcher emits nothing rather than an empty batch, which brokers reject and which
  /// would waste a round trip on every idle cycle.
  /// </returns>
  public IEnumerable<IReadOnlyList<Entry>> Flush() {
    if (_pending.Count > 0) {
      yield return _take();
    }
  }

  private Entry[] _take() {
    var batch = _pending.ToArray();
    _pending.Clear();
    return batch;
  }
}
