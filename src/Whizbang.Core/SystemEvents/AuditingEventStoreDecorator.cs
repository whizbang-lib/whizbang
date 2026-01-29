using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Decorator that emits <see cref="EventAudited"/> system events when domain events are appended.
/// Wraps the inner event store and invokes the system event emitter after successful appends.
/// </summary>
/// <remarks>
/// <para>
/// This decorator is registered automatically when event auditing is enabled via
/// <see cref="SystemEventOptions.EnableEventAudit"/>. It intercepts <c>AppendAsync</c>
/// calls and emits audit events to the <c>$wb-system</c> stream.
/// </para>
/// <para>
/// The decorator delegates to <see cref="ISystemEventEmitter"/> which handles:
/// - Checking if auditing is enabled
/// - Excluding events with <c>[AuditEvent(Exclude = true)]</c>
/// - Serializing the event body
/// - Extracting scope from the envelope
/// </para>
/// </remarks>
/// <docs>core-concepts/system-events#event-auditing</docs>
public sealed class AuditingEventStoreDecorator : IEventStore {
  private readonly IEventStore _inner;
  private readonly ISystemEventEmitter _emitter;

  /// <summary>
  /// Creates a new auditing event store decorator.
  /// </summary>
  /// <param name="inner">The inner event store to wrap.</param>
  /// <param name="emitter">The system event emitter for audit events.</param>
  public AuditingEventStoreDecorator(IEventStore inner, ISystemEventEmitter emitter) {
    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
  }

  /// <inheritdoc />
  public async Task AppendAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) {
    // First, append to the inner store
    await _inner.AppendAsync(streamId, envelope, cancellationToken);

    // Get the stream position after append
    var streamPosition = await _inner.GetLastSequenceAsync(streamId, cancellationToken);

    // Emit audit event (emitter handles enabled check and exclusions)
    await _emitter.EmitEventAuditedAsync(streamId, streamPosition, envelope, cancellationToken);
  }

  /// <inheritdoc />
  public async Task AppendAsync<TMessage>(
      Guid streamId,
      TMessage message,
      CancellationToken cancellationToken = default)
      where TMessage : notnull {
    // Delegate to inner - this overload creates/retrieves envelope internally
    // The emitter is invoked inside the inner store if it also has auditing
    // To avoid double auditing, we only audit the envelope overload
    await _inner.AppendAsync(streamId, message, cancellationToken);
  }

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId,
      long fromSequence,
      CancellationToken cancellationToken = default) =>
      _inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId,
      Guid? fromEventId,
      CancellationToken cancellationToken = default) =>
      _inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId,
      Guid? fromEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
      _inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken);

  /// <inheritdoc />
  public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      CancellationToken cancellationToken = default) =>
      _inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);

  /// <inheritdoc />
  public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
      _inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);

  /// <inheritdoc />
  public Task<long> GetLastSequenceAsync(
      Guid streamId,
      CancellationToken cancellationToken = default) =>
      _inner.GetLastSequenceAsync(streamId, cancellationToken);
}
