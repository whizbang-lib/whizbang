using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
/// <docs>operations/observability/tracing#parent-context</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public sealed partial class ReceptorInvoker : IReceptorInvoker {
  private readonly IReceptorRegistry _registry;
  private readonly IServiceProvider _scopedProvider;
  private readonly IEventCascader? _eventCascader;
  private readonly IPerspectiveSyncAwaiter? _syncAwaiter;
  private readonly HashSet<string> _ownedDomains;
  private readonly string? _serviceName;
  private readonly LifecycleStageTracker? _stageTracker;
  private readonly IReceptorDedupStore? _dedupStore;
  private readonly Configuration.ReceptorInvocationTracking _invocationTracking;
  private readonly Configuration.DoubleFireBehavior _onDoubleFire;
  private readonly IReceptorFiringObserver? _firingObserver;
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

    // Resolve owned domains for lifecycle stage filtering (AOT-safe, no reflection)
    var routingOptions = scopedProvider.GetService<Microsoft.Extensions.Options.IOptions<Routing.RoutingOptions>>()?.Value;
    _ownedDomains = routingOptions?.OwnedDomains?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

    // Resolve service name for source-service filtering (PostInbox: only fire for other services)
    _serviceName = scopedProvider.GetService<Observability.IServiceInstanceProvider>()?.ServiceName;

    // Resolve lifecycle stage tracker for cross-worker dedup (singleton)
    _stageTracker = scopedProvider.GetService<LifecycleStageTracker>();

    // Resolve the receptor dedup store (per-message per-receptor guardrail).
    // Default registration is EnvelopeReceptorDedupStore via AddWhizbangReceptorRegistry.
    // A consumer may replace it with a DB-backed impl. Null means the guardrail is disabled.
    _dedupStore = scopedProvider.GetService<IReceptorDedupStore>();

    // Resolve guardrail options. Defaults: TrackAndEnforce, Warn on double-fire.
    var whizbangOptions = scopedProvider.GetService<Microsoft.Extensions.Options.IOptions<Configuration.WhizbangOptions>>()?.Value;
    var guardrails = whizbangOptions?.Guardrails ?? new Configuration.WhizbangGuardrailsOptions();
    _invocationTracking = guardrails.ReceptorInvocationTracking;
    _onDoubleFire = guardrails.OnDoubleFire;

    // Optional test-only observer. Null in production when nothing is registered.
    _firingObserver = scopedProvider.GetService<IReceptorFiringObserver>();
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
      var establishedContext = await _setMessageContextAsync(messageContextAccessor, envelope, securityContext, callerInfo, cancellationToken).ConfigureAwait(false);
      // Establish-side symmetry (the boundary fix): the InitiatingContext (correlation/causation) written INSIDE
      // the awaited _setMessageContextAsync is lost across its ConfigureAwait(false) boundary — AsyncLocal flows
      // parent→child, not child→parent. Re-set both accessors SYNCHRONOUSLY here, on the same flow that reaches
      // the receptor body (exactly how ScopeContextAccessor.CurrentContext scope survives just below). Without
      // this, a worker-dispatched receptor's PublishAsync read a null InitiatingContext and its cascaded child
      // minted a fresh correlation while scope survived — the asymmetric boundary bug.
      messageContextAccessor.Current = establishedContext;
      var initiatingAccessor = _scopedProvider.GetService<IScopeContextAccessor>();
      if (initiatingAccessor is not null) {
        initiatingAccessor.InitiatingContext = establishedContext;
      }
    }

    // Extract both trace context and scope from envelope hops.
    // MUST happen before the early-return for receptors.Count == 0 so that tag hooks
    // at terminal stages (PostAllPerspectivesDetached, PostLifecycleDetached, etc.) — which have
    // no registered receptors — still receive ambient scope via AsyncLocal.
    var extracted = EnvelopeContextExtractor.ExtractFromHops(envelope.Hops);
    var parentContext = extracted.TraceContext;

    // Establish ambient scope context from envelope data (security propagation via AsyncLocal)
    if (extracted.Scope is not null) {
      ScopeContextAccessor.CurrentContext = extracted.Scope;
    }

    // Registry already has categorized receptors at compile time
    var receptors = _registry.GetReceptorsFor(messageType, stage);

    // Resolve scope for tag processing — use security context or extracted scope from hops
    var scopeForTags = securityContext ?? extracted.Scope;

    var isPreOutbox = stage == LifecycleStage.PreOutboxInline || stage == LifecycleStage.PreOutboxDetached;
    var isPostInbox = stage == LifecycleStage.PostInboxInline || stage == LifecycleStage.PostInboxDetached;

    if (_shouldSkipSameServicePostInbox(envelope, messageType, stage, receptors, isPostInbox)
        || _shouldSkipOwnedDomainFilter(envelope, message, messageType, stage, receptors, isPreOutbox)
        || _shouldSkipLocalDispatchPreOutbox(envelope, messageType, stage, receptors, isPreOutbox)) {
      return;
    }

    if (receptors.Count == 0) {
      await _handleNoReceptorsRegisteredAsync(message, messageType, stage, envelope, scopeForTags, cancellationToken).ConfigureAwait(false);
      return;
    }

    if (!_tryClaimStageTracker(envelope, messageType, stage, context)) {
      return;
    }

    receptors = _filterForReplayMode(receptors, context);
    if (receptors.Count == 0) {
      _logSkippedReplayModeFilter(envelope, messageType, stage);
      return;
    }

    // Try to get stream ID extractor for stream-based sync
    var streamIdExtractor = _scopedProvider.GetService<IStreamIdExtractor>();
    Guid? extractedStreamId = streamIdExtractor?.ExtractStreamId(message, messageType);

    var invocationCtx = new ReceptorInvocationContext(message, messageType, envelope, stage, context, callerInfo, extractedStreamId, parentContext);
    foreach (var receptor in receptors) {
      await _invokeReceptorAsync(receptor, invocationCtx, cancellationToken).ConfigureAwait(false);
    }

    // Process message tags after all receptors complete at the current lifecycle stage
    await _processTagsAsync(message, messageType, stage, scopeForTags, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Source-service filtering: PostInbox only fires for messages from OTHER services. Messages
  /// from THIS service already fired at LocalImmediate — returning true here skips PostInbox to
  /// prevent double-fire.
  /// </summary>
  private bool _shouldSkipSameServicePostInbox(
      IMessageEnvelope envelope,
      Type messageType,
      LifecycleStage stage,
      IReadOnlyList<ReceptorInfo> receptors,
      bool isPostInbox) {
    if (!isPostInbox || _serviceName is null || receptors.Count == 0) {
      return false;
    }
    var sourceService = envelope.Hops.Count > 0 ? envelope.Hops[^1].ServiceInstance.ServiceName : null;
    if (!string.Equals(sourceService, _serviceName, StringComparison.OrdinalIgnoreCase)) {
      return false;
    }
    _ensureLogger();
    if (_logger is not null) {
      Log.SkippedSameServicePostInbox(_logger, stage, messageType.Name, envelope.MessageId.Value, _serviceName);
    }
    return true;
  }

  /// <summary>
  /// PreOutbox ownership filtering (AOT-safe, no reflection). Skips owned commands and
  /// non-owned events at the outbox stage so domain-owned commands don't re-fire as events
  /// and non-owned events aren't pushed back through their originating outbox.
  /// </summary>
  private bool _shouldSkipOwnedDomainFilter(
      IMessageEnvelope envelope,
      object message,
      Type messageType,
      LifecycleStage stage,
      IReadOnlyList<ReceptorInfo> receptors,
      bool isPreOutbox) {
    if (_ownedDomains.Count == 0 || receptors.Count == 0 || !isPreOutbox) {
      return false;
    }
    var isOwned = _isOwnedNamespace(messageType.Namespace);
    var isEvent = message is IEvent;
    if (isOwned ? isEvent : !isEvent) {
      return false;
    }
    _ensureLogger();
    if (_logger is not null) {
      Log.SkippedOwnedDomainFilter(_logger, stage, messageType.Name, envelope.MessageId.Value);
    }
    return true;
  }

  /// <summary>
  /// Double-fire prevention: when the envelope was dispatched with the LocalDispatch flag, the
  /// handler already fired at LocalImmediate — returning true here skips PreOutbox. Only applies
  /// when owned domains are configured (preserves backward compat).
  /// </summary>
  private bool _shouldSkipLocalDispatchPreOutbox(
      IMessageEnvelope envelope,
      Type messageType,
      LifecycleStage stage,
      IReadOnlyList<ReceptorInfo> receptors,
      bool isPreOutbox) {
    if (_ownedDomains.Count == 0 || !isPreOutbox || receptors.Count == 0
        || !envelope.DispatchContext.Mode.HasFlag(Dispatch.DispatchModes.LocalDispatch)) {
      return false;
    }
    _ensureLogger();
    if (_logger is not null) {
      Log.SkippedLocalDispatchPreOutbox(_logger, stage, messageType.Name, envelope.MessageId.Value);
    }
    return true;
  }

  /// <summary>
  /// Handles the "no receptors registered" branch: logs the skip and still fires message tags
  /// so that terminal-stage tag hooks (PostAllPerspectivesDetached, PostLifecycleDetached) run.
  /// </summary>
  private async ValueTask _handleNoReceptorsRegisteredAsync(
      object message,
      Type messageType,
      LifecycleStage stage,
      IMessageEnvelope envelope,
      IScopeContext? scopeForTags,
      CancellationToken cancellationToken) {
    _ensureLogger();
    if (_logger is not null) {
      Log.NoReceptorsRegistered(_logger, stage, messageType.Name, envelope.MessageId.Value);
    }
    await _processTagsAsync(message, messageType, stage, scopeForTags, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Cross-worker dedup: prevents the same message+stage from being processed twice (e.g.
  /// TransportConsumerWorker and WorkCoordinatorPublisherWorker both firing PostInbox). For
  /// perspective-scoped stages the dedup key also includes context.PerspectiveType so each of
  /// N perspectives gets its own claim. Returns false when the stage was already claimed so the
  /// caller can short-circuit.
  /// </summary>
  private bool _tryClaimStageTracker(
      IMessageEnvelope envelope,
      Type messageType,
      LifecycleStage stage,
      ILifecycleContext? context) {
    if (_stageTracker is null) {
      return true;
    }
    if (_stageTracker.TryClaim(envelope.MessageId.Value, stage, context?.PerspectiveType)) {
      return true;
    }
    _ensureLogger();
    if (_logger is not null) {
      Log.SkippedStageTrackerDedup(_logger, stage, messageType.Name, envelope.MessageId.Value);
    }
    return false;
  }

  /// <summary>Logs that replay-mode filtering removed every remaining receptor.</summary>
  private void _logSkippedReplayModeFilter(IMessageEnvelope envelope, Type messageType, LifecycleStage stage) {
    _ensureLogger();
    if (_logger is not null) {
      Log.SkippedReplayModeFilter(_logger, stage, messageType.Name, envelope.MessageId.Value);
    }
  }

  /// <summary>
  /// Extracts the payload from an envelope, unwrapping Routed&lt;T&gt; wrappers.
  /// Returns null if the message should be skipped (RoutedNone or null value).
  /// </summary>
  private static object? _extractMessage(IMessageEnvelope envelope) {
    var message = envelope.Payload;

    // Unwrap Routed<T> if the payload contains a routing wrapper
    if (message is Dispatch.IRouted routed) {
      if (routed.Mode == Dispatch.DispatchModes.None || routed.Value == null) {
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
  private async ValueTask<MessageContext> _setMessageContextAsync(
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

    // Single source of truth for correlation propagation: hop → ambient parent (rescue) → fresh root.
    var (correlation, causation) = Observability.CascadeContext.ResolveInheritedIdentity(envelope);
    var messageContext = new MessageContext {
      MessageId = envelope.MessageId,
      CorrelationId = correlation,
      CausationId = causation,
      Timestamp = envelope.GetMessageTimestamp(),
      UserId = scopeForContext?.Scope?.UserId,
      TenantId = scopeForContext?.Scope?.TenantId,
      ScopeContext = scopeForContext,
      CallerInfo = callerInfo
    };
    messageContextAccessor.Current = messageContext;

    // Set InitiatingContext on IScopeContextAccessor - establishes IMessageContext as SOURCE OF TRUTH.
    // NOTE: these AsyncLocal writes are made inside this awaited method, so they do NOT flow back to the
    // synchronous caller (InvokeAsync) across its ConfigureAwait(false) boundary — the caller re-establishes
    // the returned context synchronously so it reaches the receptor body. See InvokeAsync.
    var scopeContextAccessor = _scopedProvider.GetService<IScopeContextAccessor>();
    if (scopeContextAccessor is not null) {
      scopeContextAccessor.InitiatingContext = messageContext;
    }

    return messageContext;
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

    // Invoke security callbacks so a consumer application's UserContextManagerCallback sets TenantContext
    var callbacks = _scopedProvider.GetServices<ISecurityContextCallback>();
    foreach (var callback in callbacks) {
      cancellationToken.ThrowIfCancellationRequested();
      await callback.OnContextEstablishedAsync(immutableScope, envelope, _scopedProvider, cancellationToken)
        .ConfigureAwait(false);
    }

    return immutableScope;
  }

  /// <summary>
  /// Filters receptors during Replay/Rebuild based on per-event idempotency semantics.
  /// <list type="bullet">
  ///   <item>Live mode → all receptors pass through.</item>
  ///   <item>Replay/Rebuild + <see cref="ILifecycleContext.IsNewEvent"/> true → all receptors
  ///   fire (this event has never been processed before).</item>
  ///   <item>Replay/Rebuild + <c>IsNewEvent</c> false → only receptors declared fully
  ///   idempotent via <c>[ReceptorIdempotent(AlwaysFire = true)]</c> fire
  ///   (<see cref="ReceptorInfo.FireDuringReplay"/>).</item>
  /// </list>
  /// </summary>
  private static IReadOnlyList<ReceptorInfo> _filterForReplayMode(
      IReadOnlyList<ReceptorInfo> receptors,
      ILifecycleContext? context) {
    var processingMode = context?.ProcessingMode;
    if (processingMode is not (ProcessingMode.Replay or ProcessingMode.Rebuild)) {
      return receptors;
    }

    // In Replay/Rebuild, new events still fire all receptors — they have never had their
    // lifecycle invoked before, so there is nothing to be idempotent about. Only
    // already-processed events need to filter down to AlwaysFire receptors.
    if (context?.IsNewEvent == true) {
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
  /// Groups parameters for a single receptor invocation to reduce parameter count.
  /// </summary>
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Positional record whose purpose is to group invocation parameters — it is itself the fix for S107 on methods that would otherwise take these 8 values individually.")]
  private readonly record struct ReceptorInvocationContext(
    object Message,
    Type MessageType,
    IMessageEnvelope Envelope,
    LifecycleStage Stage,
    ILifecycleContext? LifecycleContext,
    CallerInfo? CallerInfo,
    Guid? ExtractedStreamId,
    ActivityContext ParentContext);

  /// <summary>
  /// Invokes a single receptor with tracing, sync awaiting, and event cascading.
  /// </summary>
  /// <remarks>
  /// Emits paired structured Debug logs (EventIds 16/17) bracketing the actual receptor
  /// dispatch so that an operator can observe firing counts per <c>(ReceptorId, MessageId, Stage)</c>
  /// in Aspire or file sinks. The post-invocation log runs from a <c>finally</c> block so
  /// exceptions are still reported with <c>IsError=true</c> plus the exception type.
  /// </remarks>
  /// <docs>operations/observability/receptor-logging</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerLoggingTests.cs</tests>
  private async ValueTask _invokeReceptorAsync(
      ReceptorInfo receptor,
      ReceptorInvocationContext ctx,
      CancellationToken cancellationToken) {
    using var receptorActivity = WhizbangActivitySource.Tracing.StartActivity(
      $"Receptor {receptor.ReceptorId}",
      ActivityKind.Internal,
      parentContext: ctx.ParentContext);
    receptorActivity?.SetTag("whizbang.receptor.id", receptor.ReceptorId);
    receptorActivity?.SetTag("whizbang.receptor.message_type", ctx.MessageType.FullName);
    receptorActivity?.SetTag("whizbang.lifecycle.stage", ctx.Stage.ToString());

    // Pre-resolve log fields once per receptor invocation so both the "firing" and "fired" lines
    // share the same identity tuple. The envelope is single-threaded per message, so these values
    // do not change between the two log calls.
    var messageId = ctx.Envelope.MessageId.Value;
    var streamId = ctx.ExtractedStreamId ?? Guid.Empty;
    var messageTypeName = ctx.MessageType.FullName ?? ctx.MessageType.Name;
    Guid correlationId = Guid.Empty;
    string sourceService = string.Empty;
    if (ctx.Envelope.Hops is { Count: > 0 } hops) {
      correlationId = hops[0].CorrelationId?.Value ?? Guid.Empty;
      sourceService = hops[^1].ServiceInstance.ServiceName ?? string.Empty;
    }

    _ensureLogger();

    if (await _isDoubleFireAndSkipOrThrowAsync(receptor, ctx, messageId, streamId, sourceService, cancellationToken).ConfigureAwait(false)) {
      return;
    }

    if (_logger is not null) {
      Log.ReceptorFiring(_logger, receptor.ReceptorId, ctx.Stage, messageId, streamId, messageTypeName, correlationId, sourceService);
    }
    if (_firingObserver is not null) {
      await _firingObserver.OnReceptorFiringAsync(receptor.ReceptorId, ctx.Stage, messageId, ctx.Envelope, cancellationToken).ConfigureAwait(false);
    }
    var stopwatch = Stopwatch.StartNew();
    bool isError = false;
    string? exceptionTypeName = null;
    Exception? capturedException = null;

    try {
      await _invokeReceptorBodyAsync(receptor, ctx, receptorActivity, stopwatch, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) {
      isError = true;
      exceptionTypeName = ex.GetType().FullName;
      capturedException = ex;
      receptorActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      receptorActivity?.SetTag("exception.type", exceptionTypeName);
      receptorActivity?.SetTag("exception.message", ex.Message);
      throw;
    } finally {
      stopwatch.Stop();
      var fireFields = new ReceptorFireLogFields(
        messageId,
        streamId,
        messageTypeName,
        correlationId,
        sourceService,
        stopwatch,
        isError,
        exceptionTypeName,
        capturedException);
      await _notifyReceptorFiredAsync(receptor, ctx, fireFields, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Double-fire guardrail: consults the dedup store for a prior invocation of this receptor
  /// against this envelope. Per-receptor (not per-stage), so a filter bug that lets the same
  /// receptor fire at both LocalImmediateInline AND PreOutboxInline is caught here. Receptors
  /// declared <c>[ReceptorIdempotent]</c> bypass — they've opted in to re-firing. Perspective-
  /// scoped stages are exempt because the SAME receptor legitimately fires once per perspective
  /// per event. Returns true when the caller should skip the invocation; throws
  /// <see cref="DuplicateReceptorFireException"/> when configured behaviour is Throw.
  /// </summary>
  private async ValueTask<bool> _isDoubleFireAndSkipOrThrowAsync(
      ReceptorInfo receptor,
      ReceptorInvocationContext ctx,
      Guid messageId,
      Guid streamId,
      string sourceService,
      CancellationToken cancellationToken) {
    if (_invocationTracking != Configuration.ReceptorInvocationTracking.TrackAndEnforce
        || _dedupStore is null
        || receptor.IsIdempotent) {
      return false;
    }
    var currentIsPerspectiveScoped = ctx.LifecycleContext?.PerspectiveType is not null
      || ctx.Stage is LifecycleStage.PrePerspectiveInline
                   or LifecycleStage.PrePerspectiveDetached
                   or LifecycleStage.PostPerspectiveInline
                   or LifecycleStage.PostPerspectiveDetached;
    if (currentIsPerspectiveScoped) {
      return false;
    }
    var prior = await _dedupStore.TryGetPriorInvocationAsync(ctx.Envelope, receptor.ReceptorId, cancellationToken).ConfigureAwait(false);
    if (prior is null) {
      return false;
    }
    if (_onDoubleFire == Configuration.DoubleFireBehavior.Throw) {
      throw new DuplicateReceptorFireException(
        receptorId: receptor.ReceptorId,
        currentStage: ctx.Stage,
        priorStage: prior.Stage,
        messageId: messageId,
        priorInvocation: prior);
    }
    if (_logger is not null) {
      Log.ReceptorAlreadyFiredSkip(
        _logger,
        receptor.ReceptorId,
        ctx.Stage,
        prior.Stage,
        messageId,
        streamId,
        sourceService,
        prior.CompletedAt);
    }
    return true;
  }

  /// <summary>
  /// Runs the receptor body: establishes AwaitPerspectiveSync context, sets ambient lifecycle
  /// context, logs caller info, dispatches to the compiled receptor delegate, cascades any
  /// returned messages, and records the invocation in the dedup store on success. Exceptions
  /// propagate so the outer catch can flag the activity and rethrow.
  /// </summary>
  private async ValueTask _invokeReceptorBodyAsync(
      ReceptorInfo receptor,
      ReceptorInvocationContext ctx,
      Activity? receptorActivity,
      Stopwatch stopwatch,
      CancellationToken cancellationToken) {
    // Await perspective sync if needed — returns SyncContext to set in THIS execution context
    // (AsyncLocal values set inside child async methods don't flow back to the parent).
    var syncContext = await _awaitPerspectiveSyncAsync(receptor, ctx.ExtractedStreamId, ctx.LifecycleContext, cancellationToken).ConfigureAwait(false);
    if (syncContext is not null) {
      SyncContextAccessor.CurrentContext = syncContext;
    }

    _setAmbientLifecycleContext(ctx.LifecycleContext);
    _logCallerInfo(receptor, ctx.CallerInfo);

    // InvokeAsync is a pre-compiled delegate (no reflection)
    var result = await receptor.InvokeAsync(_scopedProvider, ctx.Message, ctx.Envelope, ctx.CallerInfo, cancellationToken).ConfigureAwait(false);

    receptorActivity?.SetStatus(ActivityStatusCode.Ok);
    receptorActivity?.SetTag("whizbang.receptor.has_result", result is not null);

    if (result is not null && _eventCascader is not null) {
      await _eventCascader.CascadeFromResultAsync(result, sourceEnvelope: ctx.Envelope, receptorDefault: null, cancellationToken).ConfigureAwait(false);
    }

    // Record the invocation on success. RecordInvocationAsync is the only mutation of the
    // envelope's ReceptorInvocations list, so on exception (caught by caller) nothing is
    // recorded and a retry can re-fire cleanly.
    if (_invocationTracking != Configuration.ReceptorInvocationTracking.Off && _dedupStore is not null) {
      var record = new ReceptorInvocationRecord {
        ReceptorId = receptor.ReceptorId,
        Stage = ctx.Stage,
        CompletedAt = DateTimeOffset.UtcNow,
        Duration = stopwatch.Elapsed,
        ServiceName = _serviceName ?? string.Empty
      };
      await _dedupStore.RecordInvocationAsync(ctx.Envelope, record, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>Sets the ambient lifecycle context so runtime-registered receptors
  /// (IAcceptsLifecycleContext) can resolve it via the accessor.</summary>
  private void _setAmbientLifecycleContext(ILifecycleContext? lifecycleContext) {
    if (lifecycleContext is null) {
      return;
    }
    var lifecycleContextAccessor = _scopedProvider.GetService<ILifecycleContextAccessor>();
    if (lifecycleContextAccessor is not null) {
      lifecycleContextAccessor.Current = lifecycleContext;
    }
  }

  /// <summary>
  /// Groups the pre-resolved log identity and outcome fields that bracket a receptor invocation,
  /// so the paired Firing/Fired log lines share the same tuple without a long parameter list.
  /// </summary>
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Positional record whose purpose is to group the fields of a single receptor fire log emission — it is itself the fix for S107 on the notify-fired helper.")]
  private readonly record struct ReceptorFireLogFields(
    Guid MessageId,
    Guid StreamId,
    string MessageTypeName,
    Guid CorrelationId,
    string SourceService,
    Stopwatch Stopwatch,
    bool IsError,
    string? ExceptionTypeName,
    Exception? CapturedException);

  /// <summary>Paired "fired" log + observer notification from the finally block. The observer's
  /// exception rides out with whatever is already propagating.</summary>
  private async ValueTask _notifyReceptorFiredAsync(
      ReceptorInfo receptor,
      ReceptorInvocationContext ctx,
      ReceptorFireLogFields fields,
      CancellationToken cancellationToken) {
    if (_logger is not null) {
      Log.ReceptorFired(_logger, receptor.ReceptorId, ctx.Stage, fields.MessageId, fields.StreamId, fields.MessageTypeName, fields.CorrelationId, fields.SourceService, fields.Stopwatch.ElapsedMilliseconds, fields.IsError, fields.ExceptionTypeName);
    }
    if (_firingObserver is not null) {
      await _firingObserver.OnReceptorFiredAsync(
        receptor.ReceptorId,
        ctx.Stage,
        fields.MessageId,
        ctx.Envelope,
        fields.Stopwatch.Elapsed,
        fields.CapturedException,
        cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Checks for [AwaitPerspectiveSync] attributes and awaits sync if needed.
  /// Returns the last SyncContext so the caller can set it on the ambient AsyncLocal
  /// (AsyncLocal values set inside child async methods don't flow back to the parent).
  /// </summary>
  private async ValueTask<SyncContext?> _awaitPerspectiveSyncAsync(
      ReceptorInfo receptor,
      Guid? extractedStreamId,
      ILifecycleContext? context,
      CancellationToken cancellationToken) {
    if (_syncAwaiter is null || receptor.SyncAttributes is not { Count: > 0 }) {
      return null;
    }

    SyncContext? lastSyncContext = null;
    foreach (var syncAttr in receptor.SyncAttributes) {
      var timeout = TimeSpan.FromMilliseconds(syncAttr.EffectiveTimeoutMs);
      SyncResult syncResult;

      if (extractedStreamId.HasValue) {
        (syncResult, lastSyncContext) = await _awaitStreamSyncAsync(syncAttr, extractedStreamId.Value, timeout, context, cancellationToken).ConfigureAwait(false);
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

    return lastSyncContext;
  }

  /// <summary>
  /// Awaits stream-based sync and creates SyncContext for receptor access.
  /// Returns both the SyncResult and the SyncContext so the caller can set it
  /// on the ambient AsyncLocal in the correct execution context.
  /// </summary>
  private async ValueTask<(SyncResult Result, SyncContext Context)> _awaitStreamSyncAsync(
      ReceptorSyncAttributeInfo syncAttr,
      Guid streamId,
      TimeSpan timeout,
      ILifecycleContext? context,
      CancellationToken cancellationToken) {
    var eventTypes = syncAttr.EventTypes?.ToArray();

    // When EventTypes is explicitly specified, the events being waited for may differ
    // from the current message (cross-scope sync). In that case, we must NOT pass
    // context.EventId as eventIdToAwait — it would be the current message's ID (e.g.,
    // a command ID), not the ID of the tracked event. This would cause Priority 1 in
    // _resolveExpectedEventIds to return the wrong ID, bypassing the correct
    // stream-based lookup (Priority 2) and returning immediately without waiting.
    var eventIdToAwait = eventTypes is { Length: > 0 } ? null : context?.EventId;

    var syncResult = await _syncAwaiter!.WaitForStreamAsync(
        syncAttr.PerspectiveType,
        streamId,
        eventTypes,
        timeout,
        eventIdToAwait: eventIdToAwait,
        cancellationToken).ConfigureAwait(false);

    // Create SyncContext - caller sets it on AsyncLocal to ensure it flows to receptor
    var syncContext = new SyncContext {
      StreamId = streamId,
      PerspectiveType = syncAttr.PerspectiveType,
      Outcome = syncResult.Outcome,
      EventsAwaited = syncResult.EventsAwaited,
      ElapsedTime = syncResult.ElapsedTime,
      FailureReason = syncResult.Outcome == SyncOutcome.TimedOut ? "Timeout exceeded" : null
    };

    return (syncResult, syncContext);
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
  /// Tags (signaltags, UI notification pushes, metric emitters, etc.) fire-and-forget so
  /// slow tag handlers cannot throttle pipeline throughput — a slow SignalR push or HTTP
  /// notification must not gate perspective progress. Exceptions thrown by tag handlers are
  /// observed asynchronously and logged via <see cref="Log.TagProcessingError"/>; they do not
  /// propagate to the caller. The tag processor itself is a singleton (see
  /// <c>ServiceCollectionExtensions</c>) and creates its own DI scope internally, so scope
  /// lifetime is safe across the fire-and-forget boundary.
  /// </summary>
  /// <remarks>
  /// <b>Semantic change (2026-04-23):</b> tags were previously awaited, which coupled
  /// pipeline throughput to external notification latency. Tags must therefore be used only
  /// for <em>side-effects</em> (notifications, metrics, audit emits) — never for validation
  /// or gating. Receptors remain the correct place for side effects that must complete
  /// before the next stage fires.
  /// </remarks>
  private ValueTask _processTagsAsync(
      object message,
      Type messageType,
      LifecycleStage stage,
      IScopeContext? scope,
      CancellationToken cancellationToken) {
    var tagProcessor = _scopedProvider.GetService<IMessageTagProcessor>();
    if (tagProcessor is null) {
      return ValueTask.CompletedTask;
    }

    var task = tagProcessor.ProcessTagsAsync(message, messageType, stage, scope, cancellationToken);

    // Fast-path: if ProcessTagsAsync completed synchronously (common case: no tags
    // registered for this message type), no background observation is needed.
    if (task.IsCompletedSuccessfully) {
      return ValueTask.CompletedTask;
    }

    _ = _observeTagProcessingAsync(task.AsTask(), messageType, stage);
    return ValueTask.CompletedTask;
  }

  private async Task _observeTagProcessingAsync(Task task, Type messageType, LifecycleStage stage) {
    try {
      await task.ConfigureAwait(false);
    } catch (OperationCanceledException) {
      // Expected on shutdown — tag processor received the cancellation token and bailed.
    } catch (Exception ex) {
      _ensureLogger();
      if (_logger is not null) {
        Log.TagProcessingError(_logger, ex, messageType.Name, stage);
      }
    }
  }

  /// <summary>
  /// Checks whether a namespace belongs to this service's owned domains.
  /// Uses hierarchical matching: exact match or child namespace (prefix with '.' separator).
  /// AOT-safe — string comparison only, no reflection.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#owned-domain-routing</docs>
  private bool _isOwnedNamespace(string? ns) {
    if (string.IsNullOrEmpty(ns) || _ownedDomains.Count == 0) {
      return false;
    }
    if (_ownedDomains.Contains(ns)) {
      return true;
    }
    foreach (var owned in _ownedDomains) {
      var prefix = owned.EndsWith('.') ? owned : owned + ".";
      if (ns.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }
    return false;
  }

  private void _ensureLogger() {
    _logger ??= _scopedProvider.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Messaging.ReceptorInvoker");
  }

  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Debug,
      Message = "Invoking receptor {ReceptorId} called from {CallerInfo}")]
    public static partial void ReceptorInvokedFromCaller(ILogger logger, string receptorId, string callerInfo);

    [LoggerMessage(
      EventId = 10,
      Level = LogLevel.Debug,
      Message = "[ReceptorInvoker] Skipped {Stage} for {MessageType} ({MessageId}): same-service PostInbox (source={ServiceName})")]
    public static partial void SkippedSameServicePostInbox(ILogger logger, LifecycleStage stage, string messageType, Guid messageId, string serviceName);

    [LoggerMessage(
      EventId = 11,
      Level = LogLevel.Debug,
      Message = "[ReceptorInvoker] Skipped {Stage} for {MessageType} ({MessageId}): owned-domain namespace filter")]
    public static partial void SkippedOwnedDomainFilter(ILogger logger, LifecycleStage stage, string messageType, Guid messageId);

    [LoggerMessage(
      EventId = 12,
      Level = LogLevel.Debug,
      Message = "[ReceptorInvoker] Skipped {Stage} for {MessageType} ({MessageId}): LocalDispatch flag at PreOutbox")]
    public static partial void SkippedLocalDispatchPreOutbox(ILogger logger, LifecycleStage stage, string messageType, Guid messageId);

    [LoggerMessage(
      EventId = 13,
      Level = LogLevel.Debug,
      Message = "[ReceptorInvoker] Skipped {Stage} for {MessageType} ({MessageId}): LifecycleStageTracker dedup (already claimed)")]
    public static partial void SkippedStageTrackerDedup(ILogger logger, LifecycleStage stage, string messageType, Guid messageId);

    [LoggerMessage(
      EventId = 14,
      Level = LogLevel.Debug,
      Message = "[ReceptorInvoker] Skipped {Stage} for {MessageType} ({MessageId}): all receptors filtered by replay mode")]
    public static partial void SkippedReplayModeFilter(ILogger logger, LifecycleStage stage, string messageType, Guid messageId);

    [LoggerMessage(
      EventId = 15,
      Level = LogLevel.Debug,
      Message = "[ReceptorInvoker] No receptors registered for {Stage} / {MessageType} ({MessageId})")]
    public static partial void NoReceptorsRegistered(ILogger logger, LifecycleStage stage, string messageType, Guid messageId);

    [LoggerMessage(
      EventId = 16,
      Level = LogLevel.Debug,
      Message = "[ReceptorInvoker] Firing {ReceptorId} at {Stage} for {MessageType} (MessageId={MessageId}, StreamId={StreamId}, CorrelationId={CorrelationId}, SourceService={SourceService})")]
    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage source-generated method — parameter list mirrors the structured log template placeholders and cannot be grouped without losing structured-logging semantics.")]
    public static partial void ReceptorFiring(
      ILogger logger,
      string receptorId,
      LifecycleStage stage,
      Guid messageId,
      Guid streamId,
      string messageType,
      Guid correlationId,
      string sourceService);

    [LoggerMessage(
      EventId = 17,
      Level = LogLevel.Debug,
      Message = "[ReceptorInvoker] Fired {ReceptorId} at {Stage} for {MessageType} in {ElapsedMs}ms (MessageId={MessageId}, StreamId={StreamId}, CorrelationId={CorrelationId}, SourceService={SourceService}, IsError={IsError}, ExceptionType={ExceptionType})")]
    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage source-generated method — parameter list mirrors the structured log template placeholders and cannot be grouped without losing structured-logging semantics.")]
    public static partial void ReceptorFired(
      ILogger logger,
      string receptorId,
      LifecycleStage stage,
      Guid messageId,
      Guid streamId,
      string messageType,
      Guid correlationId,
      string sourceService,
      long elapsedMs,
      bool isError,
      string? exceptionType);

    [LoggerMessage(
      EventId = 18,
      Level = LogLevel.Warning,
      Message = "[ReceptorInvoker] Receptor {ReceptorId} already fired at {PriorStage}, skipping duplicate attempt at {CurrentStage} (MessageId={MessageId}, StreamId={StreamId}, SourceService={SourceService}, PriorCompletedAt={PriorCompletedAt})")]
    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage source-generated method — parameter list mirrors the structured log template placeholders and cannot be grouped without losing structured-logging semantics.")]
    public static partial void ReceptorAlreadyFiredSkip(
      ILogger logger,
      string receptorId,
      LifecycleStage currentStage,
      LifecycleStage priorStage,
      Guid messageId,
      Guid streamId,
      string sourceService,
      DateTimeOffset priorCompletedAt);

    [LoggerMessage(
      EventId = 19,
      Level = LogLevel.Error,
      Message = "[ReceptorInvoker] Fire-and-forget tag processing faulted for {MessageType} at {Stage}. Tags are side-effects and do not gate pipeline throughput; check the tag handler for a bug but pipeline continues.")]
    public static partial void TagProcessingError(
      ILogger logger,
      Exception exception,
      string messageType,
      LifecycleStage stage);
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
