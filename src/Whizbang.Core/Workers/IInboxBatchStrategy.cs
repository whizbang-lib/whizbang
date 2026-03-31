using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Strategy for batching inbox message dedup before PreInbox lifecycle.
/// Implementations control HOW messages are collected and flushed to <c>process_work_batch</c>.
/// </summary>
/// <remarks>
/// <para>
/// This strategy operates at the pre-PreInbox phase of message processing:
/// <list type="number">
///   <item>Message arrives from transport → handler serializes to <see cref="InboxMessage"/> (no DB)</item>
///   <item><b>→ EnqueueAndWaitAsync batches messages here ←</b></item>
///   <item>Batch flushes → single <c>process_work_batch</c> call for N messages (1 DB connection)</item>
///   <item>Each handler gets its work items back (filtered by MessageId)</item>
///   <item>PreInbox lifecycle → OrderedStreamProcessor → PostInbox lifecycle</item>
/// </list>
/// </para>
/// <para>
/// <b>Relationship to <see cref="IWorkCoordinatorStrategy"/>:</b> These are separate concerns.
/// <c>IWorkCoordinatorStrategy</c> is scoped per-handler and manages queue-and-flush for all work types
/// (inbox, outbox, completions, failures). <c>IInboxBatchStrategy</c> is a singleton shared across
/// handlers that batches the inbox dedup DB call specifically. Internally, implementations create
/// a scope and resolve an <c>IWorkCoordinatorStrategy</c> to perform the actual flush.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Override the default strategy before AddTransportConsumer():
/// services.AddSingleton&lt;IInboxBatchStrategy&gt;(sp =>
///     new ImmediateInboxBatchStrategy(sp.GetRequiredService&lt;IServiceScopeFactory&gt;()));
/// </code>
/// </example>
/// <docs>messaging/transports/transport-consumer#inbox-batching</docs>
public interface IInboxBatchStrategy : IAsyncDisposable {
  /// <summary>
  /// Enqueues an inbox message for dedup processing and waits for the batch to flush.
  /// Returns when the batch containing this message has been flushed via <c>process_work_batch</c>.
  /// The caller filters the returned <see cref="WorkBatch"/> by MessageId for its own work items.
  /// </summary>
  /// <param name="message">The serialized inbox message to enqueue for dedup.</param>
  /// <param name="ct">Cancellation token. When cancelled, the returned task is cancelled
  /// and the message is removed from the pending batch (if not yet flushed).</param>
  /// <returns>The <see cref="WorkBatch"/> from the <c>process_work_batch</c> call that included this message.</returns>
  Task<WorkBatch> EnqueueAndWaitAsync(InboxMessage message, CancellationToken ct);
}
