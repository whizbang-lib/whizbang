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

    var messageContextAccessor = scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is null) {
      return;
    }

    var scopeContext = envelope.GetCurrentScope();
    var messageContext = new MessageContext {
      MessageId = envelope.MessageId,
      CorrelationId = envelope.GetCorrelationId() ?? CorrelationId.New(),
      CausationId = envelope.GetCausationId() ?? MessageId.New(),
      Timestamp = envelope.GetMessageTimestamp(),
      UserId = scopeContext?.Scope?.UserId,
      TenantId = scopeContext?.Scope?.TenantId,
      ScopeContext = scopeContext
    };
    messageContextAccessor.Current = messageContext;

    // CRITICAL: Set InitiatingContext on IScopeContextAccessor
    // This establishes IMessageContext as the SOURCE OF TRUTH for security context.
    // AsyncLocal carries a REFERENCE to this IMessageContext, not a copy of its data.
    var scopeContextAccessor = scopedProvider.GetService<IScopeContextAccessor>();
    if (scopeContextAccessor is not null) {
      scopeContextAccessor.InitiatingContext = messageContext;
    }
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
    _setMessageContextFromEnvelopeWithScope(envelope, scopedProvider, scopeForMessageContext);

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

  /// <summary>
  /// Sets IMessageContextAccessor.Current from envelope, using a provided scope context.
  /// This overload allows passing an already-established scope context (from extractors).
  /// </summary>
  private static void _setMessageContextFromEnvelopeWithScope(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      IScopeContext? scopeContext) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(scopedProvider);

    var messageContextAccessor = scopedProvider.GetService<IMessageContextAccessor>();
    if (messageContextAccessor is null) {
      return;
    }

    var messageContext = new MessageContext {
      MessageId = envelope.MessageId,
      CorrelationId = envelope.GetCorrelationId() ?? CorrelationId.New(),
      CausationId = envelope.GetCausationId() ?? MessageId.New(),
      Timestamp = envelope.GetMessageTimestamp(),
      UserId = scopeContext?.Scope?.UserId,
      TenantId = scopeContext?.Scope?.TenantId,
      ScopeContext = scopeContext
    };
    messageContextAccessor.Current = messageContext;

    // CRITICAL: Set InitiatingContext on IScopeContextAccessor
    var scopeContextAccessor = scopedProvider.GetService<IScopeContextAccessor>();
    if (scopeContextAccessor is not null) {
      scopeContextAccessor.InitiatingContext = messageContext;
    }
  }

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
    // Set MessageContextAccessor (for fallback + other consumers)
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
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
}
