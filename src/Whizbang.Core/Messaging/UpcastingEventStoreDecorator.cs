using System.Runtime.CompilerServices;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Decorator for <see cref="IEventStore"/> that applies the <see cref="EventUpcasterPipeline"/>
/// to every event materialized through a <b>polymorphic</b> read path — immediately after
/// deserialization, before routing / perspective <c>Apply</c>. This is the single unified
/// materialization seam: the drain path (<see cref="DeserializeStreamEvents"/>), the
/// replay/snapshot path (<see cref="ReadPolymorphicAsync"/>), and the lifecycle path
/// (<see cref="GetEventsBetweenPolymorphicAsync"/>) all run the identical pipeline, so projected
/// state never depends on how an event was read.
/// </summary>
/// <remarks>
/// <para>
/// Sits innermost in the decorator stack (wrapping the concrete store) so every outer decorator
/// and consumer observes upcasted events:
/// <code>
/// IEventStore
/// └─ AppendAndWaitEventStoreDecorator
///    └─ SyncTrackingEventStoreDecorator
///       └─ SecurityContextEventStoreDecorator
///          └─ UpcastingEventStoreDecorator   (this — transforms on read)
///             └─ Base IEventStore (e.g. EFCoreEventStore)
/// </code>
/// </para>
/// <para>
/// Typed reads (<c>ReadAsync&lt;TMessage&gt;</c>, <c>GetEventsBetweenAsync&lt;TMessage&gt;</c>)
/// delegate unchanged: a type-changing upcast can't be expressed when the caller asked for a
/// concrete <c>TMessage</c>, and those paths are not the projection-rebuild paths. Type-change
/// and re-key upcasts apply on the polymorphic paths, which are exactly the rebuild paths.
/// </para>
/// <para>
/// Registered only when at least one upcaster exists (<see cref="EventUpcasterPipeline.HasAny"/>),
/// so non-upcasting consumers pay nothing. <see cref="MessageEnvelope{TMessage}.Payload"/> is
/// settable, so a changed payload is written back in place — no envelope re-allocation.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/UpcastingEventStoreDecoratorTests.cs</tests>
public sealed class UpcastingEventStoreDecorator : ForwardingEventStoreDecorator {
  private readonly EventUpcasterPipeline _pipeline;

  /// <summary>Initializes the decorator.</summary>
  /// <param name="inner">The underlying event store.</param>
  /// <param name="pipeline">The upcaster pipeline applied to polymorphic reads.</param>
  public UpcastingEventStoreDecorator(IEventStore inner, EventUpcasterPipeline pipeline) : base(inner) {
    _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
  }

  private MessageEnvelope<IEvent> _upcast(MessageEnvelope<IEvent> envelope) {
    var upcasted = _pipeline.Apply(envelope.Payload);
    if (!ReferenceEquals(upcasted, envelope.Payload)) {
      envelope.Payload = upcasted;
    }
    return envelope;
  }

  // ── transformed: polymorphic read paths ──

  /// <inheritdoc />
  public override async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    // Type-change upcasters consume foreign inputs (LegacyA → GenericB) that the caller's
    // requested type set excludes — so the inner store would skip them before the upcaster runs.
    // Include those source types in the read, then drop any upcast result that isn't one of the
    // originally-requested types (a foreign input whose target wasn't asked for). Re-key / backfill
    // upcasters declare no source types, so this is a no-op for them (the common case).
    var extra = _pipeline.ExtraInputTypesFor(eventTypes);
    if (extra.Count == 0) {
      await foreach (var envelope in Inner
          .ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken)
          .WithCancellation(cancellationToken)) {
        yield return _upcast(envelope);
      }
      yield break;
    }

    var requested = new HashSet<Type>(eventTypes);
    var readTypes = new List<Type>(eventTypes);
    readTypes.AddRange(extra);
    await foreach (var envelope in Inner
        .ReadPolymorphicAsync(streamId, fromEventId, readTypes, cancellationToken)
        .WithCancellation(cancellationToken)) {
      var upcasted = _upcast(envelope);
      if (requested.Contains(upcasted.Payload.GetType())) {
        yield return upcasted;
      }
    }
  }

  /// <inheritdoc />
  public override async Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {
    var events = await Inner.GetEventsBetweenPolymorphicAsync(
        streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
    for (var i = 0; i < events.Count; i++) {
      events[i] = _upcast(events[i]);
    }
    return events;
  }

  /// <inheritdoc />
  public override List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
      IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
    var events = Inner.DeserializeStreamEvents(streamEvents, eventTypes);
    for (var i = 0; i < events.Count; i++) {
      events[i] = _upcast(events[i]);
    }
    return events;
  }

}
