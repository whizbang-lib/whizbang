using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Decorator for <see cref="IEventStore"/> that implements <see cref="IEventStore.AppendAndWaitAsync{TMessage,TPerspective}"/>
/// by appending events and waiting for perspective synchronization.
/// </summary>
/// <remarks>
/// <para>
/// This decorator provides the synchronous verification pattern for request-response
/// over event-sourced aggregates. After appending an event, it waits for the specified
/// perspective to process the event before returning.
/// </para>
/// <para>
/// Register this decorator in DI to enable append-and-wait functionality:
/// <code>
/// services.Decorate&lt;IEventStore, AppendAndWaitEventStoreDecorator&gt;();
/// </code>
/// </para>
/// </remarks>
/// <docs>core-concepts/event-store#append-and-wait</docs>
/// <tests>Whizbang.Core.Tests/Messaging/AppendAndWaitEventStoreDecoratorTests.cs</tests>
public sealed class AppendAndWaitEventStoreDecorator : IEventStore {
  private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(30);

  private readonly IEventStore _inner;
  private readonly IPerspectiveSyncAwaiter _syncAwaiter;

  /// <summary>
  /// Initializes a new instance of <see cref="AppendAndWaitEventStoreDecorator"/>.
  /// </summary>
  /// <param name="inner">The underlying event store implementation.</param>
  /// <param name="syncAwaiter">The perspective sync awaiter for waiting on perspective processing.</param>
  public AppendAndWaitEventStoreDecorator(
      IEventStore inner,
      IPerspectiveSyncAwaiter syncAwaiter) {
    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    _syncAwaiter = syncAwaiter ?? throw new ArgumentNullException(nameof(syncAwaiter));
  }

  /// <inheritdoc />
  public async Task<SyncResult> AppendAndWaitAsync<TMessage, TPerspective>(
      Guid streamId,
      TMessage message,
      TimeSpan? timeout = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull
      where TPerspective : class {
    // Append the event to the store
    await _inner.AppendAsync(streamId, message, cancellationToken);

    // Wait for the perspective to process the event
    var effectiveTimeout = timeout ?? _defaultTimeout;
    var result = await _syncAwaiter.WaitForStreamAsync(
        typeof(TPerspective),
        streamId,
        eventTypes: null,
        timeout: effectiveTimeout,
        eventIdToAwait: null,
        ct: cancellationToken);

    return result;
  }

  /// <inheritdoc />
  public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
    return _inner.AppendAsync(streamId, envelope, cancellationToken);
  }

  /// <inheritdoc />
  public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull {
    return _inner.AppendAsync(streamId, message, cancellationToken);
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
