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
/// <docs>core-concepts/message-security#security-context-helper</docs>
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

    var securityContext = await securityProvider
      .EstablishContextAsync(envelope, scopedProvider, cancellationToken)
      .ConfigureAwait(false);

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

    var securityContext = envelope.GetCurrentSecurityContext();
    messageContextAccessor.Current = new MessageContext {
      MessageId = envelope.MessageId,
      CorrelationId = envelope.GetCorrelationId() ?? CorrelationId.New(),
      CausationId = envelope.GetCausationId() ?? MessageId.New(),
      Timestamp = envelope.GetMessageTimestamp(),
      UserId = securityContext?.UserId,
      TenantId = securityContext?.TenantId
    };
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
  /// </list>
  /// </remarks>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:EstablishFullContextAsync_SetsBothContextsAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerSecurityContextTests.cs</tests>
  /// <tests>Whizbang.Core.Tests/Workers/TransportConsumerWorkerSecurityContextTests.cs</tests>
  public static async ValueTask EstablishFullContextAsync(
      IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
    await EstablishScopeContextAsync(envelope, scopedProvider, cancellationToken).ConfigureAwait(false);
    SetMessageContextFromEnvelope(envelope, scopedProvider);
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
  /// <docs>core-concepts/dispatcher#null-envelope-cascade-paths</docs>
  /// <docs>core-concepts/message-security#asynclocal-context-flow</docs>
  /// <tests>Whizbang.Core.Tests/Security/SecurityContextHelperTests.cs:EstablishMessageContextForCascade_WithScopeContext_PropagatesUserIdAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCascadeNullEnvelopeTests.cs:Cascade_WithNullEnvelope_*</tests>
  /// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_*ElseBranch*</tests>
  public static void EstablishMessageContextForCascade(IServiceProvider? serviceProvider = null) {
    var logger = serviceProvider?.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Security.SecurityContextHelper");

    // CRITICAL: Always log at Information level to verify method is being called
    if (logger is not null) {
#pragma warning disable CA1848 // Temporary diagnostic logging
      logger.LogInformation("🔧 CASCADE CONTEXT ESTABLISHMENT - Method called (this should always appear)");
#pragma warning restore CA1848
      Log.CascadeContextEstablishmentStarted(logger);
    } else {
      // If no logger, we have a problem - but at least we know the method was called
      Console.WriteLine("🔧 CASCADE: EstablishMessageContextForCascade called but logger is NULL");
    }

    string? userId = null;
    string? tenantId = null;

    // Try to read from parent's MessageContextAccessor (AsyncLocal from parent receptor)
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

    if (logger is not null) {
      Log.ExtractedSecurityValues(logger, userId, tenantId);
#pragma warning disable CA1873 // !string.IsNullOrEmpty is not expensive to evaluate
      Log.ContextEstablishmentCondition(logger, !string.IsNullOrEmpty(userId), !string.IsNullOrEmpty(tenantId));
#pragma warning restore CA1873
    }

    // CRITICAL: Establish BOTH contexts in cascade scope

    // 1. Set ScopeContextAccessor (for ScopedMessageContext.UserId priority 1)
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

    // 2. Set MessageContextAccessor (for fallback + other consumers)
    MessageContextAccessor.CurrentContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = userId,
      TenantId = tenantId
    };
    if (logger is not null) {
      Log.MessageContextEstablished(logger, userId, tenantId);
    }
  }

  /// <summary>
  /// AOT-compatible logging for cascade security context establishment.
  /// Uses compile-time LoggerMessage source generator for zero-allocation, high-performance logging.
  /// </summary>
  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Debug,
      Message = "Cascade security context establishment started",
      SkipEnabledCheck = true)]
    public static partial void CascadeContextEstablishmentStarted(ILogger logger);

    [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Debug,
      Message = "Parent MessageContextAccessor.CurrentContext is null: {IsNull}",
      SkipEnabledCheck = true)]
    public static partial void ParentMessageContextChecked(ILogger logger, bool isNull);

    [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Debug,
      Message = "Read security context from MessageContextAccessor - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void ReadFromMessageContextAccessor(ILogger logger, string? userId, string? tenantId);

    [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Debug,
      Message = "Read security context from ScopeContextAccessor fallback - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void ReadFromScopeContextAccessorFallback(ILogger logger, string? userId, string? tenantId);

    [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Debug,
      Message = "Extracted security values - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void ExtractedSecurityValues(ILogger logger, string? userId, string? tenantId);

    [LoggerMessage(
      EventId = 6,
      Level = LogLevel.Debug,
      Message = "Context establishment condition - HasUserId: {HasUserId}, HasTenantId: {HasTenantId}",
      SkipEnabledCheck = true)]
    public static partial void ContextEstablishmentCondition(ILogger logger, bool hasUserId, bool hasTenantId);

    [LoggerMessage(
      EventId = 7,
      Level = LogLevel.Debug,
      Message = "Creating SecurityExtraction and establishing ScopeContextAccessor.CurrentContext",
      SkipEnabledCheck = true)]
    public static partial void CreatingScopeContext(ILogger logger);

    [LoggerMessage(
      EventId = 8,
      Level = LogLevel.Debug,
      Message = "ScopeContextAccessor.CurrentContext established - IsNull: {IsNull}",
      SkipEnabledCheck = true)]
    public static partial void ScopeContextEstablished(ILogger logger, bool isNull);

    [LoggerMessage(
      EventId = 9,
      Level = LogLevel.Debug,
      Message = "Skipping ScopeContextAccessor setup - both UserId and TenantId are null or empty",
      SkipEnabledCheck = true)]
    public static partial void SkippingScopeContextSetup(ILogger logger);

    [LoggerMessage(
      EventId = 10,
      Level = LogLevel.Debug,
      Message = "MessageContextAccessor.CurrentContext established - UserId: {UserId}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void MessageContextEstablished(ILogger logger, string? userId, string? tenantId);
  }
}
