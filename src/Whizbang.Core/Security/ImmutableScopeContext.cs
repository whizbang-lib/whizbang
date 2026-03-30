using Whizbang.Core.Lenses;

namespace Whizbang.Core.Security;

/// <summary>
/// An immutable wrapper around IScopeContext that cannot be modified after establishment.
/// Provides additional metadata about when and how the context was established.
/// </summary>
/// <remarks>
/// This class wraps an extracted security context and adds:
/// - Immutability guarantees (once established, cannot be changed)
/// - Source tracking (which extractor created it)
/// - Timestamp (when it was established)
/// - Propagation flag (whether to include in outgoing messages)
///
/// The wrapper delegates all IScopeContext methods to the inner context.
/// </remarks>
/// <docs>fundamentals/security/message-security#immutable-context</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ImmutableScopeContextTests.cs</tests>
/// <remarks>
/// Creates an immutable scope context from an extraction result.
/// </remarks>
/// <param name="extraction">The security extraction to wrap</param>
/// <param name="shouldPropagate">Whether to propagate to outgoing messages</param>
public sealed class ImmutableScopeContext(SecurityExtraction extraction, bool shouldPropagate) : IScopeContext {
  private readonly SecurityExtraction _extraction = extraction ?? throw new ArgumentNullException(nameof(extraction));

  /// <summary>
  /// Identifies the source of this context (which extractor created it).
  /// </summary>
  public string Source => _extraction.Source;

  /// <summary>
  /// When this context was established.
  /// </summary>
  public DateTimeOffset EstablishedAt { get; } = DateTimeOffset.UtcNow;

  /// <summary>
  /// Whether this context should be propagated to outgoing messages.
  /// </summary>
  public bool ShouldPropagate { get; } = shouldPropagate;

  // === IScopeContext implementation ===

  /// <inheritdoc />
  public PerspectiveScope Scope => _extraction.Scope;

  /// <inheritdoc />
  public IReadOnlySet<string> Roles => _extraction.Roles;

  /// <inheritdoc />
  public IReadOnlySet<Permission> Permissions => _extraction.Permissions;

  /// <inheritdoc />
  public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals => _extraction.SecurityPrincipals;

  /// <inheritdoc />
  public IReadOnlyDictionary<string, string> Claims => _extraction.Claims;

  /// <inheritdoc />
  public string? ActualPrincipal => _extraction.ActualPrincipal;

  /// <inheritdoc />
  public string? EffectivePrincipal => _extraction.EffectivePrincipal;

  /// <inheritdoc />
  public SecurityContextType ContextType => _extraction.ContextType;

  /// <inheritdoc />
  public bool HasPermission(Permission permission) {
    return Permissions.Any(p => p.Matches(permission));
  }

  /// <inheritdoc />
  public bool HasAnyPermission(params Permission[] permissions) {
    return permissions.Any(HasPermission);
  }

  /// <inheritdoc />
  public bool HasAllPermissions(params Permission[] permissions) {
    return permissions.All(HasPermission);
  }

  /// <inheritdoc />
  public bool HasRole(string roleName) {
    return Roles.Contains(roleName);
  }

  /// <inheritdoc />
  public bool HasAnyRole(params string[] roleNames) {
    return roleNames.Any(Roles.Contains);
  }

  /// <inheritdoc />
  public bool IsMemberOfAny(params SecurityPrincipalId[] principals) {
    return principals.Any(SecurityPrincipals.Contains);
  }

  /// <inheritdoc />
  public bool IsMemberOfAll(params SecurityPrincipalId[] principals) {
    return principals.All(SecurityPrincipals.Contains);
  }
}
