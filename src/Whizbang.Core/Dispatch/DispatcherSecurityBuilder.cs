using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Dispatch;

/// <summary>
/// Fluent builder for dispatching messages with explicit security context.
/// Supports system operations (timers, schedulers) and impersonation scenarios
/// with full audit trail.
/// </summary>
/// <remarks>
/// <para>
/// This builder temporarily sets the security context on <see cref="IScopeContextAccessor"/>
/// during dispatch, then restores the previous context. The context is propagated to
/// outgoing message hops when <see cref="ImmutableScopeContext.ShouldPropagate"/> is true.
/// </para>
/// <para>
/// <b>Audit Trail</b>: Both <see cref="SecurityExtraction.ActualPrincipal"/> (who really did it)
/// and <see cref="SecurityExtraction.EffectivePrincipal"/> (what identity it runs as) are captured
/// for security auditing and compliance.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // System operation (timer/scheduler)
/// await dispatcher.AsSystem().SendAsync(new ReseedSystemEvent());
///
/// // Admin running as system (audit shows admin triggered it)
/// await dispatcher.AsSystem().SendAsync(new MaintenanceCommand());
///
/// // Support impersonating a user for debugging
/// await dispatcher.RunAs("target-user@example.com").SendAsync(command);
///
/// // System operation on a specific tenant
/// await dispatcher.AsSystem().WithTenant("tenant-123").SendAsync(new TenantMaintenanceCommand());
///
/// // Admin impersonating user in a different tenant
/// await dispatcher.RunAs("target-user").WithTenant("target-tenant").SendAsync(command);
/// </code>
/// </example>
/// <docs>core-concepts/message-security#explicit-security-context-api</docs>
/// <tests>Whizbang.Core.Tests/Dispatch/DispatcherSecurityBuilderTests.cs</tests>
public sealed partial class DispatcherSecurityBuilder {
  private readonly IDispatcher _dispatcher;
  private readonly SecurityContextType _contextType;
  private readonly string? _effectivePrincipal;
  private readonly string? _actualPrincipal;
  private readonly string? _tenantId;

  // Lazy-resolved logger for security warnings
  private ILogger? _logger;
#pragma warning disable IDE1006 // Naming rule - property follows internal naming convention
  private ILogger _Logger => _logger ??= (_dispatcher as Dispatcher)?.InternalServiceProvider
    .GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Dispatch.DispatcherSecurityBuilder")
    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
#pragma warning restore IDE1006

  /// <summary>
  /// Creates a new security builder for dispatching with explicit security context.
  /// </summary>
  /// <param name="dispatcher">The dispatcher to use for sending messages.</param>
  /// <param name="contextType">The type of security context being established.</param>
  /// <param name="effectivePrincipal">The effective principal (identity the operation runs as).</param>
  /// <param name="actualPrincipal">The actual principal (who initiated the operation, may be null for true system ops).</param>
  /// <param name="tenantId">Optional explicit tenant ID for cross-tenant operations.</param>
  internal DispatcherSecurityBuilder(
    IDispatcher dispatcher,
    SecurityContextType contextType,
    string? effectivePrincipal,
    string? actualPrincipal,
    string? tenantId = null) {
    _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    _contextType = contextType;
    _effectivePrincipal = effectivePrincipal;
    _actualPrincipal = actualPrincipal;
    _tenantId = tenantId;
  }

  /// <summary>
  /// Specifies an explicit tenant context for the dispatch operation.
  /// Use for cross-tenant operations or backend services operating on behalf of a specific tenant.
  /// </summary>
  /// <param name="tenantId">The tenant ID to operate within.</param>
  /// <returns>A new builder with the tenant context set.</returns>
  /// <exception cref="ArgumentException">
  /// Thrown when <paramref name="tenantId"/> is null, empty, or whitespace.
  /// </exception>
  /// <example>
  /// <code>
  /// // System operation on a specific tenant
  /// await dispatcher.AsSystem().WithTenant("tenant-123").SendAsync(new TenantMaintenanceCommand());
  ///
  /// // Admin impersonating user in a different tenant
  /// await dispatcher.RunAs("target-user").WithTenant("target-tenant").SendAsync(command);
  /// </code>
  /// </example>
  /// <docs>core-concepts/message-security#cross-tenant-operations</docs>
  /// <tests>Whizbang.Core.Tests/Dispatch/DispatcherSecurityBuilderTests.cs:WithTenant_SetsTenantIdOnContextAsync</tests>
  public DispatcherSecurityBuilder WithTenant(string tenantId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tenantId, nameof(tenantId));

