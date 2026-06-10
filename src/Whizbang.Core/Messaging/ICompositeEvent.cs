using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Marker for an event that fans out into multiple inner events at the
/// receiver. A composite is a wire-only optimization: producers emit a
/// single envelope carrying N inner messages; the receiver expands it
/// before invoking receptors and writing the event store. Composite types
/// are NEVER recorded in the event store — only the expanded inner events
/// are persisted, so replay reads inner events back as-if no composite
/// existed.
/// </summary>
/// <remarks>
/// <para>
/// Use cases: a bulk operation that produces many domain events (e.g.,
/// "350 jobs imported" emitting 350 <c>JobCreatedEvent</c> instances). One
/// wire message instead of 350 means one outbox row, one publish, one
/// receive, one batched event-store append — all of which reduce
/// per-message overhead enormously.
/// </para>
/// <para>
/// Pairs with the body-offload feature (W3 slices 1–7): a composite of
/// 5,000 inner events easily exceeds the 256 KB Azure Service Bus
/// Standard ceiling, so the post-serialize hook chain offloads the
/// composite body to blob storage and substitutes a small claim envelope
/// on the wire. The receiver rehydrates the claim, deserializes the
/// composite, and expands it.
/// </para>
/// <para>
/// Resolved design decisions (W3 notes, 2026-06-09):
/// </para>
/// <list type="bullet">
///   <item><description><strong>Failure atomicity:</strong> all-or-nothing. If any inner event fails during receiver expansion or event-store append, the whole composite rolls back. Per-inner retry is future work.</description></item>
///   <item><description><strong>Inner-event StreamId:</strong> all inner events inherit the composite's StreamId. Producers needing per-inner StreamIds emit separate envelopes (no composite).</description></item>
///   <item><description><strong>Ordering:</strong> inner events are processed in producer-yielded order (the order <see cref="InnerEvents"/> enumerates them). Matches single-row outbox storage semantics.</description></item>
///   <item><description><strong>Event-store replay:</strong> composite envelopes NEVER reach the replay path — only expanded inner events. Producers can stop emitting a composite type at any time without affecting historical replay.</description></item>
///   <item><description><strong>Lifecycle hooks:</strong> PreInbox / PostInbox / etc. fire per-inner-event, consistent with "composite is wire-only" — the lifecycle never sees the composite.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/messaging/composite-events</docs>
public interface ICompositeEvent : IMessage {
  /// <summary>
  /// Yields the inner events this composite expands into. Receivers
  /// enumerate this property at receive time and invoke per-inner-event
  /// processing for each yielded message. Order matters: the receiver
  /// processes them in the order yielded. Producers MAY allocate
  /// lazily — the receiver enumerates exactly once.
  /// </summary>
  IEnumerable<IMessage> InnerEvents { get; }

  /// <summary>
  /// Defensive cap surfaced at receive time. A producer that accidentally
  /// yields, say, 100,000 inner events from a bug in its enumerator gets
  /// caught here rather than corrupting the receiver's batched
  /// event-store append. Default <c>10_000</c> — override on the
  /// composite type to raise / lower per use case.
  /// </summary>
  /// <remarks>
  /// The receiver enforces this by counting yielded inner events as it
  /// expands; reaching the cap raises a typed failure (
  /// <see cref="MessageFailureReason.CompositeInnerEventLimitExceeded"/>)
  /// without writing partial results.
  /// </remarks>
  int MaxInnerEventsAllowed => 10_000;
}
