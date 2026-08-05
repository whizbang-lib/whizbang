using Whizbang.Core;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Transforms a stored event into its current shape after deserialization and BEFORE routing /
/// perspective <c>Apply</c>. Upcasting keeps the event log immutable: events are transformed on
/// read, never rewritten in the store.
/// </summary>
/// <remarks>
/// <para>
/// An upcaster MAY:
/// <list type="bullet">
/// <item><description><b>Re-key</b> — set the <c>[StreamId]</c>-marked property to land the event
/// on a different stream (e.g. moving a per-item saga event off the saga stream onto its own
/// per-item stream). Cast to the concrete type you own and set the property — no reflection.</description></item>
/// <item><description><b>Change type</b> — return a different concrete <see cref="IEvent"/>
/// (<c>FooV1</c> → <c>FooV2</c>), provided the target type is registered in the source-generated
/// JSON context.</description></item>
/// <item><description><b>Backfill</b> — populate fields added since the event was written.</description></item>
/// </list>
/// </para>
/// <para>
/// Implementations MUST be pure and deterministic (Rule 10 — the same constraints as a
/// perspective <c>Apply</c>): no I/O, no clock, no randomness, no reflection. They run on every
/// read path (drain, replay, snapshot rehydration, ad-hoc query) and must produce identical
/// output for identical input so that projected state never depends on how an event was read.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventUpcasterPipelineTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventUpcasterPipelineTests.cs:Apply_WithMatchingUpcaster_TransformsEventAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventUpcasterPipelineTests.cs:Apply_ChainsUpcastersInRegistrationOrder_V1ToV3Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventUpcasterRegistrationTests.cs:AddEventUpcaster_RegistersAsSingletonAsync</tests>
public interface IEventUpcaster {
  /// <summary>
  /// Cheap predicate — returns <c>true</c> only for events this upcaster transforms. MUST be
  /// allocation-light and side-effect free; the pipeline calls it on every event of every read.
  /// </summary>
  /// <param name="storedEvent">The freshly deserialized event (possibly already transformed by an earlier upcaster).</param>
  bool CanUpcast(IEvent storedEvent);

  /// <summary>
  /// The stored event types this upcaster consumes as <b>input</b> that are NOT otherwise part of a
  /// rebuilt perspective's own subscription — i.e. the source types of a <i>type-change</i> upcast
  /// (<c>LegacyA</c> → <c>GenericB</c>, where the perspective subscribes to <c>GenericB</c>, not <c>LegacyA</c>).
  /// </summary>
  /// <remarks>
  /// Both the rebuild stream-enumeration and the polymorphic read are scoped to a perspective's
  /// subscribed event types, so a foreign input that a type-change upcaster would transform is
  /// otherwise filtered out <i>before</i> the upcaster runs. Declaring it here (paired with
  /// <see cref="TargetTypes"/>) lets the read seam include these inputs when one of this upcaster's
  /// targets is requested, then filter the upcasted results back to the requested set.
  /// <para>Default empty: re-key and backfill upcasters keep the same type, which is already
  /// subscribed, so they need declare nothing.</para>
  /// </remarks>
  IReadOnlyList<Type> SourceTypes => Array.Empty<Type>();

  /// <summary>
  /// The event types this upcaster <b>produces</b>. Paired with <see cref="SourceTypes"/> so the
  /// read seam only pulls in this upcaster's foreign inputs when a rebuilt perspective actually
  /// subscribes to one of these targets. Default empty (no type-change).
  /// </summary>
  IReadOnlyList<Type> TargetTypes => Array.Empty<Type>();

  /// <summary>
  /// Transforms the event into its current shape. Only called when <see cref="CanUpcast"/>
  /// returned <c>true</c>. May return a new instance (type change) or the same instance mutated
  /// (re-key / backfill). MUST be deterministic.
  /// </summary>
  /// <param name="storedEvent">The event to transform.</param>
  /// <returns>The upcasted event.</returns>
  IEvent Upcast(IEvent storedEvent);
}
