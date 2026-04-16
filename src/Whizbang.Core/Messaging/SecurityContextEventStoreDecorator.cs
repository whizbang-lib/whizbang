using System.Diagnostics;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Decorator for <see cref="IEventStore"/> that automatically propagates security context
/// from the ambient scope when appending events with raw messages.
/// </summary>
/// <remarks>
/// <para>
/// This decorator wraps any <see cref="IEventStore"/> implementation and ensures that
/// when <see cref="AppendAsync{TMessage}(Guid, TMessage, CancellationToken)"/> is called
/// with a raw message, the resulting envelope includes the security context from
/// <see cref="ScopeContextAccessor.CurrentContext"/> if propagation is enabled.
/// </para>
/// <para>
/// This mirrors the behavior of the <see cref="Dispatcher"/> which uses
/// <c>_getSecurityContextForPropagation()</c> to propagate security context.
/// </para>
/// <para>
/// <strong>Decorator Stack:</strong>
/// <code>
/// IEventStore
/// └─ AppendAndWaitEventStoreDecorator (outer)
///    └─ SyncTrackingEventStoreDecorator
///       └─ SecurityContextEventStoreDecorator (inner)
///          └─ Base IEventStore (e.g., EFCoreEventStore)
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security-context-propagation</docs>
/// <tests>Whizbang.Core.Tests/Messaging/SecurityContextEventStoreDecoratorTests.cs</tests>
/// <remarks>
/// Initializes a new instance of <see cref="SecurityContextEventStoreDecorator"/>.
/// </remarks>
/// <param name="inner">The underlying event store implementation.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> is null.</exception>
public sealed class SecurityContextEventStoreDecorator(IEventStore inner) : IEventStore {
  private readonly IEventStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));

  /// <inheritdoc />
  /// <remarks>
  /// Delegates directly to the inner store - the envelope already contains security context.
  /// </remarks>
  public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
    return _inner.AppendAsync(streamId, envelope, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Creates an envelope with security context from the ambient scope and delegates to the inner store.
  /// Uses <see cref="CascadeContext.GetSecurityFromAmbient()"/> for consistent security extraction.
  /// </remarks>
  public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
      where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(message);

    // Use unified security extraction via CascadeContext
    var securityContext = CascadeContext.GetSecurityFromAmbient();

    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = Activity.Current?.Id,
          Scope = ScopeDelta.FromSecurityContext(securityContext),
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    return _inner.AppendAsync(streamId, envelope, cancellationToken);
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

  /// <inheritdoc />
  public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
    return _inner.DeserializeStreamEvents(streamEvents, eventTypes);
  }
}
