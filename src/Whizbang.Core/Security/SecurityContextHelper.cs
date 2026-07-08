using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Security;

/// <summary>
/// Helper methods for establishing security context in message processing pipelines.
/// Consolidates duplicate code from ReceptorInvoker, PerspectiveWorker, ServiceBusConsumerWorker, and Dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// This helper provides consistent security context establishment across all message processing paths:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="EstablishScopeContextAsync"/>: Sets IScopeContextAccessor.Current from envelope</description></item>
/// <item><description><see cref="SetMessageContextFromEnvelope"/>: Sets IMessageContextAccessor.Current from envelope</description></item>
/// <item><description><see cref="EstablishFullContextAsync"/>: Does both operations for complete context establishment</description></item>
/// <item><description><see cref="EstablishMessageContextForCascade"/>: For cascade paths where no envelope is available</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/security/message-security#security-context-helper</docs>
/// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs</tests>
public static partial class SecurityContextHelper {
  /// <summary>
  /// Establishes security context from envelope using IMessageSecurityContextProvider.
  /// Sets IScopeContextAccessor.Current with the established context.
  /// </summary>
  /// <param name="envelope">Message envelope with security metadata in hops</param>
  /// <param name="scopedProvider">Scoped service provider for this message</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The established scope context, or null if no context could be established</returns>
  /// <remarks>
  /// <para>
  /// Use this when you only need IScopeContextAccessor set (e.g., ServiceBusConsumerWorker)
  /// but don't need IMessageContextAccessor.
  /// </para>
  /// </remarks>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:EstablishScopeContextAsync_WithProvider_SetsAccessorCurrentAsync</tests>
  public static async ValueTask<IScopeContext?> EstablishScopeContextAsync(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(scopedProvider);

    var securityProvider = scopedProvider.GetService<IMessageSecurityContextProvider>();
    if (securityProvider is null) {
      return null;
    }

    // IMPORTANT: Do NOT use ConfigureAwait(false) because we SET AsyncLocal values below.
    // AsyncLocal values flow from parent to child contexts, not child to parent.
    // Using ConfigureAwait(false) would cause our AsyncLocal writes to be lost when returning to caller.
    var securityContext = await securityProvider
      .EstablishContextAsync(envelope, scopedProvider, cancellationToken);

    if (securityContext is not null) {
      var accessor = scopedProvider.GetService<IScopeContextAccessor>();
      if (accessor is not null) {
        accessor.Current = securityContext;
      }
    }

    return securityContext;
  }

  /// <summary>
  /// Sets IMessageContextAccessor.Current from envelope.
  /// Extracts UserId from envelope's security context (last hop).
  /// </summary>
  /// <param name="envelope">Message envelope</param>
  /// <param name="scopedProvider">Scoped service provider</param>
  /// <remarks>
  /// <para>
  /// This reads the security context from the envelope's hops and sets the message context
  /// with MessageId, CorrelationId, CausationId, Timestamp, and UserId.
  /// </para>
  /// </remarks>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:SetMessageContextFromEnvelope_WithSecurityContext_SetsUserIdAsync</tests>
  public static void SetMessageContextFromEnvelope(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(scopedProvider);
    BuildAndEstablishMessageContext(envelope, envelope.GetCurrentScope(), scopedProvider);
  }

