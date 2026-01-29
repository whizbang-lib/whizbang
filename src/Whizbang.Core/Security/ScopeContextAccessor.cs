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
/// Register as singleton in DI:
/// services.AddSingleton&lt;IScopeContextAccessor, ScopeContextAccessor&gt;();
/// </para>
/// </remarks>
public sealed class ScopeContextAccessor : IScopeContextAccessor {
  private static readonly AsyncLocal<IScopeContext?> _current = new();

  /// <inheritdoc />
  public IScopeContext? Current {
    get => _current.Value;
    set => _current.Value = value;
  }
}
