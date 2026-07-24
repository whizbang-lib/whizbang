using System;
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
/// <strong>Each collective event is its own single-event stream.</strong> The base carries a
/// <c>[StreamId] [GenerateStreamId]</c> <see cref="StreamId"/> that the dispatcher mints at publish — the
/// producer never sets it. That stream is what the <c>__collective__</c> sink work row is keyed to, and
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
public abstract record CollectiveEventBase : ICollectiveEvent {
  /// <summary>
  /// The collective event's own stream id, auto-generated at dispatch (each collective event is its own
  /// single-event stream). Do not set this — the framework mints it.
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
  public Guid StreamId { get; set; }

  /// <summary>
  /// The cohort descriptor — the sole source of the SQL UPDATE's <c>WHERE</c> predicate. Set it on the
  /// derived event via the object initializer.
  /// </summary>
  public required CollectiveScope Scope { get; init; }
}