  /// <summary>
  /// The single "build a <see cref="MessageContext"/> from an envelope + establish it on both accessors" step,
  /// shared by every establishment site (ReceptorInvoker, PerspectiveWorker, and the SecurityContextHelper
  /// entry points) so the shape can't drift between them. Resolves correlation/causation via
  /// <see cref="CascadeContext.ResolveInheritedIdentity"/>, stamps the (already-resolved) scope's Tenant/User,
  /// sets <see cref="IMessageContextAccessor.Current"/> + <see cref="IScopeContextAccessor.InitiatingContext"/>,
  /// and RETURNS the built context so callers that must survive a <c>ConfigureAwait(false)</c> boundary can
  /// re-establish it synchronously (see <c>ReceptorInvoker.InvokeAsync</c>). The caller resolves + promotes the
  /// scope; this helper does not. Returns <see langword="null"/> when no <see cref="IMessageContextAccessor"/>
  /// is registered (nothing to establish).
  /// </summary>
  internal static MessageContext? BuildAndEstablishMessageContext(
      IMessageEnvelope envelope,
      IScopeContext? scope,
      IServiceProvider scopedProvider,
      CallerInfo? callerInfo = null) {
    var messageContextAccessor = scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is null) {
      return null;
    }

    // Single source of truth for correlation propagation: hop → ambient parent (rescue) → fresh root.
    var (correlation, causation) = CascadeContext.ResolveInheritedIdentity(envelope);
    var messageContext = new MessageContext {
      MessageId = envelope.MessageId,
      CorrelationId = correlation,
      CausationId = causation,
      Timestamp = envelope.GetMessageTimestamp(),
      UserId = scope?.Scope?.UserId,
      TenantId = scope?.Scope?.TenantId,
      ScopeContext = scope,
      CallerInfo = callerInfo
    };
    messageContextAccessor.Current = messageContext;

    // CRITICAL: Set InitiatingContext on IScopeContextAccessor — establishes IMessageContext as the SOURCE OF
    // TRUTH for security context. AsyncLocal carries a REFERENCE to this IMessageContext, not a copy.
    var scopeContextAccessor = scopedProvider.GetService<IScopeContextAccessor>();
    if (scopeContextAccessor is not null) {
      scopeContextAccessor.InitiatingContext = messageContext;
    }

