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
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AnApplierWrittenBeforeRewindsFoldedCollectives_InterleavesNoneAfterAStartAsync</tests>
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

  /// <summary>
  /// Whether <paramref name="collectiveEvent"/>, as a replay merged it in, is still in flight on the live path (#1970): its
  /// collective sink work row is pending, so the live sink may yet apply it to rows the replay did not fold it into. A
  /// replay that folds such a collective takes no snapshot, since no mark says which of its rows a snapshot would hold.
  /// The default says none is.
  /// </summary>
  /// <param name="collectiveEvent">A collective event the replay merged in.</param>
  /// <returns>True when the collective is still in flight.</returns>
  /// <docs>fundamentals/messaging/collective-events#collective-replay-from-a-start</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/CollectiveReplayRunnerTests.cs:AnApplierWrittenBeforeInFlightCollectivesWereKnown_SaysNoneIsInFlightAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rewind_ThatMeetsACollectiveStillInFlight_TakesNoSnapshotAsync</tests>
  bool IsInFlight(IEvent collectiveEvent) => false;

  /// <summary>
  /// Whether a replay of <paramref name="modelType"/> folds foreign events into its rows (#2621): a perspective declares a
  /// fan-out to the model (<see cref="IPerspectiveFanOutFor{TModel, TEvent}"/>). A replay asks once, and a model with no
  /// fan-out folds exactly as it did before fan-outs existed. The default folds none.
  /// </summary>
  /// <param name="modelType">The model the row folds into.</param>
  /// <returns>True when the model has a fan-out.</returns>
  /// <docs>fundamentals/perspectives/multi-stream#fan-out-on-rebuild-and-rewind</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutReplayRunnerTests.cs:AnApplierWrittenBeforeReplaysFoldedFanOuts_FoldsNoneAsync</tests>
  bool FoldsFanOutsInto(Type modelType) => false;

  /// <summary>
  /// The foreign streams <paramref name="currentModel"/>'s fan-out keys hold (#2621): one per key the model's fan-outs
  /// declare, for each that holds a stream. A replay reads them after every event of the row's own that it folds: a key
  /// that takes a stream it has not held yet brings that stream's fan-out events into the fold. The default holds none.
  /// </summary>
  /// <param name="modelType">The model the row folds into.</param>
  /// <param name="currentModel">The row as folded so far.</param>
  /// <returns>Each stream once.</returns>
  /// <docs>fundamentals/perspectives/multi-stream#fan-out-on-rebuild-and-rewind</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutReplayRunnerTests.cs:AnApplierWrittenBeforeReplaysFoldedFanOuts_FoldsNoneAsync</tests>
  IReadOnlyList<Guid> FanOutKeysOf(Type modelType, object currentModel) => [];

  /// <summary>
  /// Returns <paramref name="remaining"/>, the events a replay has still to fold, with the fan-out events of
  /// <paramref name="keyStreamId"/>'s stream that <paramref name="start"/> <see cref="CollectiveReplayStart.Follows"/>
  /// merged in (#2621), in <see cref="ReplayEventOrder"/> among themselves, each after every remaining event it was
  /// committed after, as <see cref="InterleaveAfterAsync"/> merges collectives. Only <paramref name="start"/>'s tenant's
  /// events are merged. Each comes back as a collective: its payload is the foreign event wrapped with its stream and
  /// tenant, so it folds into the row and moves no checkpoint, and <see cref="ApplyInMemory"/> applies it only while the
  /// row's key holds its stream. The default merges none.
  /// </summary>
  /// <param name="modelType">The model the row folds into.</param>
  /// <param name="remaining">The events the replay has not folded yet, in replay order.</param>
  /// <param name="keyStreamId">The foreign stream a key of the row has just taken, or held when the replay started.</param>
  /// <param name="start">Where the key took the stream (the event that set it, or the replay's start), and the tenant.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The events to fold, in order.</returns>
  /// <docs>fundamentals/perspectives/multi-stream#fan-out-on-rebuild-and-rewind</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/FanOutReplayRunnerTests.cs:AnApplierWrittenBeforeReplaysFoldedFanOuts_FoldsNoneAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/FanOutReplayApplierTests.cs:InterleaveFanOutsAsync_MergesTheStreamsFanOutsAfterTheStart_InTheTenant_AsCollectivesAsync</tests>
  Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveFanOutsAsync(
    Type modelType,
    IReadOnlyList<MessageEnvelope<IEvent>> remaining,
    Guid keyStreamId,
    CollectiveReplayStart start,
    CancellationToken cancellationToken) => Task.FromResult(remaining);
}
