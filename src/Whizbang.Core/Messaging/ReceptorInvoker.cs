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

    // Extract payload from envelope, unwrapping Routed<T> wrappers
    var message = _extractMessage(envelope);
    if (message is null) {
      return;
    }

    // GetType() is AOT-safe - returns the runtime type
    var messageType = message.GetType();

    // Establish security context from the envelope BEFORE checking for receptors
    var securityContext = await _establishSecurityContextAsync(envelope, cancellationToken).ConfigureAwait(false);

    // Extract caller info from the first Current hop (captured at dispatch time)
    var callerInfo = _extractCallerInfo(envelope);

    // Set message context from envelope for injectable IMessageContext
    // CRITICAL: This must happen BEFORE early return to ensure InitiatingContext is always set
    var messageContextAccessor = _scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is not null) {
      await _setMessageContextAsync(messageContextAccessor, envelope, securityContext, callerInfo, cancellationToken).ConfigureAwait(false);
    }

    // Registry already has categorized receptors at compile time
    var receptors = _registry.GetReceptorsFor(messageType, stage);

    if (receptors.Count == 0) {
      await _processTagsAsync(message, messageType, stage, messageContextAccessor, cancellationToken).ConfigureAwait(false);
      return;
    }

    // Suppress receptors during replay/rebuild unless they opt in with [FireDuringReplay]
    receptors = _filterForReplayMode(receptors, context);
    if (receptors.Count == 0) {
      return;
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
      await _invokeReceptorAsync(receptor, message, messageType, envelope, stage, context, callerInfo,
        extractedStreamId, parentContext, cancellationToken).ConfigureAwait(false);
    }

    // Process message tags after all receptors complete at the current lifecycle stage
    await _processTagsAsync(message, messageType, stage, messageContextAccessor, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Extracts the payload from an envelope, unwrapping Routed&lt;T&gt; wrappers.
  /// Returns null if the message should be skipped (RoutedNone or null value).
  /// </summary>
  private static object? _extractMessage(IMessageEnvelope envelope) {
    var message = envelope.Payload;

    // Unwrap Routed<T> if the payload contains a routing wrapper
    if (message is Dispatch.IRouted routed) {
      if (routed.Mode == Dispatch.DispatchMode.None || routed.Value == null) {
        return null;
      }
      message = routed.Value;
    }

    return message;
  }

  /// <summary>
  /// Establishes security context from the envelope via the registered security provider.
  /// Sets the scope context accessor if security context is established.
  /// </summary>
  private async ValueTask<IScopeContext?> _establishSecurityContextAsync(
      IMessageEnvelope envelope,
      CancellationToken cancellationToken) {
    var securityProvider = _scopedProvider.GetService<IMessageSecurityContextProvider>();
    if (securityProvider is null) {
      return null;
    }

    var securityContext = await securityProvider
      .EstablishContextAsync(envelope, _scopedProvider, cancellationToken)
      .ConfigureAwait(false);

    if (securityContext is not null) {
      var accessor = _scopedProvider.GetService<IScopeContextAccessor>();
      if (accessor is not null) {
        accessor.Current = securityContext;
      }
    }

    return securityContext;
  }

  /// <summary>
  /// Extracts caller info from the first Current hop in the envelope (captured at dispatch time).
  /// </summary>
  private static CallerInfo? _extractCallerInfo(IMessageEnvelope envelope) {
    if (envelope.Hops is not { Count: > 0 }) {
      return null;
    }

    for (int i = 0; i < envelope.Hops.Count; i++) {
      var hop = envelope.Hops[i];
      if (hop.Type == HopType.Current && hop.CallerMemberName is not null) {
        return new CallerInfo(
            hop.CallerMemberName,
            hop.CallerFilePath ?? string.Empty,
            hop.CallerLineNumber ?? 0);
      }
    }

    return null;
  }

  /// <summary>
  /// Sets message context from envelope for injectable IMessageContext.
  /// Establishes ImmutableScopeContext with propagation when security extraction failed but envelope has scope.
  /// </summary>
  private async ValueTask _setMessageContextAsync(
      IMessageContextAccessor messageContextAccessor,
      IMessageEnvelope envelope,
      IScopeContext? securityContext,
      CallerInfo? callerInfo,
      CancellationToken cancellationToken) {
    IScopeContext? scopeForContext = securityContext ?? envelope.GetCurrentScope();

    // When extraction fails but envelope has scope, wrap in ImmutableScopeContext with propagation
    if (securityContext is null && scopeForContext is not null) {
      scopeForContext = await _promoteScopeWithPropagationAsync(scopeForContext, envelope, cancellationToken).ConfigureAwait(false);
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

    // Set InitiatingContext on IScopeContextAccessor - establishes IMessageContext as SOURCE OF TRUTH
    var scopeContextAccessor = _scopedProvider.GetService<IScopeContextAccessor>();
    if (scopeContextAccessor is not null) {
      scopeContextAccessor.InitiatingContext = messageContext;
    }
  }

  /// <summary>
  /// Wraps an existing scope in ImmutableScopeContext with ShouldPropagate=true so that
  /// CascadeContext.GetSecurityFromAmbient() can find it when receptors return events.
  /// Also invokes security callbacks.
  /// </summary>
  private async ValueTask<IScopeContext> _promoteScopeWithPropagationAsync(
      IScopeContext scopeForContext,
      IMessageEnvelope envelope,
      CancellationToken cancellationToken) {
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

    return immutableScope;
  }

  /// <summary>
  /// Filters receptors during replay/rebuild mode, keeping only those with [FireDuringReplay].
  /// Returns the original list if not in replay/rebuild mode.
  /// </summary>
  private static IReadOnlyList<ReceptorInfo> _filterForReplayMode(
      IReadOnlyList<ReceptorInfo> receptors,
      ILifecycleContext? context) {
    var processingMode = context?.ProcessingMode;
    if (processingMode is not (ProcessingMode.Replay or ProcessingMode.Rebuild)) {
      return receptors;
    }

    var filtered = new List<ReceptorInfo>(receptors.Count);
    for (int i = 0; i < receptors.Count; i++) {
      if (receptors[i].FireDuringReplay) {
        filtered.Add(receptors[i]);
      }
    }
    return filtered;
  }

  /// <summary>
  /// Invokes a single receptor with tracing, sync awaiting, and event cascading.
  /// </summary>
  private async ValueTask _invokeReceptorAsync(
      ReceptorInfo receptor,
      object message,
      Type messageType,
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context,
      CallerInfo? callerInfo,
      Guid? extractedStreamId,
      ActivityContext parentContext,
      CancellationToken cancellationToken) {
    using var receptorActivity = WhizbangActivitySource.Tracing.StartActivity(
      $"Receptor {receptor.ReceptorId}",
      ActivityKind.Internal,
      parentContext: parentContext);
    receptorActivity?.SetTag("whizbang.receptor.id", receptor.ReceptorId);
    receptorActivity?.SetTag("whizbang.receptor.message_type", messageType.FullName);
    receptorActivity?.SetTag("whizbang.lifecycle.stage", stage.ToString());

    try {
      // Await perspective sync if needed
      await _awaitPerspectiveSyncAsync(receptor, extractedStreamId, context, cancellationToken).ConfigureAwait(false);

      // Set lifecycle context for runtime-registered receptors (IAcceptsLifecycleContext support)
      if (context is not null) {
        var lifecycleContextAccessor = _scopedProvider.GetService<ILifecycleContextAccessor>();
        if (lifecycleContextAccessor is not null) {
          lifecycleContextAccessor.Current = context;
        }
      }

      _logCallerInfo(receptor, callerInfo);

      // InvokeAsync is a pre-compiled delegate (no reflection)
      var result = await receptor.InvokeAsync(_scopedProvider, message, envelope, callerInfo, cancellationToken).ConfigureAwait(false);

      receptorActivity?.SetStatus(ActivityStatusCode.Ok);
      receptorActivity?.SetTag("whizbang.receptor.has_result", result is not null);

      // Cascade any IMessage instances from the receptor's return value
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

  /// <summary>
  /// Checks for [AwaitPerspectiveSync] attributes and awaits sync if needed.
  /// </summary>
  private async ValueTask _awaitPerspectiveSyncAsync(
      ReceptorInfo receptor,
      Guid? extractedStreamId,
      ILifecycleContext? context,
      CancellationToken cancellationToken) {
    if (_syncAwaiter is null || receptor.SyncAttributes is not { Count: > 0 }) {
      return;
    }

    foreach (var syncAttr in receptor.SyncAttributes) {
      var timeout = TimeSpan.FromMilliseconds(syncAttr.EffectiveTimeoutMs);
      SyncResult syncResult;

      if (extractedStreamId.HasValue) {
        syncResult = await _awaitStreamSyncAsync(syncAttr, extractedStreamId.Value, timeout, context, cancellationToken).ConfigureAwait(false);
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
    }
  }

  /// <summary>
  /// Awaits stream-based sync and sets SyncContext for receptor access via AsyncLocal.
  /// </summary>
  private async ValueTask<SyncResult> _awaitStreamSyncAsync(
      ReceptorSyncAttributeInfo syncAttr,
      Guid streamId,
      TimeSpan timeout,
      ILifecycleContext? context,
      CancellationToken cancellationToken) {
    var eventTypes = syncAttr.EventTypes?.ToArray();
    // Pass the incoming event's ID for cross-scope sync - this is CRITICAL
    var syncResult = await _syncAwaiter!.WaitForStreamAsync(
        syncAttr.PerspectiveType,
        streamId,
        eventTypes,
        timeout,
        eventIdToAwait: context?.EventId,
        cancellationToken).ConfigureAwait(false);

    // Create and set SyncContext for receptor access via AsyncLocal
    var syncContext = new SyncContext {
      StreamId = streamId,
      PerspectiveType = syncAttr.PerspectiveType,
      Outcome = syncResult.Outcome,
      EventsAwaited = syncResult.EventsAwaited,
      ElapsedTime = syncResult.ElapsedTime,
      FailureReason = syncResult.Outcome == SyncOutcome.TimedOut ? "Timeout exceeded" : null
    };
    SyncContextAccessor.CurrentContext = syncContext;

    return syncResult;
  }

  /// <summary>
  /// Logs caller info for debugging dispatch-to-receptor traceability.
  /// </summary>
  private void _logCallerInfo(ReceptorInfo receptor, CallerInfo? callerInfo) {
    if (callerInfo is null) {
      return;
    }

    _logger ??= _scopedProvider.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Messaging.ReceptorInvoker");
    if (_logger is not null) {
      var callerInfoString = callerInfo.ToString();
      Log.ReceptorInvokedFromCaller(_logger, receptor.ReceptorId, callerInfoString);
    }
  }

  /// <summary>
  /// Processes message tags after all receptors complete at the current lifecycle stage.
  /// </summary>
  private async ValueTask _processTagsAsync(
      object message,
      Type messageType,
      LifecycleStage stage,
      IMessageContextAccessor? messageContextAccessor,
      CancellationToken cancellationToken) {
    var tagProcessor = _scopedProvider.GetService<IMessageTagProcessor>();
    if (tagProcessor is not null) {
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
