using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;

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
/// <docs>fundamentals/receptors/lifecycle-receptors</docs>
/// <docs>operations/observability/tracing#parent-context</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public sealed partial class ReceptorInvoker : IReceptorInvoker {
  private readonly IReceptorRegistry _registry;
  private readonly IServiceProvider _scopedProvider;
  private readonly IEventCascader? _eventCascader;
  private readonly IPerspectiveSyncAwaiter? _syncAwaiter;
  private ILogger? _logger;

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
  /// <docs>fundamentals/security/message-security#lifecycle-receptors</docs>
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
  /// <docs>fundamentals/perspectives/perspective-sync</docs>
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

    // Use the injected scoped provider directly - no CreateScope needed
    // This invoker is registered as scoped and uses the ambient scope from the worker
    var securityProvider = _scopedProvider.GetService<IMessageSecurityContextProvider>();

    // Hoist securityContext so it can be used for MessageContext below
    IScopeContext? securityContext = null;

    // Establish security context from the envelope BEFORE checking for receptors
    // This enables scoped services (like UserContextManager) to access security information
    // even when there are no receptors registered for this message type/stage
    if (securityProvider is not null) {
      securityContext = await securityProvider
        .EstablishContextAsync(envelope, _scopedProvider, cancellationToken)
        .ConfigureAwait(false);

      if (securityContext is not null) {
        var accessor = _scopedProvider.GetService<IScopeContextAccessor>();
        if (accessor is not null) {
          accessor.Current = securityContext;
        }
      }
    }

    // Extract caller info from the first Current hop (captured at dispatch time)
    // Hoisted to method scope so it's available both for MessageContext and receptor invocation
    CallerInfo? callerInfo = null;
    if (envelope.Hops is { Count: > 0 }) {
      for (int i = 0; i < envelope.Hops.Count; i++) {
        var hop = envelope.Hops[i];
        if (hop.Type == HopType.Current && hop.CallerMemberName is not null) {
          callerInfo = new CallerInfo(
              hop.CallerMemberName,
              hop.CallerFilePath ?? string.Empty,
              hop.CallerLineNumber ?? 0);
          break;
        }
      }
    }

    // Set message context from envelope for injectable IMessageContext
    // This enables code to inject IMessageContext and access MessageId, CorrelationId, UserId, TenantId, etc.
    // CRITICAL: This must happen BEFORE early return to ensure InitiatingContext is always set
    var messageContextAccessor = _scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is not null) {
      // FIX: Use established security context first, fall back to envelope.GetCurrentScope()
      // The security provider extracts context from the envelope via extractors - use that result!
      // envelope.GetCurrentScope() may be null if hops don't have scope data
      IScopeContext? scopeForContext = securityContext ?? envelope.GetCurrentScope();

      // CRITICAL FIX: When extraction fails (securityContext is null) but envelope has scope,
      // we must wrap the scope in ImmutableScopeContext with ShouldPropagate=true so that
      // CascadeContext.GetSecurityFromAmbient() can find it when receptors return events.
      // GetSecurityFromAmbient() requires ImmutableScopeContext with propagation enabled.
      if (securityContext is null && scopeForContext is not null) {
        var extraction = new SecurityExtraction {
          Scope = scopeForContext.Scope,
          Roles = scopeForContext.Roles,
          Permissions = scopeForContext.Permissions,
          SecurityPrincipals = scopeForContext.SecurityPrincipals,
          Claims = scopeForContext.Claims,
          ActualPrincipal = scopeForContext.ActualPrincipal,
          EffectivePrincipal = scopeForContext.EffectivePrincipal,
          ContextType = scopeForContext.ContextType,
          Source = "EnvelopeHop"
        };
        var immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);
        scopeForContext = immutableScope;

        // Set on accessor so GetSecurityFromAmbient() can find it
        var accessor = _scopedProvider.GetService<IScopeContextAccessor>();
        if (accessor is not null) {
          accessor.Current = immutableScope;
        }

        // Invoke security callbacks so JDNext's UserContextManagerCallback sets TenantContext
        var callbacks = _scopedProvider.GetServices<ISecurityContextCallback>();
        foreach (var callback in callbacks) {
          cancellationToken.ThrowIfCancellationRequested();
          await callback.OnContextEstablishedAsync(immutableScope, envelope, _scopedProvider, cancellationToken)
            .ConfigureAwait(false);
        }
      }

      var messageContext = new MessageContext {
        MessageId = envelope.MessageId,
        CorrelationId = envelope.GetCorrelationId() ?? ValueObjects.CorrelationId.New(),
        CausationId = envelope.GetCausationId() ?? ValueObjects.MessageId.New(),
        Timestamp = envelope.GetMessageTimestamp(),
        UserId = scopeForContext?.Scope?.UserId,
        TenantId = scopeForContext?.Scope?.TenantId,
        ScopeContext = scopeForContext,
        CallerInfo = callerInfo
      };
      messageContextAccessor.Current = messageContext;

      // CRITICAL: Set InitiatingContext on IScopeContextAccessor
      // This establishes IMessageContext as the SOURCE OF TRUTH for security context.
      // AsyncLocal carries a REFERENCE to this IMessageContext, not a copy of its data.
      var scopeContextAccessor = _scopedProvider.GetService<IScopeContextAccessor>();
      if (scopeContextAccessor is not null) {
        scopeContextAccessor.InitiatingContext = messageContext;
      }
    }

    // Registry already has categorized receptors at compile time
    // Just get receptors for this type/stage combination and invoke them
    var receptors = _registry.GetReceptorsFor(messageType, stage);

    if (receptors.Count == 0) {
      // No receptors registered for this message type and stage - this is normal
      // Context is already established above, so just return
      return;
    }

    // Suppress receptors during replay/rebuild unless they opt in with [FireDuringReplay]
    // This prevents duplicate side effects (emails, webhooks, cache busting) when events are replayed
    var processingMode = context?.ProcessingMode;
    if (processingMode is ProcessingMode.Replay or ProcessingMode.Rebuild) {
      var filtered = new List<ReceptorInfo>(receptors.Count);
      for (int i = 0; i < receptors.Count; i++) {
        if (receptors[i].FireDuringReplay) {
          filtered.Add(receptors[i]);
        }
      }
      if (filtered.Count == 0) {
        return;
      }
      receptors = filtered;
    }

    // Try to get stream ID extractor for stream-based sync
    var streamIdExtractor = _scopedProvider.GetService<IStreamIdExtractor>();
    Guid? extractedStreamId = streamIdExtractor?.ExtractStreamId(message, messageType);

    // Extract both trace context and scope from envelope hops
    var extracted = EnvelopeContextExtractor.ExtractFromHops(envelope.Hops);
    var parentContext = extracted.TraceContext;

    // Establish ambient scope context from envelope data (security propagation via AsyncLocal)
    if (extracted.Scope is not null) {
      ScopeContextAccessor.CurrentContext = extracted.Scope;
    }

    foreach (var receptor in receptors) {
      // Start activity for this receptor invocation - enables per-handler tracing
      // Pass parentContext to ensure proper parenting when Activity.Current is null (background threads)
      using var receptorActivity = WhizbangActivitySource.Tracing.StartActivity(
        $"Receptor {receptor.ReceptorId}",
        ActivityKind.Internal,
        parentContext: parentContext);
      receptorActivity?.SetTag("whizbang.receptor.id", receptor.ReceptorId);
      receptorActivity?.SetTag("whizbang.receptor.message_type", messageType.FullName);
      receptorActivity?.SetTag("whizbang.lifecycle.stage", stage.ToString());

      try {
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
                  ? SyncFilter.ForEventTypes([.. syncAttr.EventTypes]).WithTimeout(timeout).Build()
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

        // Set lifecycle context for runtime-registered receptors (IAcceptsLifecycleContext support)
        if (context is not null) {
          var lifecycleContextAccessor = _scopedProvider.GetService<ILifecycleContextAccessor>();
          if (lifecycleContextAccessor is not null) {
            lifecycleContextAccessor.Current = context;
          }
        }

        // Log caller info for debugging dispatch-to-receptor traceability
        if (callerInfo is not null) {
          _logger ??= _scopedProvider.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Messaging.ReceptorInvoker");
          if (_logger is not null) {
            var callerInfoString = callerInfo.ToString();
            Log.ReceptorInvokedFromCaller(_logger, receptor.ReceptorId, callerInfoString);
          }
        }

        // InvokeAsync is a pre-compiled delegate (no reflection)
        // Pass the scoped provider so receptor can be resolved with its dependencies
        var result = await receptor.InvokeAsync(_scopedProvider, message, envelope, callerInfo, cancellationToken).ConfigureAwait(false);

        receptorActivity?.SetStatus(ActivityStatusCode.Ok);
        receptorActivity?.SetTag("whizbang.receptor.has_result", result is not null);

        // Cascade any IMessage instances (events and commands) from the receptor's return value
        // Handles tuples, arrays, Route wrappers via IEventCascader
        // Pass source envelope so cascaded messages can inherit SecurityContext
        // Note: Receptor default routing not passed through - routing is determined by
        // message attributes, Route wrappers, or system default (Outbox)
        if (result is not null && _eventCascader is not null) {
          await _eventCascader.CascadeFromResultAsync(result, sourceEnvelope: envelope, receptorDefault: null, cancellationToken).ConfigureAwait(false);
        }
      } catch (Exception ex) {
        receptorActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        receptorActivity?.SetTag("exception.type", ex.GetType().FullName);
        receptorActivity?.SetTag("exception.message", ex.Message);
        throw;
      }
    }

    // Process message tags after all receptors complete at the current lifecycle stage
    // This enables notification hooks to fire when events are consumed from transport
    // (e.g., BffService consuming AccountCreatedEvent from UserService)
    var tagProcessor = _scopedProvider.GetService<IMessageTagProcessor>();
    if (tagProcessor is not null) {
      // Get scope from message context accessor (established earlier in this method)
      // The scope is already set from security context extraction or envelope hops
      var scopeForTags = messageContextAccessor?.Current?.ScopeContext;
      await tagProcessor.ProcessTagsAsync(message, messageType, stage, scopeForTags, cancellationToken).ConfigureAwait(false);
    }
  }

  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Debug,
      Message = "Invoking receptor {ReceptorId} called from {CallerInfo}")]
    public static partial void ReceptorInvokedFromCaller(ILogger logger, string receptorId, string callerInfo);
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
