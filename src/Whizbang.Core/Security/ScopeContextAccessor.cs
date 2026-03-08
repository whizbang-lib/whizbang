namespace Whizbang.Core.Security;

/// <summary>
/// AsyncLocal-based implementation of IScopeContextAccessor.
/// Provides ambient scope context that flows across async calls within the same logical context.
/// </summary>
/// <docs>core-concepts/security#scope-context-accessor</docs>
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
  /// Reads FROM the initiating message context's ScopeContext.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Messages carry state in event-sourced systems. The ScopeContext is OWNED by
  /// the IMessageContext, and this property reads FROM it.
  /// </para>
  /// <para>
  /// Fallback: If no InitiatingContext is set, falls back to _current for backward
  /// compatibility during migration.
  /// </para>
  /// </remarks>
  public static IScopeContext? CurrentContext {
    get => _initiatingContext.Value?.ScopeContext ?? _current.Value;
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
  /// <docs>core-concepts/cascade-context#initiating-context</docs>
  /// <tests>Whizbang.Core.Tests/Security/ScopeContextAccessorInitiatingContextTests.cs</tests>
  public static IMessageContext? CurrentInitiatingContext {
    get => _initiatingContext.Value;
    set => _initiatingContext.Value = value;
  }

  /// <summary>
  /// Static accessor for UserId from InitiatingContext (SOURCE OF TRUTH).
  /// This is a POINTER to InitiatingContext.UserId, not a copy.
  /// </summary>
  /// <docs>core-concepts/cascade-context#pointer-properties</docs>
  public static string? CurrentUserId => _initiatingContext.Value?.UserId;

  /// <summary>
  /// Static accessor for TenantId from InitiatingContext (SOURCE OF TRUTH).
  /// This is a POINTER to InitiatingContext.TenantId, not a copy.
  /// </summary>
  /// <docs>core-concepts/cascade-context#pointer-properties</docs>
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
  /// <docs>core-concepts/cascade-context#initiating-context</docs>
  /// <tests>Whizbang.Core.Tests/Security/ScopeContextAccessorInitiatingContextTests.cs</tests>
  public IMessageContext? InitiatingContext {
    get => _initiatingContext.Value;
    set => _initiatingContext.Value = value;
  }
}
