// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// For a replay of one row that starts at <paramref name="start"/> (#1956): a rewind, or a rebuild that starts from a
  /// stream's newest snapshot. Returns <paramref name="streamEvents"/> with the collectives of
  /// <paramref name="start"/>'s tenant for <paramref name="modelType"/> that it <see cref="CollectiveReplayStart.Follows"/>
  /// merged in, in the order <see cref="InterleaveForReplayAsync"/> merges them. A rewind is a replay whatever the ambient
  /// processing mode, so unlike <see cref="InterleaveForReplayAsync"/> this merges outside a rebuild too, and with no
  /// stream events it returns the collectives alone, for a replay whose only news since its snapshot is a collective. The
  /// default merges none, so an applier written before rewinds folded collectives replays as it always did.
  /// </summary>
  /// <param name="modelType">The model the row folds into.</param>
  /// <param name="streamEvents">The stream's events the replay read, in replay order.</param>
  /// <param name="start">Where the replay starts, and the tenant whose collectives it folds.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The events to fold, in order.</returns>
  /// <docs>fundamentals/messaging/collective-events#collective-replay-from-a-start</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AnApplierWrittenBeforeRewindsFoldedCollectives_InterleavesNoneAfterAStartAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rewind_FromASnapshot_FoldsTheCollectivesCommittedAfterItAsync</tests>
  Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveAfterAsync(
    Type modelType,
    IReadOnlyList<MessageEnvelope<IEvent>> streamEvents,
    CollectiveReplayStart start,
    CancellationToken cancellationToken) => Task.FromResult(streamEvents);

  /// <summary>
  /// Applies a single collective event to one in-memory model during replay and returns the (possibly mutated)
  /// model. Evaluates every matching <c>[CollectiveApplyFor]</c> spec's <c>Where</c> against the row and, when
  /// it matches, applies its setters — the in-memory twin of the live set-based UPDATE, scoped to this one row.
  /// </summary>
  object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent);

  /// <summary>
  /// The stored event-type names of the collective events that apply to the model named
  /// <paramref name="modelTypeName"/> (its fully qualified name, as the perspective registry records it). A
  /// blue-green rebuild reads them to notice a collective committed while it ran. The default names none.
  /// </summary>
  /// <param name="modelTypeName">The model's fully qualified name, <c>global::</c> prefix optional.</param>
  /// <docs>fundamentals/perspectives/rebuild#blue-green</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveReplayApplierCoverageTests.cs:CollectiveEventTypeNamesFor_NamesTheModelsCollectivesOnlyAsync</tests>
  IReadOnlyList<string> CollectiveEventTypeNamesFor(string modelTypeName) => [];
}
