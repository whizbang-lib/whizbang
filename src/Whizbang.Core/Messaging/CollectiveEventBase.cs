using System;
using System.Text.Json.Serialization;
using Whizbang.Core;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Turnkey base record for authoring an <see cref="ICollectiveEvent"/>. Carries the event's own
/// auto-generated stream id so a collective event persists to the event store as a first-class
/// single-event stream, then derive and add the <see cref="ICollectiveEvent.Scope"/> (and any
/// mutation-payload fields):
/// <code>
/// [PinnedId("…")]
/// public sealed record ArchiveJobsCollectiveEvent : CollectiveEventBase {
///   public required CollectiveScope Scope { get; init; }
///   public required DateTimeOffset OccurredAt { get; init; }
/// }
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each collective event is its own single-event stream</strong>, unless it carries an
/// <see cref="OrderingKey"/>, in which case it shares its key's stream with every collective carrying the same key in
/// the same scope. The base carries a <c>[StreamId] [GenerateStreamId]</c> <see cref="StreamId"/> that the dispatcher
/// mints at publish — the producer never sets it. That stream is what the <c>__collective__</c> sink work row is keyed to, and
/// what the perspective worker leases to dispatch the event through <c>ICollectiveDispatcher</c> exactly
/// once. (Scope-level determinism means the event carries no enumerated stream set — only its scope.)
/// </para>
/// <para>
/// An abstract <em>record</em> (not class) so concrete collective events, which are records, can derive
/// from it. <see cref="ICollectiveEvent.Scope"/> is left to the derived record — an abstract base need not
/// implement every interface member.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveScopeBaseRoutingTests.cs:CollectiveEventBase_IsAnEvent_CarriesGeneratedStreamAndScopeAsync</tests>
public abstract record CollectiveEventBase : ICollectiveEvent {
  /// <summary>
  /// The collective event's stream id. Without an <see cref="OrderingKey"/> it is auto-generated at dispatch (each
  /// collective event is its own single-event stream). With one it is the key's stream,
  /// <see cref="CollectiveOrdering.StreamIdFor"/> over the scope and the key, and a value written to it is ignored.
  /// Do not set this — the framework mints or derives it.
  /// </summary>
  /// <remarks>
  /// Mutable (<c>set</c>, not <c>init</c>) on purpose: <c>[GenerateStreamId]</c> mints the id at dispatch
  /// and the source generator's <c>SetStreamId</c> writer can only target a mutable Guid — an <c>init</c>-only
  /// property would make the attribute a silent no-op (the minted id could never be written back). The
  /// StreamIdGenerator emits a build error for <c>[GenerateStreamId]</c> on an <c>init</c>-only stream id to
  /// keep this from regressing.
  /// </remarks>
  [StreamId]
  [GenerateStreamId]
  public Guid StreamId {
    get => string.IsNullOrWhiteSpace(OrderingKey) ? _streamId : CollectiveOrdering.StreamIdFor(Scope, OrderingKey);
    set => _streamId = value;
  }

  private Guid _streamId;

  /// <summary>
  /// The cohort descriptor — the sole source of the SQL UPDATE's <c>WHERE</c> predicate. Set it on the
  /// derived event via the object initializer.
  /// </summary>
  public required CollectiveScope Scope { get; init; }

  /// <inheritdoc/>
  /// <remarks>
  /// Setting it moves the event onto its key's stream: <see cref="StreamId"/> then reads the derived id whatever the
  /// dispatcher mints, so every collective sharing the key, of any type, lands on one stream.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveOrderingKeyTests.cs:WithAnOrderingKey_TheStreamIsDerivedFromTheScopeAndTheKeyAsync</tests>
  public string? OrderingKey { get; init; }

  /// <inheritdoc/>
  /// <remarks>
  /// Written by the store into the stored payload (migration 190) and read back on receipt, so it is settable; a
  /// producer leaves it alone. Left out of the serialized event when there is no link, so an unlinked collective is
  /// written exactly as before. The wire name is pinned, because the store writes the link under it (migration 190,
  /// <c>{p,predecessorId}</c>) whatever naming the payload serializer would otherwise use.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorLinkTests.cs:Serialization_TheLinkFields_HaveTheNamesTheStoreWritesAsync</tests>
  [JsonPropertyName("predecessorId")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public Guid? PredecessorId { get; set; }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CollectivePredecessorLinkTests.cs:Serialization_TheLinkFields_HaveTheNamesTheStoreWritesAsync</tests>
  [JsonPropertyName("predecessorType")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? PredecessorType { get; set; }
}
