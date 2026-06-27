using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Whizbang.Core;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Turnkey base class for authoring an <see cref="ICompositeEvent"/> — bundle many inner events into
/// one wire envelope that the receiver expands. One-line authoring:
/// <code>
/// public sealed class OrderBulkImportComposite : CompositeEventBase;
/// // ...
/// var composite = new OrderBulkImportComposite { StreamId = jobStreamId, Inner = jobFieldEvents };
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// The base carries the shared <c>[StreamId]</c>: every inner event inherits the composite's stream
/// at the receiver (see <see cref="ICompositeEvent"/>), so pre-mint the target stream id and set it
/// here. <see cref="InnerEvents"/> yields <see cref="Inner"/> in the order supplied (producer-yielded
/// order, which the receiver preserves).
/// </para>
/// <para>
/// Serialization: <see cref="StreamId"/>, <see cref="Inner"/>, and <see cref="MaxInnerEventsAllowed"/>
/// are <c>init</c> properties, so a derived composite round-trips through the source-generated JSON
/// context (the generator discovers <see cref="ICompositeEvent"/> types and registers them as
/// <c>IMessage</c> wire-polymorphic types; the inner <c>IMessage</c> list round-trips via the
/// registry's nested-polymorphism support). Composites are never persisted — only the expanded inner
/// events are.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeEventBaseTests.cs</tests>
/// <docs>fundamentals/messaging/composite-events</docs>
public abstract class CompositeEventBase : ICompositeEvent {
  /// <summary>
  /// The stream every inner event inherits at the receiver. Pre-mint this (e.g.
  /// <c>TrackedGuid.NewMedo()</c>) so all inner events land on the intended stream.
  /// </summary>
  [StreamId]
  public Guid StreamId { get; init; }

  /// <summary>
  /// The inner events, in producer-yielded order. Defaults to empty (never null). Settable via
  /// object initializer / deserialization. Typed as a concrete <see cref="List{T}"/> so the
  /// source-generated JSON metadata serializes each element with its polymorphic <c>$type</c>
  /// discriminator (an interface-typed collection property loses that on the source-gen path).
  /// Read it through <see cref="InnerEvents"/>.
  /// </summary>
  public List<IMessage> Inner { get; init; } = [];

  /// <summary>
  /// Defensive cap on inner-event count, surfaced at the receiver. Defaults to 10,000; override per
  /// composite via the object initializer when a use case needs a different bound.
  /// </summary>
  public int MaxInnerEventsAllowed { get; init; } = 10_000;

  /// <inheritdoc />
  /// <remarks>
  /// Not serialized: it is a computed view over <see cref="Inner"/> (which carries the wire data).
  /// <c>[JsonIgnore]</c> also breaks the source-gen cycle that <c>IEnumerable&lt;IMessage&gt;</c> would
  /// otherwise form (IMessage is polymorphic over the composite, which exposes InnerEvents again).
  /// </remarks>
  [JsonIgnore]
  public IEnumerable<IMessage> InnerEvents => Inner;

  /// <summary>
  /// Producer-side fail-fast: throws if the inner-event count exceeds <see cref="MaxInnerEventsAllowed"/>.
  /// Call before publishing so a malformed composite surfaces synchronously (in the publishing
  /// handler's <c>try/catch</c>) instead of being silently dropped at the receiver's expansion gate.
  /// Cheap — <see cref="Inner"/> is a materialized list, so this does not force a lazy enumeration.
  /// </summary>
  /// <exception cref="InvalidOperationException">The inner-event count exceeds the cap.</exception>
  public void EnsureWithinCap() {
    var count = Inner.Count;
    if (count > MaxInnerEventsAllowed) {
      throw new InvalidOperationException(
        $"Composite '{GetType().Name}' carries {count} inner events, exceeding MaxInnerEventsAllowed ({MaxInnerEventsAllowed}).");
    }
  }
}
