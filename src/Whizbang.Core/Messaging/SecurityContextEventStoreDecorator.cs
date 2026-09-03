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
/// <tests>tests/Whizbang.Core.Tests/Messaging/SecurityContextEventStoreDecoratorTests.cs</tests>
/// <remarks>
/// Initializes a new instance of <see cref="SecurityContextEventStoreDecorator"/>.
/// </remarks>
/// <param name="inner">The underlying event store implementation.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> is null.</exception>
public sealed class SecurityContextEventStoreDecorator(IEventStore inner) : ForwardingEventStoreDecorator(inner) {
  /// <inheritdoc />
  /// <remarks>
  /// Creates an envelope with security context from the ambient scope and delegates to the inner store.
  /// Uses <see cref="CascadeContext.GetSecurityFromAmbient()"/> for consistent security extraction.
  /// </remarks>
  public override Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);

    // Top-level event-store append (no source hop) — resolve scope + identity through the SAME shared hop-first
    // helpers as the outbox hop builders, so a raw event stored through this path keeps its correlation/causation
    // and scope consistently (with a null source these collapse to the ambient context).
    var (correlation, causation) = CascadeContext.ResolveHopFirstIdentity(sourceEnvelope: null);

    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = Activity.Current?.Id,
          Scope = CascadeContext.ResolveHopFirstScope(sourceEnvelope: null),
          CorrelationId = correlation,
          CausationId = causation,
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    return Inner.AppendAsync(streamId, envelope, cancellationToken);
  }
}
