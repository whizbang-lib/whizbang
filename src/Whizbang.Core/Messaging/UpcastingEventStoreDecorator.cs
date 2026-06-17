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
public sealed class UpcastingEventStoreDecorator : IEventStore {
  private readonly IEventStore _inner;
  private readonly EventUpcasterPipeline _pipeline;

  /// <summary>Initializes the decorator.</summary>
  /// <param name="inner">The underlying event store.</param>
  /// <param name="pipeline">The upcaster pipeline applied to polymorphic reads.</param>
  public UpcastingEventStoreDecorator(IEventStore inner, EventUpcasterPipeline pipeline) {
    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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
  public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    await foreach (var envelope in _inner
        .ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken)
        .WithCancellation(cancellationToken)) {
      yield return _upcast(envelope);
    }
  }

  /// <inheritdoc />
  public async Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {
    var events = await _inner.GetEventsBetweenPolymorphicAsync(
        streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
    for (var i = 0; i < events.Count; i++) {
      events[i] = _upcast(events[i]);
    }
    return events;
  }

  /// <inheritdoc />
  public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
      IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
    var events = _inner.DeserializeStreamEvents(streamEvents, eventTypes);
    for (var i = 0; i < events.Count; i++) {
      events[i] = _upcast(events[i]);
    }
    return events;
  }

  // ── delegated: typed reads, appends, metadata ──

  /// <inheritdoc />
  public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
    _inner.AppendAsync(streamId, envelope, cancellationToken);

  /// <inheritdoc />
  public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
    _inner.AppendAsync(streamId, message, cancellationToken);

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
    _inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
    _inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);

  /// <inheritdoc />
  public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
    _inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);

  /// <inheritdoc />
  public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    _inner.GetLastSequenceAsync(streamId, cancellationToken);

  /// <inheritdoc />
  public Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) =>
    _inner.GetCommitSequenceAsync(eventId, cancellationToken);
}
