using System.Collections.Generic;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Marker for a first-class persistable event that mutates a *set* of
/// streams as a unit, captured at write time. Unlike per-row events that
/// each target one stream, a collective event names a cohort
/// (<see cref="MatchedStreamIds"/>) and a parametric mutation; the
/// projection runner applies it as a single SQL UPDATE per affected
/// projection table — not as N per-row Apply invocations.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Use cases:</strong> "update all non-archived jobs to this
/// template," "remove all jobs in tenant T," "flag all entities older
/// than Y." The producer expresses scope + matched-set + uniform
/// mutation; the runtime fans the work into one set-based SQL operation
/// per projection.
/// </para>
/// <para>
/// <strong>Pairs complementarily with <see cref="ICompositeEvent"/>:</strong>
/// composite events bundle many DIFFERENT events into one transport hop
/// (wire-only optimization, 1:N at receiver expansion). Collective events
/// are ONE event applied collectively to a set of streams (semantic
/// primitive, persisted as-is). Pick collective when the mutation is
/// uniform across the matched set; pick composite when each stream gets
/// a distinct payload.
/// </para>
/// <para>
/// <strong>Locked design invariants (plan 2026-06-16):</strong>
/// </para>
/// <list type="bullet">
///   <item><description><strong>Snapshot determinism:</strong> <see cref="MatchedStreamIds"/> is captured at write time, immune to subsequent state changes. Replay re-applies against the captured set, not a re-evaluated predicate.</description></item>
///   <item><description><strong>Persisted in the unified event store:</strong> collective events sit in <c>wh_event_store</c> alongside per-stream events, distinguished by the <c>is_collective</c> column. Routing is purely by the flag + <see cref="Scope"/> payload.</description></item>
///   <item><description><strong>Ephemeral <c>stream_id</c>:</strong> each collective event gets a fresh <c>stream_id</c>; routing does not use it. Per-stream replay correctly does not see collective events (use the GIN index on <c>matched_stream_ids</c> for "which collectives hit X?" audit queries).</description></item>
///   <item><description><strong>Stream is no longer self-contained:</strong> projection rebuild for a row requires per-stream events + collective events whose snapshot included that row. Audit on each row is preserved via the projection's <c>last_collective_event_id</c> pointer.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs:ICollectiveEvent_ExtendsIMessage_SoExistingPipelinesCanCarryItAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs:ICollectiveEvent_MatchedStreamIds_IsImmutableSnapshotAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs:ICollectiveEvent_Scope_CarriedThroughEvent_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs:ICollectiveEvent_MatchedStreamIds_EmptySetIsValidAsync</tests>
public interface ICollectiveEvent : IMessage {
  /// <summary>
  /// The scope envelope this event operates in. Drives runtime routing
  /// (which <see cref="ICollectiveScopeResolver"/> handles the apply path)
  /// and authorization filter composition (the resolver's
  /// <c>ScopeFilter</c> is composed as an outer WHERE around the
  /// perspective's mutation spec).
  /// </summary>
  ICollectiveScope Scope { get; }

  /// <summary>
  /// The set of stream ids the mutation applies to, evaluated and
  /// captured at write time. Immutable on the wire and at rest —
  /// <see cref="IReadOnlyList{T}"/> shape prevents accidental mutation
  /// after construction. Replay reads this set as-is; new streams that
  /// would match the original predicate later are NOT retroactively
  /// affected.
  /// </summary>
  IReadOnlyList<Guid> MatchedStreamIds { get; }

  /// <summary>
  /// Defensive cap surfaced for the opt-in
  /// <c>CollectiveEventExpander</c> (Slice 8): a consumer that asks for
  /// per-stream expansion gets at most this many synthetic
  /// per-stream events. Exceeding the cap throws
  /// <c>CollectiveExpansionLimitExceededException</c> with no partial
  /// yield, so a runaway matched-set can't silently blow out the
  /// consumer's inbox. Defaults to 10_000 — matches
  /// <see cref="ICompositeEvent.MaxInnerEventsAllowed"/> for parity.
  /// Override on the concrete event type to raise/lower per use case.
  /// </summary>
  /// <docs>fundamentals/messaging/collective-events</docs>
  int MaxExpandedInnersAllowed => 10_000;
}
