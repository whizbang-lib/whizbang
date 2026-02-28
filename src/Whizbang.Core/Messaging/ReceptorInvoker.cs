using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
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
  private readonly IPerspectiveSyncAwaiter? _syncAwaiter;

  /// <summary>
  /// Creates a new ReceptorInvoker.
  /// </summary>
  /// <param name="registry">The receptor registry to query for discovered receptors.</param>
  /// <param name="scopedProvider">The scoped service provider (ambient scope from worker).</param>
  public ReceptorInvoker(IReceptorRegistry registry, IServiceProvider scopedProvider)
    : this(registry, scopedProvider, eventCascader: null, syncAwaiter: null) {
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
    IEventCascader? eventCascader)
    : this(registry, scopedProvider, eventCascader, syncAwaiter: null) {
  }

  /// <summary>
  /// Creates a new ReceptorInvoker with event cascading and perspective sync support.
  /// </summary>
  /// <param name="registry">The receptor registry to query for discovered receptors.</param>
  /// <param name="scopedProvider">The scoped service provider (ambient scope from worker).</param>
  /// <param name="eventCascader">Optional cascader for publishing events returned by receptors.</param>
  /// <param name="syncAwaiter">Optional sync awaiter for [AwaitPerspectiveSync] attribute handling.</param>
  /// <docs>core-concepts/perspectives/perspective-sync</docs>
  public ReceptorInvoker(
    IReceptorRegistry registry,
    IServiceProvider scopedProvider,
    IEventCascader? eventCascader,
    IPerspectiveSyncAwaiter? syncAwaiter) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(scopedProvider);
    _registry = registry;
    _scopedProvider = scopedProvider;
    _eventCascader = eventCascader;
    _syncAwaiter = syncAwaiter;
  }

  /// <inheritdoc/>
  public async ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);

    // Context provides metadata about the invocation (stream ID, event ID, message source, etc.)
    // Used for perspective sync to pass the incoming event's ID for cross-scope sync

    // Extract payload from envelope - this is what receptors receive
    var message = envelope.Payload;

    // Unwrap Routed<T> if the payload contains a routing wrapper
    // This ensures receptors receive the actual message type, not the dispatch wrapper
    if (message is Dispatch.IRouted routed) {
      if (routed.Mode == Dispatch.DispatchMode.None || routed.Value == null) {
        // RoutedNone should not be in an envelope - skip silently
        return;
      }
      message = routed.Value;
    }

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
    // This enables receptors to inject IMessageContext and access MessageId, CorrelationId, UserId, TenantId, etc.
    var messageContextAccessor = _scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is not null) {
      var securityContext = envelope.GetCurrentSecurityContext();
      messageContextAccessor.Current = new MessageContext {
        MessageId = envelope.MessageId,
        CorrelationId = envelope.GetCorrelationId() ?? ValueObjects.CorrelationId.New(),
        CausationId = envelope.GetCausationId() ?? ValueObjects.MessageId.New(),
        Timestamp = envelope.GetMessageTimestamp(),
        UserId = securityContext?.UserId,
        TenantId = securityContext?.TenantId
      };
    }

    // Try to get stream ID extractor for stream-based sync
    var streamIdExtractor = _scopedProvider.GetService<IStreamIdExtractor>();
    Guid? extractedStreamId = streamIdExtractor?.ExtractStreamId(message, messageType);

    foreach (var receptor in receptors) {
      // Check for [AwaitPerspectiveSync] attributes and await sync if needed
      if (_syncAwaiter is not null && receptor.SyncAttributes is { Count: > 0 }) {
        foreach (var syncAttr in receptor.SyncAttributes) {
          var timeout = TimeSpan.FromMilliseconds(syncAttr.EffectiveTimeoutMs);
          SyncResult syncResult;

          // Use stream-based sync when stream ID extractor is available
          if (extractedStreamId.HasValue) {
            var eventTypes = syncAttr.EventTypes?.ToArray();
            // Pass the incoming event's ID for cross-scope sync - this is CRITICAL
            // Without this, WaitForStreamAsync has no way to know what event to wait for
            // when the event was emitted in a different scope (e.g., command handler)
            syncResult = await _syncAwaiter.WaitForStreamAsync(
                syncAttr.PerspectiveType,
                extractedStreamId.Value,
                eventTypes,
                timeout,
                eventIdToAwait: context?.EventId,
                cancellationToken).ConfigureAwait(false);

            // Create and set SyncContext for receptor access via AsyncLocal
            var syncContext = new SyncContext {
              StreamId = extractedStreamId.Value,
              PerspectiveType = syncAttr.PerspectiveType,
              Outcome = syncResult.Outcome,
              EventsAwaited = syncResult.EventsAwaited,
              ElapsedTime = syncResult.ElapsedTime,
              FailureReason = syncResult.Outcome == SyncOutcome.TimedOut ? "Timeout exceeded" : null
            };
            SyncContextAccessor.CurrentContext = syncContext;
          } else {
            // Fall back to scope-based sync when no stream ID extractor
            var syncOptions = syncAttr.EventTypes is { Count: > 0 }
                ? SyncFilter.ForEventTypes(syncAttr.EventTypes.ToArray()).WithTimeout(timeout).Build()
                : SyncFilter.CurrentScope().WithTimeout(timeout).Build();

            syncResult = await _syncAwaiter.WaitAsync(syncAttr.PerspectiveType, syncOptions, cancellationToken).ConfigureAwait(false);
          }

          // If FireBehavior is FireOnSuccess and we timed out, throw an exception
          if (syncAttr.FireBehavior == SyncFireBehavior.FireOnSuccess && syncResult.Outcome == SyncOutcome.TimedOut) {
            throw new PerspectiveSyncTimeoutException(
                syncAttr.PerspectiveType,
                timeout,
                $"Perspective sync timed out waiting for {syncAttr.PerspectiveType.Name} before invoking receptor {receptor.ReceptorId}");
          }
          // FireBehavior.FireAlways continues regardless of timeout
          // FireBehavior.FireOnEachEvent is future functionality
        }
      }

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
