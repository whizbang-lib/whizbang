namespace Whizbang.Core.Security;

/// <summary>
/// AsyncLocal-based implementation of IScopeContextAccessor.
/// Provides ambient scope context that flows across async calls within the same logical context.
/// </summary>
/// <docs>fundamentals/security/security#scope-context-accessor</docs>
/// <tests>Whizbang.Core.Tests/Security/ScopeContextAccessorTests.cs</tests>
/// <remarks>
/// <para>
/// Uses AsyncLocal&lt;T&gt; for proper async flow semantics:
/// - Context automatically flows to child tasks
/// - Changes in child tasks don't affect parent context
/// - Each parallel task can have isolated context
/// </para>
/// <para>
/// Register as scoped in DI:
/// services.AddScoped&lt;IScopeContextAccessor, ScopeContextAccessor&gt;();
/// </para>
/// <para>
/// For singleton services that need to read the current context (e.g., Dispatcher),
/// use <see cref="CurrentContext"/> which provides direct access to the static AsyncLocal.
/// </para>
/// </remarks>
public sealed class ScopeContextAccessor : IScopeContextAccessor {
  private static readonly AsyncLocal<IScopeContext?> _current = new();
  private static readonly AsyncLocal<IMessageContext?> _initiatingContext = new();

  /// <summary>
  /// Static accessor for the current scope context.
  /// Prioritizes ImmutableScopeContext with propagation (for security propagation),
  /// then falls back to initiating context's ScopeContext, then _current.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Priority order:
  /// 1. _current if it's an ImmutableScopeContext with ShouldPropagate=true
  ///    (set by ReceptorInvoker after EstablishContextAsync - needed for security propagation)
  /// 2. InitiatingContext.ScopeContext (the message context's scope)
  /// 3. _current (backward compatibility fallback)
  /// </para>
  /// <para>
  /// This ensures that when security infrastructure explicitly sets an ImmutableScopeContext
  /// with propagation enabled (e.g., for cascaded events), it takes precedence over the
  /// initiating context's scope which may not have propagation enabled.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CascadeToOutboxIntegrationTests.cs:InitiatingContext_DoesNotShadow_ImmutableScopeContextWithPropagationAsync</tests>
  public static IScopeContext? CurrentContext {
    get {
      // Priority 1: _current if it's an ImmutableScopeContext with propagation enabled
      // This is critical for security propagation to cascaded events
      if (_current.Value is ImmutableScopeContext { ShouldPropagate: true }) {
        return _current.Value;
      }
      // Priority 2: InitiatingContext's ScopeContext, then _current fallback
      return _initiatingContext.Value?.ScopeContext ?? _current.Value;
    }
    set => _current.Value = value;
  }

  /// <summary>
  /// Static accessor for the initiating message context.
  /// Use this from singleton services that cannot resolve the scoped IScopeContextAccessor.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The initiating context is the IMessageContext that started the current scope.
  /// It is the SOURCE OF TRUTH for security context (UserId, TenantId).
  /// </para>
  /// <para>
  /// Primary use case: Singleton services (e.g., Dispatcher) that need to read
  /// the initiating message context for security propagation.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/messages/cascade-context#initiating-context</docs>
  /// <tests>Whizbang.Core.Tests/Security/ScopeContextAccessorInitiatingContextTests.cs</tests>
  public static IMessageContext? CurrentInitiatingContext {
    get => _initiatingContext.Value;
    set => _initiatingContext.Value = value;
  }

  /// <summary>
  /// Static accessor for UserId from InitiatingContext (SOURCE OF TRUTH).
  /// This is a POINTER to InitiatingContext.UserId, not a copy.
  /// </summary>
  /// <docs>fundamentals/messages/cascade-context#pointer-properties</docs>
  public static string? CurrentUserId => _initiatingContext.Value?.UserId;

  /// <summary>
  /// Static accessor for TenantId from InitiatingContext (SOURCE OF TRUTH).
  /// This is a POINTER to InitiatingContext.TenantId, not a copy.
  /// </summary>
  /// <docs>fundamentals/messages/cascade-context#pointer-properties</docs>
  public static string? CurrentTenantId => _initiatingContext.Value?.TenantId;

  /// <inheritdoc />
  /// <remarks>
  /// Reads FROM the initiating message context's ScopeContext.
  /// Fallback to _current for backward compatibility.
  /// </remarks>
  public IScopeContext? Current {
    get => _initiatingContext.Value?.ScopeContext ?? _current.Value;
    set => _current.Value = value;
  }

  /// <inheritdoc />
  /// <docs>fundamentals/messages/cascade-context#initiating-context</docs>
  /// <tests>Whizbang.Core.Tests/Security/ScopeContextAccessorInitiatingContextTests.cs</tests>
  public IMessageContext? InitiatingContext {
    get => _initiatingContext.Value;
    set => _initiatingContext.Value = value;
  }
}
