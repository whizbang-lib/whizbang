using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Base class for <see cref="IEventStore"/> decorators: forwards every pass-through member —
/// including the interface's default-implemented probes — to the wrapped store, so a decorator
/// overrides only the members it intercepts.
/// </summary>
/// <remarks>
/// <para>
/// A C# default interface method is NOT virtual dispatch through composition: a decorator that
/// implements <see cref="IEventStore"/> directly and forgets to forward a default member silently
/// serves the interface default instead of the inner store's override — which is how decorated
/// stores lost <see cref="IEventStore.GetCommitSequenceAsync"/> (snapshot commit-sequence anchors
/// silently null) and <see cref="IEventStore.HasStreamEventsBeforeAsync"/> (row-retention
/// resurrection-on-wake never fired). Deriving from this class makes the forwarding surface a
/// single implementation instead of a hand-copied block per decorator.
/// </para>
/// <para>
/// Two default members are DELIBERATELY not forwarded, so their interface defaults keep working:
/// <see cref="IEventStore.AppendBatchAsync"/> — its default loops over <c>this.AppendAsync</c>,
/// which routes every entry through the decorator's own append interception (forwarding it to the
/// inner store would bypass auditing/tracking) — and <c>AppendAndWaitAsync</c>, which only the
/// outermost sync decorator implements for real. The full contract is locked by
/// <c>EventStoreDecoratorForwardingTests</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-store</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreDecoratorForwardingTests.cs</tests>
/// <param name="inner">The wrapped event store.</param>
public abstract class ForwardingEventStoreDecorator(IEventStore inner) : IEventStore {
  /// <summary>The wrapped store every non-intercepted member forwards to.</summary>
  protected IEventStore Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

  /// <inheritdoc />
  public virtual Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
    Inner.AppendAsync(streamId, envelope, cancellationToken);

  /// <inheritdoc />
  public virtual Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
    Inner.AppendAsync(streamId, message, cancellationToken);

  /// <inheritdoc />
  public virtual IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
    Inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);

  /// <inheritdoc />
  public virtual IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
    Inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);

  /// <inheritdoc />
  public virtual IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
    Inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken);

  /// <inheritdoc />
  public virtual Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
    Inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);

  /// <inheritdoc />
  public virtual Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
    Inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);

  /// <inheritdoc />
  public virtual Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    Inner.GetLastSequenceAsync(streamId, cancellationToken);

  /// <inheritdoc />
  public virtual List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
    Inner.DeserializeStreamEvents(streamEvents, eventTypes);

  /// <inheritdoc />
  public virtual Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) =>
    Inner.GetCommitSequenceAsync(eventId, cancellationToken);

  /// <inheritdoc />
  public virtual Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, CancellationToken cancellationToken = default) =>
    Inner.HasStreamEventsBeforeAsync(streamId, beforeEventId, cancellationToken);
}
