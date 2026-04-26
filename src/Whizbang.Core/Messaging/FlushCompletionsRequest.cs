namespace Whizbang.Core.Messaging;

/// <summary>
/// Composite single-round-trip flusher payload. When the C# flush worker has
/// multiple completion categories buffered, it issues one call carrying all of
/// them. Single fsync at outer commit covers all sub-operations.
/// </summary>
/// <param name="OutboxIds">Outbox message ids to mark processed.</param>
/// <param name="PerspectiveCursors">Cursor advancement specs.</param>
/// <param name="PerspectiveEventWorkIds">perspective_event work ids to mark processed.</param>
/// <param name="FailuresByCategory">Per-category failure batches.</param>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public sealed record FlushCompletionsRequest(
  IReadOnlyList<Guid>? OutboxIds = null,
  IReadOnlyList<PerspectiveCursorCompletion>? PerspectiveCursors = null,
  IReadOnlyList<Guid>? PerspectiveEventWorkIds = null,
  IReadOnlyList<CategoryFailures>? FailuresByCategory = null);

/// <summary>Per-category failure batch carried by <see cref="FlushCompletionsRequest"/>.</summary>
/// <param name="Category">Which category these failures belong to.</param>
/// <param name="Items">Failure records.</param>
public sealed record CategoryFailures(WorkCategory Category, IReadOnlyList<MessageFailure> Items);

