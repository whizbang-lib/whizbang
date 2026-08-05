using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Marker for an event that fans out into multiple inner events at the receiver. A composite is a
/// wire-only optimization: producers emit a single envelope carrying N inner messages; the receiver
/// fans it out at the <strong>dispatch seam</strong> — as an ordinary inbox row, inside the durable
/// retry/DLQ envelope — into per-inner-event inbox rows. Composite types are NEVER recorded in the
/// event store — only the expanded inner events are persisted, so replay reads inner events back as-if
/// no composite existed. See <c>docs/fundamentals/messaging/composite-events.md</c> for the full
/// lifecycle, the pre-fanout hook, fan-out control, and the no-rebroadcast invariant.
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
///   <item><description><strong>Failure atomicity:</strong> configurable via <see cref="Atomicity"/> — <c>Independent</c> (default; a bad child is dropped, the rest fan out) or <c>Atomic</c> (any child failure dead-letters the whole composite). A cap breach always dead-letters the whole composite.</description></item>
///   <item><description><strong>Inner-event StreamId:</strong> all inner events inherit the composite's StreamId. Producers needing per-inner StreamIds emit separate envelopes (no composite).</description></item>
///   <item><description><strong>Ordering:</strong> inner events are processed in producer-yielded order (the order <see cref="InnerEvents"/> enumerates them). Matches single-row outbox storage semantics.</description></item>
///   <item><description><strong>Event-store replay:</strong> composite envelopes NEVER reach the replay path — only expanded inner events. Producers can stop emitting a composite type at any time without affecting historical replay.</description></item>
///   <item><description><strong>Dispatchable + hookable:</strong> the composite is a real message at the surface — a pre-fanout <c>IReceptor&lt;TComposite&gt;</c> fires before fan-out (its inline emissions commit atomically with the children). After fan-out, the per-inner-event lifecycle runs normally; downstream sees only children.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/messaging/composite-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeEventContractTests.cs:ICompositeEvent_InnerEvents_LazyEnumerationSupportedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeEventContractTests.cs:ICompositeEvent_ExtendsIMessage_SoExistingPipelinesCanCarryItAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:TryExpand_YieldsOneChildInboxMessagePerInnerAsync</tests>
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

  /// <summary>
  /// How fan-out is triggered. <see cref="Whizbang.Core.Messaging.FanoutMode.Auto"/> (default) fans out
  /// <see cref="InnerEvents"/> automatically at the dispatch seam; <see cref="Whizbang.Core.Messaging.FanoutMode.Manual"/>
  /// defers to a pre-fanout receptor that drives fan-out via <see cref="DispatchFanoutControl"/>.
  /// </summary>
  /// <docs>fundamentals/messaging/composite-events#fanout-control</docs>
  FanoutMode FanoutMode => FanoutMode.Auto;

  /// <summary>
  /// Per-child failure policy. <see cref="FanoutAtomicity.Independent"/> (default) drops a child that
  /// fails to serialize and fans out the rest; <see cref="FanoutAtomicity.Atomic"/> dead-letters the
  /// whole composite on any child failure (all-or-nothing).
  /// </summary>
  /// <docs>fundamentals/messaging/composite-events#fanout-control</docs>
  FanoutAtomicity Atomicity => FanoutAtomicity.Independent;
}