    return new DispatcherSecurityBuilder(
      _dispatcher,
      _contextType,
      _effectivePrincipal,
      _actualPrincipal,
      tenantId: tenantId);
  }

  /// <summary>
  /// Sends a typed message with the explicit security context and returns a delivery receipt.
  /// </summary>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <param name="message">The message to send.</param>
  /// <param name="callerMemberName">Caller method name (auto-captured).</param>
  /// <param name="callerFilePath">Caller file path (auto-captured).</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured).</param>
  /// <returns>Delivery receipt with correlation information.</returns>
  public async Task<IDeliveryReceipt> SendAsync<TMessage>(
    TMessage message,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull {
    var previousContext = ScopeContextAccessor.CurrentContext;
    try {
      ScopeContextAccessor.CurrentContext = _createExplicitContext();
      return await _dispatcher.SendAsync(message);
    } finally {
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  /// <summary>
  /// Sends a typed message with explicit security context and message context.
  /// </summary>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <param name="message">The message to send.</param>
  /// <param name="context">The message context for correlation.</param>
  /// <param name="callerMemberName">Caller method name (auto-captured).</param>
  /// <param name="callerFilePath">Caller file path (auto-captured).</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured).</param>
  /// <returns>Delivery receipt with correlation information.</returns>
  public async Task<IDeliveryReceipt> SendAsync<TMessage>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull {
    var previousContext = ScopeContextAccessor.CurrentContext;
    try {
      ScopeContextAccessor.CurrentContext = _createExplicitContext();
      return await _dispatcher.SendAsync(message, context, callerMemberName, callerFilePath, callerLineNumber);
    } finally {
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  /// <summary>
  /// Sends a typed message with explicit security context and dispatch options.
  /// </summary>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <param name="message">The message to send.</param>
  /// <param name="options">Options controlling dispatch behavior (cancellation, timeout).</param>
  /// <returns>Delivery receipt with correlation information.</returns>
  public async Task<IDeliveryReceipt> SendAsync<TMessage>(
    TMessage message,
    DispatchOptions options) where TMessage : notnull {
    // Check cancellation before doing any work
    options.CancellationToken.ThrowIfCancellationRequested();

    var previousContext = ScopeContextAccessor.CurrentContext;
    try {
      ScopeContextAccessor.CurrentContext = _createExplicitContext();
      return await _dispatcher.SendAsync(message, options);
    } finally {
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  /// <summary>
  /// Publishes an event with explicit security context.
  /// </summary>
  /// <typeparam name="TEvent">The event type.</typeparam>
  /// <param name="eventData">The event to publish.</param>
  /// <returns>Delivery receipt with correlation information.</returns>
  public async Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
    var previousContext = ScopeContextAccessor.CurrentContext;
    try {
      ScopeContextAccessor.CurrentContext = _createExplicitContext();
      return await _dispatcher.PublishAsync(eventData);
    } finally {
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  /// <summary>
  /// Invokes a receptor in-process with explicit security context and returns the typed business result.
  /// </summary>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <typeparam name="TResult">The expected business result type.</typeparam>
  /// <param name="message">The message to process.</param>
  /// <returns>The typed business result from the receptor.</returns>
  public async ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull {
    var previousContext = ScopeContextAccessor.CurrentContext;
    try {
      ScopeContextAccessor.CurrentContext = _createExplicitContext();
      return await _dispatcher.LocalInvokeAsync<TMessage, TResult>(message);
    } finally {
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  /// <summary>
  /// Invokes a void receptor in-process with explicit security context.
  /// </summary>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <param name="message">The message to process.</param>
  /// <returns>ValueTask representing the completion.</returns>
  public async ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull {
    var previousContext = ScopeContextAccessor.CurrentContext;
    try {
      ScopeContextAccessor.CurrentContext = _createExplicitContext();
      await _dispatcher.LocalInvokeAsync(message);
    } finally {
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  private ImmutableScopeContext _createExplicitContext() {
    // Warn if the actual principal (current user before elevation) is an empty GUID
    // This indicates the originating request didn't have proper user context
    if (!string.IsNullOrEmpty(_actualPrincipal) &&
        Guid.TryParse(_actualPrincipal, out var parsedActual) &&
        parsedActual == Guid.Empty) {
      Log.EmptyGuidActualPrincipal(_Logger, _contextType.ToString(), _tenantId);
    }

    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope {
        UserId = _effectivePrincipal,
        TenantId = _tenantId // Explicit tenant or null for cross-tenant operations
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = $"Explicit:{_contextType}",
      ContextType = _contextType,
      ActualPrincipal = _actualPrincipal,
      EffectivePrincipal = _effectivePrincipal
    };
    return new ImmutableScopeContext(extraction, shouldPropagate: true);
  }

  /// <summary>
  /// AOT-compatible logging for security builder warnings.
  /// Uses compile-time LoggerMessage source generator for zero-allocation, high-performance logging.
  /// </summary>
  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Warning,
      Message = "Explicit security context ({ContextType}) created with empty GUID (00000000-0000-0000-0000-000000000000) as ActualPrincipal. " +
                "This indicates the originating request didn't have proper user context captured. TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void EmptyGuidActualPrincipal(ILogger logger, string? contextType, string? tenantId);
  }
}
