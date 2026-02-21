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

  /// <summary>
  /// Static accessor for the current scope context.
  /// Use this from singleton services that cannot resolve the scoped IScopeContextAccessor.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This provides direct access to the ambient context without requiring DI resolution.
  /// Use sparingly - prefer the scoped IScopeContextAccessor for proper DI patterns.
  /// </para>
  /// <para>
  /// Primary use case: Singleton services (e.g., Dispatcher) that need to read/write
  /// context but cannot resolve scoped services.
  /// </para>
  /// </remarks>
  public static IScopeContext? CurrentContext {
    get => _current.Value;
    set => _current.Value = value;
  }

  /// <inheritdoc />
  public IScopeContext? Current {
    get => _current.Value;
    set => _current.Value = value;
  }
}
