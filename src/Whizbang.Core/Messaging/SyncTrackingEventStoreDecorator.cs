using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Decorator for <see cref="IEventStore"/> that tracks emitted events for perspective synchronization.
/// </summary>
/// <remarks>
/// <para>
/// This decorator wraps any <see cref="IEventStore"/> implementation and notifies
/// the <see cref="IScopedEventTracker"/> when events are appended. This enables
/// perspective synchronization to know which events need to be awaited.
/// </para>
/// <para>
/// Register this decorator in DI to enable perspective sync tracking:
/// <code>
/// services.Decorate&lt;IEventStore, SyncTrackingEventStoreDecorator&gt;();
/// </code>
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Messaging/SyncTrackingEventStoreDecoratorTests.cs</tests>
public sealed class SyncTrackingEventStoreDecorator : IEventStore {
  private readonly IEventStore _inner;
  private readonly IScopedEventTracker? _tracker;
  private readonly IEnvelopeRegistry? _envelopeRegistry;

  /// <summary>
  /// Initializes a new instance of <see cref="SyncTrackingEventStoreDecorator"/>.
  /// </summary>
  /// <param name="inner">The underlying event store implementation.</param>
  /// <param name="tracker">The scoped event tracker (optional - tracking is skipped if null).</param>
  /// <param name="envelopeRegistry">The envelope registry for looking up message IDs (optional).</param>
  public SyncTrackingEventStoreDecorator(
      IEventStore inner,
      IScopedEventTracker? tracker = null,
      IEnvelopeRegistry? envelopeRegistry = null) {
    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    _tracker = tracker;
    _envelopeRegistry = envelopeRegistry;
  }

  /// <inheritdoc />
  public async Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
    await _inner.AppendAsync(streamId, envelope, cancellationToken);

    // Track the emitted event for sync awaiting
    _tracker?.TrackEmittedEvent(streamId, typeof(TMessage), envelope.MessageId);
  }

  /// <inheritdoc />
  public async Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull {
    // Try to get the envelope from the registry to get the actual MessageId
    var envelope = _envelopeRegistry?.TryGetEnvelope(message);
    var messageId = envelope?.MessageId ?? MessageId.New();

    await _inner.AppendAsync(streamId, message, cancellationToken);

    // Track the emitted event for sync awaiting
    _tracker?.TrackEmittedEvent(streamId, typeof(TMessage), messageId);
  }

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) {
    return _inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);
  }

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) {
    return _inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);
  }

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
    return _inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken);
  }

  /// <inheritdoc />
  public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) {
    return _inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);
  }

  /// <inheritdoc />
  public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
    return _inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
  }

  /// <inheritdoc />
  public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) {
    return _inner.GetLastSequenceAsync(streamId, cancellationToken);
  }
}
