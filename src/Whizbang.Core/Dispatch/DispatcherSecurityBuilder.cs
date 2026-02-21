using System.Runtime.CompilerServices;
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
/// </code>
/// </example>
/// <docs>core-concepts/message-security#explicit-security-context-api</docs>
/// <tests>Whizbang.Core.Tests/Dispatch/DispatcherSecurityBuilderTests.cs</tests>
public sealed class DispatcherSecurityBuilder {
  private readonly IDispatcher _dispatcher;
  private readonly IScopeContextAccessor _scopeContextAccessor;
  private readonly SecurityContextType _contextType;
  private readonly string? _effectivePrincipal;
  private readonly string? _actualPrincipal;

  /// <summary>
  /// Creates a new security builder for dispatching with explicit security context.
  /// </summary>
  /// <param name="dispatcher">The dispatcher to use for sending messages.</param>
  /// <param name="scopeContextAccessor">The scope context accessor for setting security context.</param>
  /// <param name="contextType">The type of security context being established.</param>
  /// <param name="effectivePrincipal">The effective principal (identity the operation runs as).</param>
  /// <param name="actualPrincipal">The actual principal (who initiated the operation, may be null for true system ops).</param>
  internal DispatcherSecurityBuilder(
    IDispatcher dispatcher,
    IScopeContextAccessor scopeContextAccessor,
    SecurityContextType contextType,
    string? effectivePrincipal,
    string? actualPrincipal) {
    _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    _scopeContextAccessor = scopeContextAccessor ?? throw new ArgumentNullException(nameof(scopeContextAccessor));
    _contextType = contextType;
    _effectivePrincipal = effectivePrincipal;
    _actualPrincipal = actualPrincipal;
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
    var previousContext = _scopeContextAccessor.Current;
    try {
      _scopeContextAccessor.Current = _createExplicitContext();
      return await _dispatcher.SendAsync(message);
    } finally {
      _scopeContextAccessor.Current = previousContext;
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
    var previousContext = _scopeContextAccessor.Current;
    try {
      _scopeContextAccessor.Current = _createExplicitContext();
      return await _dispatcher.SendAsync(message, context, callerMemberName, callerFilePath, callerLineNumber);
    } finally {
      _scopeContextAccessor.Current = previousContext;
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

    var previousContext = _scopeContextAccessor.Current;
    try {
      _scopeContextAccessor.Current = _createExplicitContext();
      return await _dispatcher.SendAsync(message, options);
    } finally {
      _scopeContextAccessor.Current = previousContext;
    }
  }

  /// <summary>
  /// Publishes an event with explicit security context.
  /// </summary>
  /// <typeparam name="TEvent">The event type.</typeparam>
  /// <param name="eventData">The event to publish.</param>
  /// <returns>Delivery receipt with correlation information.</returns>
  public async Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
    var previousContext = _scopeContextAccessor.Current;
    try {
      _scopeContextAccessor.Current = _createExplicitContext();
      return await _dispatcher.PublishAsync(eventData);
    } finally {
      _scopeContextAccessor.Current = previousContext;
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
    var previousContext = _scopeContextAccessor.Current;
    try {
      _scopeContextAccessor.Current = _createExplicitContext();
      return await _dispatcher.LocalInvokeAsync<TMessage, TResult>(message);
    } finally {
      _scopeContextAccessor.Current = previousContext;
    }
  }

  /// <summary>
  /// Invokes a void receptor in-process with explicit security context.
  /// </summary>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <param name="message">The message to process.</param>
  /// <returns>ValueTask representing the completion.</returns>
  public async ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull {
    var previousContext = _scopeContextAccessor.Current;
    try {
      _scopeContextAccessor.Current = _createExplicitContext();
      await _dispatcher.LocalInvokeAsync(message);
    } finally {
      _scopeContextAccessor.Current = previousContext;
    }
  }

  private ImmutableScopeContext _createExplicitContext() {
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope {
        UserId = _effectivePrincipal,
        TenantId = null // System/impersonation ops may be cross-tenant
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
}
