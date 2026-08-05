using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Folds collective events back into a perspective during replay/rebuild. Collective events are applied live
/// as one set-based SQL UPDATE (see <see cref="ICollectiveEventExecutor"/>), which the rebuilder does NOT
/// replay — so without this seam a collective mutation would be lost on rebuild. This applier makes the same
/// mutation reproducible per-row: it interleaves the in-scope collective events into a stream's replay by
/// message id (the runner's ordering invariant) and applies each to the single in-memory model.
/// </summary>
/// <remarks>
/// <para>
/// Optional dependency on the generated perspective runner: when no driver registers an implementation (or the
/// perspective has no <c>[CollectiveApplyFor]</c> handler for its model) the runner behaves exactly as before.
/// The Postgres drivers register <c>CollectiveReplayApplier</c> (in <c>Whizbang.Data.Postgres</c>).
/// </para>
/// <para>
/// The specs it applies are guaranteed self-referential by the <c>WHIZ106</c> analyzer (no
/// <see cref="ICollectiveQuery"/> at apply time), so per-row in-memory evaluation is deterministic — the exact
/// property this makes safe for replay.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rebuild_FoldsCollectiveEvent_OnlyIntoTheTargetRowAsync</tests>
public interface ICollectiveReplayApplier {
  /// <summary>
  /// During a rebuild, returns <paramref name="streamEvents"/> with the in-scope collective events for
  /// <paramref name="modelType"/> merged in and the whole set re-ordered by message id; outside a rebuild, or
  /// when the model has no collective handlers, returns <paramref name="streamEvents"/> unchanged. The stream's
  /// scope (tenant) is taken from the first event, so only that tenant's collective events are folded in.
  /// </summary>
  Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
    Type modelType,
    IReadOnlyList<MessageEnvelope<IEvent>> streamEvents,
    CancellationToken cancellationToken);

  /// <summary>
  /// Applies a single collective event to one in-memory model during replay and returns the (possibly mutated)
  /// model. Evaluates every matching <c>[CollectiveApplyFor]</c> spec's <c>Where</c> against the row and, when
  /// it matches, applies its setters — the in-memory twin of the live set-based UPDATE, scoped to this one row.
  /// </summary>
  object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent);
}
