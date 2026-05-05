namespace Whizbang.Core.Messaging;

/// <summary>
/// Pluggable inbox-write batching strategy. Sits between the receive boundary and the
/// inbox table: each receive callback appends an <see cref="InboxMessage"/>, and the
/// strategy decides when to call <see cref="IWorkCoordinator.StoreInboxMessagesAsync"/>
/// (per-message, batch-by-time-window, batch-by-size, etc.).
/// </summary>
/// <remarks>
/// <para>Default implementation: <c>SlidingWindowInboxBatchStrategy</c> with 50 ms / 1 s / 100
/// (sliding-window debounce / max-wait / max-size). See plans/pump-then-process.md for the
/// motivating architecture.</para>
/// <para>Other strategies users can plug in:</para>
/// <list type="bullet">
///   <item>Immediate strategy (no batching) for low-throughput / strict-ordering tenants</item>
///   <item>Larger-window strategy for high-volume backends</item>
///   <item>Per-tenant strategies via a strategy resolver</item>
/// </list>
/// </remarks>
/// <docs>internals/inbox-batch-strategy</docs>
public interface IInboxBatchStrategy : IAsyncDisposable {
  /// <summary>
  /// Append a message for batched insertion. Returns when the message is safely buffered.
  /// Does NOT mean it's been written to wh_inbox yet — the strategy decides when to flush.
  /// </summary>
  ValueTask AppendAsync(InboxMessage message, CancellationToken cancellationToken = default);

  /// <summary>
  /// Flush all buffered messages to wh_inbox and stop accepting new appends. Idempotent.
  /// Called at service shutdown.
  /// </summary>
  Task FlushAndStopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Callback used by <see cref="IInboxBatchStrategy"/> implementations to flush a batch of
/// messages to the inbox. Concrete consumers (DI registration) provide the function that
/// resolves <see cref="IWorkCoordinator"/> and calls
/// <see cref="IWorkCoordinator.StoreInboxMessagesAsync"/>.
/// </summary>
public delegate Task InboxBulkFlushCallback(InboxMessage[] messages, CancellationToken cancellationToken);
