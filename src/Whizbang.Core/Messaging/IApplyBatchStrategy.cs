namespace Whizbang.Core.Messaging;

/// <summary>
/// Pluggable apply-batching strategy. Sits between the perspective drain channel and the
/// per-stream apply pipeline: each drain signal appends a stream_id, and the strategy
/// decides when to flush the signal back through the configured
/// <see cref="ApplyBulkFlushCallback"/> so the perspective worker runs ONE apply cycle
/// over everything pending for that stream.
/// </summary>
/// <remarks>
/// <para>Default implementation: <c>SlidingWindowApplyBatchStrategy</c> with a per-stream
/// sliding window of 300 ms / 3000 ms max (mirrors the slice 18a/18b outbox + inbox
/// sliding-window batch strategies, but applied to the perspective apply boundary). This
/// solves the Order-style hot-spot where many events for the same stream arrive in
/// rapid succession and currently trigger many separate apply cycles — with the window the
/// drain signals coalesce into one flush, one snapshot read, one apply pass, one atomic
/// upsert.</para>
/// <para>Strategies users can plug in:</para>
/// <list type="bullet">
///   <item>Immediate (no batching) — for low-throughput tenants where latency dominates</item>
///   <item>Larger-window — for very wide aggregate models where flush cost is significant</item>
///   <item>Per-tenant strategies via a strategy resolver</item>
/// </list>
/// </remarks>
/// <docs>internals/apply-batch-strategy</docs>
public interface IApplyBatchStrategy : IAsyncDisposable {
  /// <summary>
  /// Append a drain signal for the given stream. Returns when the signal is safely buffered.
  /// Does NOT mean the apply has run yet — the strategy decides when to flush. Repeated
  /// appends for the same stream coalesce into one flush per sliding-window cycle.
  /// </summary>
  ValueTask AppendAsync(System.Guid streamId, System.Threading.CancellationToken cancellationToken = default);

  /// <summary>
  /// Flush all buffered signals and stop accepting new appends. Idempotent. Called at
  /// service shutdown.
  /// </summary>
  System.Threading.Tasks.Task FlushAndStopAsync(System.Threading.CancellationToken cancellationToken = default);
}

/// <summary>
/// Callback the apply batch strategy invokes when a per-stream window flushes. The signal
/// count is informational (drain signals dedupe semantically — the actual pending-event
/// fetch happens inside the apply pipeline). DI wires this to invoke
/// <c>PerspectiveWorker._processDrainModeStreamAsync</c> via the existing drain pipeline.
/// </summary>
public delegate System.Threading.Tasks.Task ApplyBulkFlushCallback(
  System.Guid streamId,
  int signalCount,
  System.Threading.CancellationToken cancellationToken);