    return messageContext;
  }

  /// <summary>
  /// Full security context establishment: scope context + message context.
  /// Use this from workers processing incoming messages.
  /// </summary>
  /// <param name="envelope">Message envelope with security metadata</param>
  /// <param name="scopedProvider">Scoped service provider for this message</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <remarks>
  /// <para>
  /// This is the standard method for establishing complete security context when processing
  /// incoming messages. It:
  /// </para>
  /// <list type="number">
  /// <item><description>Calls <see cref="EstablishScopeContextAsync"/> to set IScopeContextAccessor.Current</description></item>
  /// <item><description>Calls <see cref="SetMessageContextFromEnvelope"/> to set IMessageContextAccessor.Current</description></item>
  /// <item><description>If extraction succeeds, uses that context for MessageContext.ScopeContext (not envelope.GetCurrentScope())</description></item>
  /// <item><description>Invokes ISecurityContextCallback implementations to notify user code (e.g., UserContextManager)</description></item>
  /// </list>
  /// </remarks>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:EstablishFullContextAsync_SetsBothContextsAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerSecurityContextTests.cs</tests>
  /// <tests>Whizbang.Core.Tests/Workers/TransportConsumerWorkerSecurityContextTests.cs</tests>
  public static async ValueTask EstablishFullContextAsync(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
    // Step 1: Establish scope context via extractors
    // IMPORTANT: Do NOT use ConfigureAwait(false) in this method because we SET AsyncLocal values.
    // AsyncLocal values flow from parent to child contexts, not child to parent.
    // Using ConfigureAwait(false) would cause our AsyncLocal writes to be lost when returning to caller.
    var securityContext = await EstablishScopeContextAsync(envelope, scopedProvider, cancellationToken);

    // Step 2: Determine the scope to use for message context
    // Priority: extractor result > envelope.GetCurrentScope()
    var scopeForMessageContext = securityContext ?? envelope.GetCurrentScope();

    // Step 3: If extraction failed but envelope has scope, wrap it in ImmutableScopeContext
    // and set IScopeContextAccessor (callbacks will be invoked AFTER MessageContextAccessor is set)
    ImmutableScopeContext? immutableScope = null;
    if (securityContext is null && scopeForMessageContext is not null) {
      var extraction = new SecurityExtraction {
        Scope = scopeForMessageContext.Scope,
        Roles = scopeForMessageContext.Roles,
        Permissions = scopeForMessageContext.Permissions,
        SecurityPrincipals = scopeForMessageContext.SecurityPrincipals,
        Claims = scopeForMessageContext.Claims,
        ActualPrincipal = scopeForMessageContext.ActualPrincipal,
        EffectivePrincipal = scopeForMessageContext.EffectivePrincipal,
        ContextType = scopeForMessageContext.ContextType,
        Source = "EnvelopeHop"
      };
      immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);
      scopeForMessageContext = immutableScope;

      // Set IScopeContextAccessor.Current with ImmutableScopeContext (for GetSecurityFromAmbient)
      var accessor = scopedProvider.GetService<IScopeContextAccessor>();
      if (accessor is not null) {
        accessor.Current = immutableScope;
      }
    }

    // Step 4: Set message context with the resolved scope
    // CRITICAL: This must be done BEFORE callbacks are invoked so MessageContextAccessor.Current
    // is available inside callbacks (e.g., UserContextManagerCallback)
    BuildAndEstablishMessageContext(envelope, scopeForMessageContext, scopedProvider);

    // Step 5: Invoke callbacks AFTER both accessors are set
    // This ensures MessageContextAccessor.CurrentContext is available inside callbacks
    // Only invoke when extraction failed but envelope had scope (immutableScope was created)
    if (immutableScope is not null) {
      // Invoke callbacks (e.g., UserContextManagerCallback) since extractors didn't invoke them
      // IMPORTANT: Do NOT use ConfigureAwait(false) - we need AsyncLocal values to flow back
      var callbacks = scopedProvider.GetServices<ISecurityContextCallback>();
      foreach (var callback in callbacks) {
        cancellationToken.ThrowIfCancellationRequested();
        await callback.OnContextEstablishedAsync(immutableScope, envelope, scopedProvider, cancellationToken);
      }
    }
  }

  // _setMessageContextFromEnvelopeWithScope was folded into BuildAndEstablishMessageContext (above) — the one
  // shared "build + establish MessageContext from envelope" step used by every establishment site.

  /// <summary>
  /// Establishes message context for cascaded receptor invocation.
  /// Reads UserId from ScopeContextAccessor and sets MessageContextAccessor.CurrentContext.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Used by Dispatcher cascade path where no envelope is available. The cascade path
  /// invokes receptors via raw delegates, bypassing the normal ReceptorInvoker flow.
  /// </para>
  /// <para>
  /// This method reads the UserId from the current scope context (set by the parent receptor's
  /// invocation via ReceptorInvoker) and establishes a new message context with that UserId.
  /// </para>
  /// <para>
  /// Uses static accessors (<see cref="ScopeContextAccessor.CurrentContext"/> and
  /// <see cref="MessageContextAccessor.CurrentContext"/>) because the Dispatcher is a singleton
  /// and cannot resolve scoped services.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/dispatcher/dispatcher#null-envelope-cascade-paths</docs>
  /// <docs>fundamentals/security/message-security#asynclocal-context-flow</docs>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:EstablishMessageContextForCascade_WithScopeContext_PropagatesUserIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCascadeNullEnvelopeTests.cs:Cascade_WithNullEnvelope_*</tests>
  /// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_*ElseBranch*</tests>
  public static void EstablishMessageContextForCascade(IServiceProvider? serviceProvider = null) {
    var logger = serviceProvider?.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Security.SecurityContextHelper");

    if (logger is not null) {
      Log.CascadeContextEstablishmentStarted(logger);
    }

    // Check if an explicit security context is already set (e.g., by AsSystem/RunAs).
    var scopeAccessor = serviceProvider?.GetService<IScopeContextAccessor>();
    var explicitContext = scopeAccessor?.Current;
    var hasExplicitContext = explicitContext is not null;

    if (hasExplicitContext && logger is not null) {
#pragma warning disable CA1848 // Temporary diagnostic logging
      logger.LogDebug("CASCADE: Explicit security context found - using it for MessageContextAccessor");
#pragma warning restore CA1848
    }

    // Extract userId and tenantId from available contexts
    var (userId, tenantId) = _extractSecurityValuesForCascade(
      hasExplicitContext, explicitContext, logger);

    if (logger is not null) {
      Log.ExtractedSecurityValues(logger, userId, tenantId);
#pragma warning disable CA1873 // !string.IsNullOrEmpty is not expensive to evaluate
      Log.ContextEstablishmentCondition(logger, !string.IsNullOrEmpty(userId), !string.IsNullOrEmpty(tenantId));
#pragma warning restore CA1873
    }

    // CRITICAL: Establish BOTH contexts in cascade scope
    _establishScopeContextForCascade(hasExplicitContext, userId, tenantId, logger);
    _establishMessageContextForCascade(userId, tenantId, logger);
  }

  private static (string? UserId, string? TenantId) _extractSecurityValuesForCascade(
      bool hasExplicitContext, IScopeContext? explicitContext, ILogger? logger) {
    string? userId = null;
    string? tenantId = null;

    // Priority 1: If explicit context exists (AsSystem/RunAs), use it
    if (hasExplicitContext && explicitContext is ImmutableScopeContext immutableCtx) {
      userId = immutableCtx.Scope.UserId;
      tenantId = immutableCtx.Scope.TenantId;
      if (logger is not null) {
        Log.ReadFromExplicitContext(logger, userId, tenantId);
      }
      return (userId, tenantId);
    }

    // Priority 2: Try to read from parent's MessageContextAccessor (AsyncLocal from parent receptor)
    var parentMessageContext = MessageContextAccessor.CurrentContext;
    if (logger is not null) {
      Log.ParentMessageContextChecked(logger, parentMessageContext is null);
    }

    if (parentMessageContext is not null) {
      userId = parentMessageContext.UserId;
      tenantId = parentMessageContext.TenantId;
      if (logger is not null) {
        Log.ReadFromMessageContextAccessor(logger, userId, tenantId);
      }
    }
    // Fallback: try ScopeContextAccessor (for transport workers)
    else if (ScopeContextAccessor.CurrentContext is ImmutableScopeContext ctx) {
      userId = ctx.Scope.UserId;
      tenantId = ctx.Scope.TenantId;
      if (logger is not null) {
        Log.ReadFromScopeContextAccessorFallback(logger, userId, tenantId);
      }
    }

    return (userId, tenantId);
  }

  private static void _establishScopeContextForCascade(
      bool hasExplicitContext, string? userId, string? tenantId, ILogger? logger) {
    // Skip if explicit context already set (AsSystem/RunAs) - don't overwrite it
    if (hasExplicitContext) {
      if (logger is not null) {
        Log.SkippingScopeContextDueToExplicit(logger);
      }
      return;
    }

    if (!string.IsNullOrEmpty(userId) || !string.IsNullOrEmpty(tenantId)) {
      if (logger is not null) {
        Log.CreatingScopeContext(logger);
      }
      var extraction = new SecurityExtraction {
        Scope = new PerspectiveScope {
          TenantId = tenantId,
          UserId = userId
        },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Cascade:AsyncLocal"
      };
      ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(
        extraction,
        shouldPropagate: true
      );
      if (logger is not null) {
        Log.ScopeContextEstablished(logger, ScopeContextAccessor.CurrentContext is null);
      }
    } else {
      if (logger is not null) {
        Log.SkippingScopeContextSetup(logger);
      }
    }
  }

  private static void _establishMessageContextForCascade(
      string? userId, string? tenantId, ILogger? logger) {
    // Inherit the cascade IDENTITY (correlation + causation) from the ambient parent context — the receptor
    // whose handler is emitting this cascaded event — via the single shared resolver, exactly the way userId/
    // tenantId are inherited above. There is NO envelope on this path (the generated cascade publisher invokes
    // receptors directly), so the resolver falls to its ambient-parent branch. Without this, a cascaded event's
    // receptor scope minted a FRESH correlation, orphaning anything that receptor in turn PUBLISHES (the
    // saga-trigger shape) onto a brand-new chain — so the collective apply + completion notification no longer
    // shared the activating command's correlation and the "Template Activating" toast never dismissed.
    var (correlationId, causationId) = CascadeContext.ResolveInheritedIdentity(sourceEnvelope: null);

    // Set MessageContextAccessor (for fallback + other consumers)
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = correlationId,
      CausationId = causationId,
      Timestamp = DateTimeOffset.UtcNow,
      UserId = userId,
      TenantId = tenantId
    };
    MessageContextAccessor.CurrentContext = messageContext;

    // Set InitiatingContext on IScopeContextAccessor (SOURCE OF TRUTH pattern)
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;

    if (logger is not null) {
      Log.MessageContextEstablished(logger, userId, tenantId);
    }
  }

  /// <summary>
  /// AOT-compatible logging for cascade security context establishment.
  /// Uses compile-time LoggerMessage source generator for zero-allocation, high-performance logging.
  /// </summary>
  private static partial class Log {
    /// <summary>Logs the start of cascade security context establishment.</summary>
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Debug,
      Message = "Cascade security context establishment started",
      SkipEnabledCheck = true)]
    public static partial void CascadeContextEstablishmentStarted(ILogger logger);

    /// <summary>Logs whether the parent MessageContextAccessor.CurrentContext is null.</summary>
    [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Debug,
      Message = "Parent MessageContextAccessor.CurrentContext is null: {IsNull}",
      SkipEnabledCheck = true)]
    public static partial void ParentMessageContextChecked(ILogger logger, bool isNull);

    /// <summary>Logs security values read from MessageContextAccessor.</summary>
    [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Debug,
      Message = "Read security context from MessageContextAccessor - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void ReadFromMessageContextAccessor(ILogger logger, string? userId, string? tenantId);

    /// <summary>Logs security values read from ScopeContextAccessor fallback.</summary>
    [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Debug,
      Message = "Read security context from ScopeContextAccessor fallback - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void ReadFromScopeContextAccessorFallback(ILogger logger, string? userId, string? tenantId);

    /// <summary>Logs the extracted UserId and TenantId values for cascade context.</summary>
    [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Debug,
      Message = "Extracted security values - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void ExtractedSecurityValues(ILogger logger, string? userId, string? tenantId);

    /// <summary>Logs whether UserId and TenantId are available for context establishment.</summary>
    [LoggerMessage(
      EventId = 6,
      Level = LogLevel.Debug,
      Message = "Context establishment condition - HasUserId: {HasUserId}, HasTenantId: {HasTenantId}",
      SkipEnabledCheck = true)]
    public static partial void ContextEstablishmentCondition(ILogger logger, bool hasUserId, bool hasTenantId);

    /// <summary>Logs the creation of a SecurityExtraction for ScopeContextAccessor.</summary>
    [LoggerMessage(
      EventId = 7,
      Level = LogLevel.Debug,
      Message = "Creating SecurityExtraction and establishing ScopeContextAccessor.CurrentContext",
      SkipEnabledCheck = true)]
    public static partial void CreatingScopeContext(ILogger logger);

    /// <summary>Logs whether ScopeContextAccessor.CurrentContext was successfully established.</summary>
    [LoggerMessage(
      EventId = 8,
      Level = LogLevel.Debug,
      Message = "ScopeContextAccessor.CurrentContext established - IsNull: {IsNull}",
      SkipEnabledCheck = true)]
    public static partial void ScopeContextEstablished(ILogger logger, bool isNull);

    /// <summary>Logs that ScopeContextAccessor setup was skipped due to missing security values.</summary>
    [LoggerMessage(
      EventId = 9,
      Level = LogLevel.Debug,
      Message = "Skipping ScopeContextAccessor setup - both UserId and TenantId are null or empty",
      SkipEnabledCheck = true)]
    public static partial void SkippingScopeContextSetup(ILogger logger);

    /// <summary>Logs the established MessageContextAccessor values.</summary>
    [LoggerMessage(
      EventId = 10,
      Level = LogLevel.Debug,
      Message = "MessageContextAccessor.CurrentContext established - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void MessageContextEstablished(ILogger logger, string? userId, string? tenantId);

    /// <summary>Logs security values read from an explicit context (AsSystem/RunAs).</summary>
    [LoggerMessage(
      EventId = 11,
      Level = LogLevel.Debug,
      Message = "Read security context from explicit context (AsSystem/RunAs) - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void ReadFromExplicitContext(ILogger logger, string? userId, string? tenantId);

    /// <summary>Logs that ScopeContextAccessor setup was skipped because an explicit context is already set.</summary>
    [LoggerMessage(
      EventId = 12,
      Level = LogLevel.Debug,
      Message = "Skipping ScopeContextAccessor setup - explicit context already set (AsSystem/RunAs)",
      SkipEnabledCheck = true)]
    public static partial void SkippingScopeContextDueToExplicit(ILogger logger);
  }

  /// <summary>
  /// Slice 5a of release/v0.647.0-alpha.1 — wraps <see cref="EstablishFullContextAsync"/>
  /// in a per-call timeout so a hung <see cref="IMessageSecurityContextProvider"/>
  /// implementation (e.g. a consumer BFF's production Jun-2026 incident — provider blocked
  /// indefinitely on an unknown tenant id) surfaces as a clean failure instead
  /// of blocking the worker until pod shutdown.
  /// </summary>
  /// <param name="envelope">Message envelope to establish context from.</param>
  /// <param name="scopedProvider">Scoped service provider.</param>
  /// <param name="timeoutSeconds">Per-call timeout in seconds. Values ≤ 0
  /// disable the timeout (restores legacy behavior).</param>
  /// <param name="ct">Caller's cancellation token. Worker shutdown still
  /// flows through this CT normally — the helper only intercepts the
  /// timeout-specific OCE.</param>
  /// <returns>
  /// <see cref="SecurityContextEstablishmentOutcome.Success"/> when the
  /// provider returned within the timeout (legacy fast path).
  /// <see cref="SecurityContextEstablishmentOutcome.TimedOut"/> when the
  /// timeout fired before the provider returned. Callers should log,
  /// enqueue a <see cref="MessageFailureReason.SecurityContextEstablishmentFailure"/>
  /// failure to the appropriate channel, and skip the row/message.
  /// </returns>
  /// <remarks>
  /// The timeout-vs-shutdown discrimination uses the linked CT pattern with a
  /// <c>when</c> filter on the catch — if the parent <paramref name="ct"/> is
  /// the one that fired (worker shutdown), the OCE re-throws naturally so the
  /// dispatch loop unwinds. Only timeout-specific OCEs return <c>TimedOut</c>.
  /// </remarks>
  /// <docs>operations/dead-letter-queue/internal-dlq</docs>
  public static async ValueTask<SecurityContextEstablishmentOutcome> TryEstablishFullContextWithTimeoutAsync(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      int timeoutSeconds,
      CancellationToken ct) {
    // v0.651 hardening: use WaitAsync(TimeSpan, ct) so the timeout fires GUARANTEED even
    // when the IMessageSecurityContextProvider implementation doesn't observe the
    // cancellation token. The earlier (v0.647) form used CreateLinkedTokenSource +
    // CancelAfter, which only signalled cooperatively — if the provider hung on a
    // non-cancellable I/O (sync-over-async, blocked DB call, etc.), the await sat
    // forever and the catch never fired. a consumer BFF production forensic (Jun 2026) confirmed
    // that exact pattern: a stuck outbox row's SecurityContext call hung for the full
    // lease duration without timing out. The still-running establishment task is
    // abandoned with an observe-only continuation so any late exception lands in
    // UnobservedExceptionDiagnostics rather than disappearing at GC.
    // Capture the Task inside the try so a synchronous throw from EstablishFullContextAsync
    // (e.g., DI validation) propagates normally instead of escaping through a half-initialized
    // local.
    Task? establishTask = null;
    try {
      establishTask = EstablishFullContextAsync(envelope, scopedProvider, ct).AsTask();
      if (timeoutSeconds > 0) {
        await establishTask.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct);
      } else {
        await establishTask;
      }
      return SecurityContextEstablishmentOutcome.Success;
    } catch (TimeoutException) {
      if (establishTask is not null) {
        _ = establishTask.ContinueWith(
          static t => { _ = t.Exception; },
          CancellationToken.None,
          TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
          TaskScheduler.Default);
      }
      return SecurityContextEstablishmentOutcome.TimedOut;
    }
  }

  /// <summary>
  /// Slice 5a — companion to <see cref="TryEstablishFullContextWithTimeoutAsync"/>.
  /// Builds the standard <see cref="MessageFailure"/> shape for a
  /// SecurityContext timeout and enqueues it to the supplied
  /// <see cref="IFailureChannel"/>. Centralises the Error message text so any
  /// future format change happens in one place (and so SonarCloud doesn't
  /// flag near-identical blocks across each worker call site).
  /// </summary>
  /// <param name="failureChannel">Failure channel to enqueue to.</param>
  /// <param name="category">Source <see cref="WorkCategory"/> — Outbox or
  /// Inbox depending on the caller.</param>
  /// <param name="messageId">Source message id.</param>
  /// <param name="completedStatus">Status flags the row had at promotion
  /// time (caller's responsibility — the helper just forwards them).</param>
  /// <param name="timeoutSeconds">Configured timeout that fired; embedded in
  /// the Error text for operator triage.</param>
  /// <param name="ct">Worker cancellation token. The enqueue is a quick
  /// in-memory operation; the CT only matters if the failure channel itself
  /// blocks.</param>
  /// <docs>operations/dead-letter-queue/internal-dlq</docs>
  public static ValueTask EnqueueSecurityContextTimeoutFailureAsync(
      Whizbang.Core.Workers.IFailureChannel failureChannel,
      Whizbang.Core.Messaging.WorkCategory category,
      Guid messageId,
      Whizbang.Core.Messaging.MessageProcessingStatus completedStatus,
      int timeoutSeconds,
      CancellationToken ct) {
    return failureChannel.EnqueueAsync(category, new Whizbang.Core.Messaging.MessageFailure {
      MessageId = messageId,
      CompletedStatus = completedStatus,
      Error = $"EstablishFullContextAsync timed out after {timeoutSeconds}s — IMessageSecurityContextProvider implementation hung on envelope scope (message_id={messageId})",
      Reason = Whizbang.Core.Messaging.MessageFailureReason.SecurityContextEstablishmentFailure,
    }, ct);
  }
}

/// <summary>
/// Outcome of <see cref="SecurityContextHelper.TryEstablishFullContextWithTimeoutAsync"/>.
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public enum SecurityContextEstablishmentOutcome {
  /// <summary>Provider returned within the timeout. Caller proceeds normally.</summary>
  Success,

  /// <summary>The per-call timeout fired before the provider returned. Caller
  /// should log a warning, enqueue a failure with
  /// <see cref="Whizbang.Core.Messaging.MessageFailureReason.SecurityContextEstablishmentFailure"/>,
  /// and skip the row/message.</summary>
  TimedOut,
}
