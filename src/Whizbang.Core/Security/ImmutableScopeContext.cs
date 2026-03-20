#pragma warning disable S3604, S3928 // Primary constructor field/property initializers are intentional

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
    foreach (var p in Permissions) {
      if (p.Matches(permission)) {
        return true;
      }
    }

    return false;
  }

  /// <inheritdoc />
  public bool HasAnyPermission(params Permission[] permissions) {
    foreach (var required in permissions) {
      if (HasPermission(required)) {
        return true;
      }
    }

    return false;
  }

  /// <inheritdoc />
  public bool HasAllPermissions(params Permission[] permissions) {
    foreach (var required in permissions) {
      if (!HasPermission(required)) {
        return false;
      }
    }

    return true;
  }

  /// <inheritdoc />
  public bool HasRole(string roleName) {
    return Roles.Contains(roleName);
  }

  /// <inheritdoc />
  public bool HasAnyRole(params string[] roleNames) {
    foreach (var role in roleNames) {
      if (Roles.Contains(role)) {
        return true;
      }
    }

    return false;
  }

  /// <inheritdoc />
  public bool IsMemberOfAny(params SecurityPrincipalId[] principals) {
    foreach (var principal in principals) {
      if (SecurityPrincipals.Contains(principal)) {
        return true;
      }
    }

    return false;
  }

  /// <inheritdoc />
  public bool IsMemberOfAll(params SecurityPrincipalId[] principals) {
    foreach (var principal in principals) {
      if (!SecurityPrincipals.Contains(principal)) {
        return false;
      }
    }

    return true;
  }
}
