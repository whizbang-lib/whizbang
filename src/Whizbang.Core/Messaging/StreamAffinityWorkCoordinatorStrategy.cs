using Whizbang.Core.Workers;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stream-affinity decorator over an inner <see cref="IWorkCoordinatorStrategy"/>. Routes ALL
/// outbox writes through an <see cref="IOutboxBatchStrategy"/> for per-stream sliding-window
/// batching; everything else (inbox, completions, failures, flush) delegates to the inner
/// strategy unchanged.
/// </summary>
/// <remarks>
/// <para>Slice 10 of plans/pump-then-process.md (Half B). Pairs with
/// <see cref="SlidingWindowOutboxBatchStrategy"/> as the default outbox batch implementation.</para>
/// <para>
/// <strong>Async-first.</strong> The new <see cref="QueueOutboxMessageAsync"/> is the
/// canonical entry point. The legacy sync <see cref="QueueOutboxMessage"/> throws — this
/// strategy is for callers (the dispatcher's outbox publish path) that have migrated to the
/// async overload. The sync method failing loud surfaces migration gaps rather than silently
/// bypassing the per-stream batcher.
/// </para>
/// </remarks>
/// <docs>internals/outbox-batch-strategy</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamAffinityWorkCoordinatorStrategyTests.cs</tests>
public sealed class StreamAffinityWorkCoordinatorStrategy(
    IWorkCoordinatorStrategy inner,
    IOutboxBatchStrategy outboxBatch,
    Whizbang.Core.SystemEvents.SystemEventOptions? systemEventOptions = null,
    Microsoft.Extensions.Logging.ILogger? logger = null,
    Whizbang.Core.Tags.CoalesceGroupResolver? coalesceResolver = null
) : IWorkCoordinatorStrategy, IWorkFlusher {
  private readonly IWorkCoordinatorStrategy _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly IOutboxBatchStrategy _outboxBatch = outboxBatch ?? throw new ArgumentNullException(nameof(outboxBatch));
  private readonly Whizbang.Core.SystemEvents.SystemEventOptions? _systemEventOptions = systemEventOptions;
  private readonly Microsoft.Extensions.Logging.ILogger? _logger = logger;
  private readonly Whizbang.Core.Tags.CoalesceGroupResolver? _coalesceResolver = coalesceResolver;

  /// <inheritdoc />
  /// <remarks>
  /// Throws to surface callers that haven't migrated to the async overload. The
  /// stream-affinity batcher is async-by-design and silently delegating to sync would bypass
  /// the per-stream batching entirely — exactly the bug we're trying to prevent.
  /// </remarks>
  public void QueueOutboxMessage(OutboxMessage message) {
    throw new InvalidOperationException(
      $"{nameof(StreamAffinityWorkCoordinatorStrategy)} is async-first. " +
      $"Use {nameof(QueueOutboxMessageAsync)} instead of the sync {nameof(QueueOutboxMessage)} " +
      "so outbox writes route through the per-stream sliding-window batcher.");
  }

  /// <inheritdoc />
  /// <remarks>
  /// Audit generation (issue #500): the inner strategies build <c>EventAudited</c> companions
  /// inside <c>WorkCoordinatorQueues.AddOutboxMessage</c>, which this route bypasses — so the
  /// wrapper builds the companion itself and rides it on the same batch. Appending directly
  /// (rather than deferring like the inner path's <c>PendingAuditMessages</c>) is safe here
  /// because the batcher flushes straight to <c>IWorkCoordinator.StoreOutboxMessagesAsync</c>;
  /// no lifecycle stages run over batch items, so the post-lifecycle deferral the inner path
  /// needs does not apply.
  /// </remarks>
  public async Task QueueOutboxMessageAsync(OutboxMessage message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    // Tag-bound coalescing: this route bypasses WorkCoordinatorQueues.AddOutboxMessage, so the
    // wrapper is a mint seam of its own — stamp group + max-delay floor before the append,
    // mirroring the inner path (same rule for the audit companion below).
    message = _coalesceResolver?.ApplyCoalescePolicy(message) ?? message;
    await _outboxBatch.AppendAsync(message, cancellationToken).ConfigureAwait(false);

    if (message.IsEvent && _systemEventOptions?.EventAuditEnabled == true) {
      var auditMessage = Whizbang.Core.SystemEvents.AuditOutboxMessageBuilder.TryBuildAuditMessage(message, _systemEventOptions, _logger);
      if (auditMessage != null) {
        auditMessage = _coalesceResolver?.ApplyCoalescePolicy(auditMessage) ?? auditMessage;
        await _outboxBatch.AppendAsync(auditMessage, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  /// <inheritdoc />
  public void QueueInboxMessage(InboxMessage message) => _inner.QueueInboxMessage(message);

  /// <inheritdoc />
  public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus)
    => _inner.QueueOutboxCompletion(messageId, completedStatus);

  /// <inheritdoc />
  public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus)
    => _inner.QueueInboxCompletion(messageId, completedStatus);

  /// <inheritdoc />
  public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage)
    => _inner.QueueOutboxFailure(messageId, completedStatus, errorMessage);

  /// <inheritdoc />
  public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage)
    => _inner.QueueInboxFailure(messageId, completedStatus, errorMessage);

  /// <inheritdoc />
  public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default)
    => _inner.FlushAsync(flags, ct);

  /// <inheritdoc />
  public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default)
    => _inner.FlushAndGetBatchAsync(flags, ct);

  /// <summary>
  /// IWorkFlusher delegate — middleware (e.g., WhizbangFlushMiddleware) resolves
  /// IWorkCoordinatorStrategy and casts to IWorkFlusher. Forwards to the inner strategy if it
  /// implements IWorkFlusher; otherwise calls the standard FlushAsync(WorkBatchOptions, CT)
  /// path. The outbox batch buffer is flushed by its own background loop, not by this hook.
  /// </summary>
  Task IWorkFlusher.FlushAsync(CancellationToken ct) =>
    _inner is IWorkFlusher flusher
      ? flusher.FlushAsync(ct)
      : _inner.FlushAsync(WorkBatchOptions.None, ct);
}
