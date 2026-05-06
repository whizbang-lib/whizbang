namespace Whizbang.Core.Messaging;

/// <summary>
/// Pluggable outbox-write batching strategy. Sits between the dispatcher's outbox queue and
/// the SQL bulk-write path: the dispatcher's strategy implementations call
/// <see cref="AppendAsync"/> per outbox emission, and the strategy decides when to call
/// <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/> with a stream-ordered batch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Producer-side stream affinity.</strong> The default
/// <c>SlidingWindowOutboxBatchStrategy</c> is per-stream-keyed: each stream_id has its own
/// sliding-window buffer (50 ms / 1 s / 100 defaults). Same-stream events serialize through one
/// buffer; different streams batch independently. This eliminates the inter-emit cursor
/// inversion that fan-out workloads hit when parallel handlers race for per-stream locks
/// in <c>_emit_event_store_chain</c>. See plans/pump-then-process.md Half B.
/// </para>
/// <para>Other strategies users can plug in:</para>
/// <list type="bullet">
///   <item><c>ImmediateOutboxBatchStrategy</c> — no batching, per-message INSERT (back-compat / strict-ordering tenants)</item>
///   <item>Larger-window strategy for bursty pipelines</item>
/// </list>
/// </remarks>
/// <docs>internals/outbox-batch-strategy</docs>
public interface IOutboxBatchStrategy : IAsyncDisposable {
  /// <summary>
  /// Append an outbox message for batched insertion under its stream key. Returns when the
  /// message is safely buffered. Does NOT mean it has been written to wh_outbox yet.
  /// Messages with null <see cref="OutboxMessage.StreamId"/> route to a default stream key.
  /// </summary>
  ValueTask AppendAsync(OutboxMessage message, CancellationToken cancellationToken = default);

  /// <summary>
  /// Drain all per-stream buffers to wh_outbox and stop accepting new appends. Idempotent.
  /// Called at service shutdown.
  /// </summary>
  Task FlushAndStopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Callback used by <see cref="IOutboxBatchStrategy"/> implementations to flush a batch of
/// outbox messages to the database. Concrete consumers (DI registration) provide the function
/// that resolves <see cref="IWorkCoordinator"/> and calls
/// <see cref="IWorkCoordinator.StoreOutboxMessagesAsync"/>.
/// </summary>
public delegate Task OutboxBulkFlushCallback(OutboxMessage[] messages, CancellationToken cancellationToken);
