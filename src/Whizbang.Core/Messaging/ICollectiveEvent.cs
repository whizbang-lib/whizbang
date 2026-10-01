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
/// processed. That makes the result deterministic <em>given an order</em>
/// of collectives. Two collectives are ordered against each other only
/// when they share an <see cref="OrderingKey"/>; without one they apply
/// in whatever order their applies finish.
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

  /// <summary>
  /// Opt-in ordering. Collectives that carry the same key, in the same scope, are applied one at a time and in
  /// the order the database committed them, so a later collective always lands after an earlier one. Null (the
  /// default) leaves the collective unordered against every other: each is its own stream, and two collectives apply
  /// in whatever order their applies finish.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Use a key when collectives express "latest wins" across a family of rows, such as
  /// <c>IsActive = (Id == e.Chosen)</c>, or when two collectives are the halves of one change (deactivate the old
  /// record, activate the new one). Pick the key at the grain of the family (<c>"activation:" + familyId</c>):
  /// everything sharing a key is serialized, so a key wider than the family costs throughput for nothing.
  /// </para>
  /// <para>
  /// The key places the collective on the stream <see cref="CollectiveOrdering.StreamIdFor"/> derives from its scope
  /// and the key. <see cref="CollectiveEventBase"/> does that for you; a hand-written collective that returns a key
  /// must return that stream from its <c>[StreamId]</c> property as well.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/messaging/collective-events</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveOrderingKeyTests.cs:AHandWrittenCollective_HasNoOrderingKeyAsync</tests>
  string? OrderingKey => null;

  /// <summary>
  /// The id of the collective its publisher sent on the same <see cref="OrderingKey"/> just before this one, or null
  /// for the first on its key, an unkeyed collective, or one from a publisher that sends no link.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A receiver applies a key's collectives in the order it committed them, which is the order they arrived, and
  /// transport can deliver them in another order than they were sent. The link lets the receiver put them back:
  /// when it handles <see cref="PredecessorType"/> and has not seen the predecessor yet, it holds this collective for a
  /// bounded time and applies the two in order when the predecessor arrives. It applies at once when it does not
  /// handle that type, when it has already seen the predecessor, or when the wait runs out.
  /// </para>
  /// <para>
  /// The dispatcher stamps it on a <see cref="CollectiveEventBase"/> as it publishes; a hand-written collective that
  /// wants the link derives from that base. It is not set by a producer.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorTrackerTests.cs</tests>
  Guid? PredecessorId => null;

  /// <summary>
  /// The type of the collective named by <see cref="PredecessorId"/>, in the form the event store records event types,
  /// so a receiver can tell whether it handles that type at all; a receiver that does not never sees the predecessor
  /// and so does not wait for it.
  /// </summary>
  /// <docs>fundamentals/messaging/collective-events#ordering-across-services</docs>
  string? PredecessorType => null;
}
