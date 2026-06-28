namespace Whizbang.Core.Messaging;

/// <summary>
/// Marker for a first-class persistable event whose semantics are
/// "apply this mutation to every row in scope-X at this point in the
/// event sequence." The event IS the descriptor: <see cref="Scope"/>
/// names the cohort, the event's concrete type carries the mutation
/// payload, and the projection runner composes a single SQL UPDATE
/// per affected projection table whose WHERE is the scope predicate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Determinism is at scope level, not stream level.</strong>
/// The event does NOT enumerate the streams that happened to be in
/// scope at write time. Replay re-evaluates the predicate against
/// the projection state at the moment the collective event is being
/// processed. Because event sourcing guarantees that projection state
/// at any point in a replay is fully determined by the event sequence
/// up to that point, the predicate's result is deterministic — and it
/// reflects the logically correct state, not the original execution's
/// possibly-wrong (e.g. out-of-order delivery) result.
/// </para>
/// <para>
/// <strong>Pairs complementarily with <see cref="ICompositeEvent"/>:</strong>
/// composite events bundle many distinct events into one transport hop
/// (wire-only, 1:N at receiver expansion). Collective events are ONE
/// event applied collectively (semantic primitive, persisted as-is).
/// Pick collective when the mutation is uniform across a scope; pick
/// composite when each stream gets a distinct payload.
/// </para>
/// <para>
/// <strong>Routing:</strong> the dispatcher branches on
/// <see cref="EventFlags.Collective"/> in the inbox envelope's
/// <c>Flags</c> column, looks up the <see cref="ICollectiveScopeResolver"/>
/// matching <see cref="ICollectiveScope.ScopeKind"/>, finds matching
/// handlers in <c>CollectiveApplyRegistry</c>, and fans out via
/// <c>ICollectiveEventExecutor</c> per <c>TModel</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs:ICollectiveEvent_ExtendsIMessage_SoExistingPipelinesCanCarryItAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveEventContractTests.cs:ICollectiveEvent_Scope_CarriedThroughEvent_Async</tests>
public interface ICollectiveEvent : IEvent {
  /// <summary>
  /// The scope envelope this event operates in. Drives runtime routing
  /// (which <see cref="ICollectiveScopeResolver"/> handles the apply
  /// path) and is the SOLE source of the SQL UPDATE's <c>WHERE</c>
  /// predicate — the resolver's <c>ScopeFilter(Scope)</c> is composed
  /// directly into the UPDATE.
  /// <para>
  /// Typed as the <see cref="CollectiveScope"/> abstract base (not the
  /// <see cref="ICollectiveScope"/> interface) so the event round-trips
  /// through the AOT-strict message serializer via a polymorphic
  /// <c>$scopeKind</c> discriminator. <see cref="CollectiveScope"/>
  /// implements <see cref="ICollectiveScope"/>, so resolver APIs are
  /// unchanged.
  /// </para>
  /// </summary>
  CollectiveScope Scope { get; }
}
