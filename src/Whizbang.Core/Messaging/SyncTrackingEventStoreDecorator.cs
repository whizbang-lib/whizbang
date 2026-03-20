#pragma warning disable S3604, S3928 // Primary constructor field/property initializers are intentional

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
/// Additionally, when an <see cref="ISyncEventTracker"/> and <see cref="ITrackedEventTypeRegistry"/>
/// are provided, events of tracked types are recorded for cross-scope synchronization.
/// </para>
/// <para>
/// Register this decorator in DI to enable perspective sync tracking:
/// <code>
/// services.Decorate&lt;IEventStore, SyncTrackingEventStoreDecorator&gt;();
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Messaging/SyncTrackingEventStoreDecoratorTests.cs</tests>
/// <remarks>
/// Initializes a new instance of <see cref="SyncTrackingEventStoreDecorator"/>.
/// </remarks>
/// <param name="inner">The underlying event store implementation.</param>
/// <param name="tracker">The scoped event tracker (optional - tracking is skipped if null).</param>
/// <param name="envelopeRegistry">The envelope registry for looking up message IDs (optional).</param>
/// <param name="syncEventTracker">The singleton event tracker for cross-scope sync (optional).</param>
/// <param name="typeRegistry">The registry of event types to track (optional).</param>
public sealed class SyncTrackingEventStoreDecorator(
    IEventStore inner,
    IScopedEventTracker? tracker = null,
    IEnvelopeRegistry? envelopeRegistry = null,
    ISyncEventTracker? syncEventTracker = null,
    ITrackedEventTypeRegistry? typeRegistry = null) : IEventStore {
  private readonly IEventStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly IScopedEventTracker? _tracker = tracker;
  private readonly ISyncEventTracker? _syncEventTracker = syncEventTracker;
  private readonly ITrackedEventTypeRegistry? _typeRegistry = typeRegistry;
  private readonly IEnvelopeRegistry? _envelopeRegistry = envelopeRegistry;

  /// <inheritdoc />
  public async Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
    await _inner.AppendAsync(streamId, envelope, cancellationToken);

    var eventType = typeof(TMessage);
    var messageId = envelope.MessageId;

    // Track the emitted event in scoped tracker (same request scope)
    _tracker?.TrackEmittedEvent(streamId, eventType, messageId.Value);

    // Track in singleton tracker for cross-scope sync (if event type is registered)
    _trackInSingletonTracker(eventType, messageId.Value, streamId);
  }

  /// <inheritdoc />
  public async Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull {
    // Try to get the envelope from the registry to get the actual MessageId
    var envelope = _envelopeRegistry?.TryGetEnvelope(message);
    var messageId = envelope?.MessageId ?? MessageId.New();

    await _inner.AppendAsync(streamId, message, cancellationToken);

    var eventType = typeof(TMessage);

    // Track the emitted event in scoped tracker (same request scope)
    _tracker?.TrackEmittedEvent(streamId, eventType, messageId.Value);

    // Track in singleton tracker for cross-scope sync (if event type is registered)
    _trackInSingletonTracker(eventType, messageId.Value, streamId);
  }

  /// <summary>
  /// Tracks the event in the singleton tracker if the event type is registered.
  /// </summary>
  private void _trackInSingletonTracker(Type eventType, Guid messageId, Guid streamId) {
    if (_syncEventTracker is null || _typeRegistry is null) {
      return;
    }

    // Check if this event type should be tracked
    var perspectiveNames = _typeRegistry.GetPerspectiveNames(eventType);
    foreach (var perspectiveName in perspectiveNames) {
      _syncEventTracker.TrackEvent(eventType, messageId, streamId, perspectiveName);
    }
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
