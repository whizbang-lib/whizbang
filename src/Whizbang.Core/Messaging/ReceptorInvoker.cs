using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of <see cref="IReceptorInvoker"/> that invokes receptors
/// based on lifecycle stage.
/// </summary>
/// <remarks>
/// <para>
/// This implementation queries the <see cref="IReceptorRegistry"/> for receptors registered
/// at the specified stage and invokes them. All categorization (which receptors fire at which
/// stages) is done at compile time by the source generator:
/// </para>
/// <list type="bullet">
/// <item><description>Receptors WITH [FireAt(X)] are registered at stage X only</description></item>
/// <item><description>Receptors WITHOUT [FireAt] are registered at LocalImmediateInline, PreOutboxInline, and PostInboxInline</description></item>
/// </list>
/// <para>
/// No runtime logic is needed to determine when a receptor fires - it's all compile-time categorization.
/// </para>
/// <para>
/// <strong>Scoped Service:</strong> This invoker is registered as a scoped service and uses the
/// ambient scope for resolving dependencies. Workers create a scope per message, then resolve
/// the invoker from that scope. This follows industry patterns from MediatR and MassTransit.
/// </para>
/// <para>
/// <strong>Event Cascading:</strong> When receptors return IEvent instances (directly, in tuples, or arrays),
/// these events are cascaded (published) via the optional <see cref="IEventCascader"/>.
/// </para>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public sealed class ReceptorInvoker : IReceptorInvoker {
  private readonly IReceptorRegistry _registry;
  private readonly IServiceProvider _scopedProvider;
  private readonly IEventCascader? _eventCascader;

  /// <summary>
  /// Creates a new ReceptorInvoker.
  /// </summary>
  /// <param name="registry">The receptor registry to query for discovered receptors.</param>
  /// <param name="scopedProvider">The scoped service provider (ambient scope from worker).</param>
  public ReceptorInvoker(IReceptorRegistry registry, IServiceProvider scopedProvider)
    : this(registry, scopedProvider, eventCascader: null) {
  }

  /// <summary>
  /// Creates a new ReceptorInvoker with event cascading support.
  /// </summary>
  /// <param name="registry">The receptor registry to query for discovered receptors.</param>
  /// <param name="scopedProvider">The scoped service provider (ambient scope from worker).</param>
  /// <param name="eventCascader">Optional cascader for publishing events returned by receptors.</param>
  /// <remarks>
  /// <para>
  /// <strong>Security Context:</strong> When <see cref="IMessageSecurityContextProvider"/> is registered,
  /// it will be resolved from the scoped provider during message processing to establish security context.
  /// </para>
  /// <para>
  /// When a security provider is available, the invoker will:
  /// </para>
  /// <list type="number">
  /// <item><description>Extract security context from the message envelope's hops</description></item>
  /// <item><description>Call <see cref="IMessageSecurityContextProvider.EstablishContextAsync"/> to establish security context</description></item>
  /// <item><description>Set <see cref="IScopeContextAccessor.Current"/> with the established context</description></item>
  /// </list>
  /// <para>
  /// This enables scoped services (like UserContextManager) to access security information during receptor execution.
  /// </para>
  /// </remarks>
  /// <docs>core-concepts/message-security#lifecycle-receptors</docs>
  public ReceptorInvoker(
    IReceptorRegistry registry,
    IServiceProvider scopedProvider,
    IEventCascader? eventCascader) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(scopedProvider);
    _registry = registry;
    _scopedProvider = scopedProvider;
    _eventCascader = eventCascader;
  }

  /// <inheritdoc/>
  public async ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);

    // Note: context is available for receptors that need metadata about the invocation
    // (stream ID, message source, etc.) but is not used by the invoker itself.
    // Receptors can access it if needed for logging, tracing, or conditional logic.
    _ = context; // Currently unused by invoker, but passed to receptors if needed

    // Extract payload from envelope - this is what receptors receive
    var message = envelope.Payload;

    // GetType() is AOT-safe - returns the runtime type
    var messageType = message.GetType();

    // Registry already has categorized receptors at compile time
    // Just get receptors for this type/stage combination and invoke them
    var receptors = _registry.GetReceptorsFor(messageType, stage);

    if (receptors.Count == 0) {
      // No receptors registered for this message type and stage - this is normal
      return;
    }

    // Use the injected scoped provider directly - no CreateScope needed
    // This invoker is registered as scoped and uses the ambient scope from the worker
    var securityProvider = _scopedProvider.GetService<IMessageSecurityContextProvider>();

    // Establish security context from the envelope before invoking receptors
    // This enables scoped services (like UserContextManager) to access security information
    if (securityProvider is not null) {
      var securityContext = await securityProvider
        .EstablishContextAsync(envelope, _scopedProvider, cancellationToken)
        .ConfigureAwait(false);

      if (securityContext is not null) {
        var accessor = _scopedProvider.GetService<IScopeContextAccessor>();
        if (accessor is not null) {
          accessor.Current = securityContext;
        }
      }
    }

    // Set message context from envelope for injectable IMessageContext
    // This enables receptors to inject IMessageContext and access MessageId, CorrelationId, UserId, etc.
    var messageContextAccessor = _scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is not null) {
      var securityContext = envelope.GetCurrentSecurityContext();
      messageContextAccessor.Current = new MessageContext {
        MessageId = envelope.MessageId,
        CorrelationId = envelope.GetCorrelationId() ?? ValueObjects.CorrelationId.New(),
        CausationId = envelope.GetCausationId() ?? ValueObjects.MessageId.New(),
        Timestamp = envelope.GetMessageTimestamp(),
        UserId = securityContext?.UserId
      };
    }

    foreach (var receptor in receptors) {
      // InvokeAsync is a pre-compiled delegate (no reflection)
      // Pass the scoped provider so receptor can be resolved with its dependencies
      var result = await receptor.InvokeAsync(_scopedProvider, message, cancellationToken).ConfigureAwait(false);

      // Cascade any IMessage instances (events and commands) from the receptor's return value
      // Handles tuples, arrays, Route wrappers via IEventCascader
      // Pass source envelope so cascaded messages can inherit SecurityContext
      // Note: Receptor default routing not passed through - routing is determined by
      // message attributes, Route wrappers, or system default (Outbox)
      if (result is not null && _eventCascader is not null) {
        await _eventCascader.CascadeFromResultAsync(result, sourceEnvelope: envelope, receptorDefault: null, cancellationToken).ConfigureAwait(false);
      }
    }
  }
}

/// <summary>
/// No-op implementation of <see cref="IReceptorInvoker"/> used when no registry is available.
/// </summary>
/// <remarks>
/// This is used as a fallback when <c>AddWhizbangReceptorRegistry()</c> has not been called.
/// It allows the system to function without lifecycle receptor invocation.
/// </remarks>
internal sealed class NullReceptorInvoker : IReceptorInvoker {
  /// <inheritdoc/>
  public ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
    // No-op - no receptors to invoke
    return ValueTask.CompletedTask;
  }
}
