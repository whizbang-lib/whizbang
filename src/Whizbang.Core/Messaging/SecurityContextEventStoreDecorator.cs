// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
///    └─ SecurityContextEventStoreDecorator
///       └─ SyncTrackingEventStoreDecorator (tracks the id of the envelope built here, #1465)
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

    return Inner.AppendAsync(streamId, CreateAmbientEnvelope(message), cancellationToken);
  }

  /// <summary>
  /// The envelope a bare message is stored under: a fresh id and one hop carrying the ambient scope, correlation
  /// and causation. Shared with the audit decorator, which builds the envelope itself so the id it records is the
  /// id the store writes.
  /// </summary>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <param name="message">The message to wrap.</param>
  /// <returns>The envelope the store writes for <paramref name="message"/>.</returns>
  internal static MessageEnvelope<TMessage> CreateAmbientEnvelope<TMessage>(TMessage message) {
    // Top-level event-store append (no source hop) — resolve scope + identity through the SAME shared hop-first
    // helpers as the outbox hop builders, so a raw event stored through this path keeps its correlation/causation
    // and scope consistently (with a null source these collapse to the ambient context).
    var (correlation, causation) = CascadeContext.ResolveHopFirstIdentity(sourceEnvelope: null);

    return new MessageEnvelope<TMessage> {
      Priority = Whizbang.Core.Priority.PriorityContext.CurrentParent,   // priority step 1: declared from the handling in progress, undeclared outside one
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = HopStamping.AmbientTraceParent,
          Scope = CascadeContext.ResolveHopFirstScope(sourceEnvelope: null),
          CorrelationId = correlation,
          CausationId = causation,
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }
}
