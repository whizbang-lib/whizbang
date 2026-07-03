namespace Whizbang.Core.Perspectives;

/// <summary>
/// Non-generic seam for applying a collective spec to a single in-memory model during replay/rebuild. The
/// in-memory twin of <see cref="ICollectiveEventExecutor"/>: where that runs a set-based SQL UPDATE, this
/// mutates one already-materialized model instance. Each implementation closes a concrete <c>TModel</c> at
/// registration and advertises it via <see cref="ModelType"/>, so <see cref="ICollectiveReplayApplier"/> can
/// look up the right one by <see cref="CollectiveApplyEntry.ModelType"/> with no <c>MakeGenericType</c> —
/// AOT-clean by construction, mirroring the SQL executor's registration.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public interface ICollectiveInMemoryExecutor {
  /// <summary>The closed <c>TModel</c> this executor handles.</summary>
  Type ModelType { get; }

  /// <summary>
  /// If the row (<paramref name="currentModel"/> identified by <paramref name="streamId"/>) is in the spec's
  /// cohort, apply the spec's setters and return the mutated model; otherwise return it unchanged.
  /// </summary>
  /// <param name="spec">An <c>ICollectiveSpec&lt;TModel&gt;</c> (boxed) produced by a <c>[CollectiveApplyFor]</c> handler.</param>
  /// <param name="currentModel">The single in-memory model being folded during replay.</param>
  /// <param name="streamId">The row's id (used to evaluate a self-referential <c>Where</c>).</param>
  object ApplyToRow(object spec, object currentModel, Guid streamId);
}
