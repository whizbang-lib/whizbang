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
public interface IEventUpcaster {
  /// <summary>
  /// Cheap predicate — returns <c>true</c> only for events this upcaster transforms. MUST be
  /// allocation-light and side-effect free; the pipeline calls it on every event of every read.
  /// </summary>
  /// <param name="storedEvent">The freshly deserialized event (possibly already transformed by an earlier upcaster).</param>
  bool CanUpcast(IEvent storedEvent);

  /// <summary>
  /// Transforms the event into its current shape. Only called when <see cref="CanUpcast"/>
  /// returned <c>true</c>. May return a new instance (type change) or the same instance mutated
  /// (re-key / backfill). MUST be deterministic.
  /// </summary>
  /// <param name="storedEvent">The event to transform.</param>
  /// <returns>The upcasted event.</returns>
  IEvent Upcast(IEvent storedEvent);
}
