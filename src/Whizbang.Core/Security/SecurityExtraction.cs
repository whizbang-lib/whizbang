using Whizbang.Core.Lenses;

namespace Whizbang.Core.Security;

/// <summary>
/// Result of security context extraction from a message.
/// Contains all security-related information extracted from the message.
/// </summary>
/// <docs>core-concepts/message-security#extraction</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs</tests>
public sealed record SecurityExtraction {
  /// <summary>
  /// The perspective scope containing TenantId, UserId, etc.
  /// </summary>
  public required PerspectiveScope Scope { get; init; }

  /// <summary>
  /// Role names assigned to the caller.
  /// </summary>
  public required IReadOnlySet<string> Roles { get; init; }

  /// <summary>
  /// Permissions from roles and direct grants.
  /// </summary>
  public required IReadOnlySet<Permission> Permissions { get; init; }

  /// <summary>
  /// Security principal IDs the caller belongs to (user + groups).
  /// </summary>
  public required IReadOnlySet<SecurityPrincipalId> SecurityPrincipals { get; init; }

  /// <summary>
  /// Raw claims from authentication.
  /// </summary>
  public required IReadOnlyDictionary<string, string> Claims { get; init; }

  /// <summary>
  /// Identifies the source of this extraction for audit/debugging.
  /// Examples: "MessageHop", "JwtPayload", "ServiceBusMetadata", "Explicit:System"
  /// </summary>
  public required string Source { get; init; }

  /// <summary>
  /// The actual principal who initiated this operation (never hidden).
  /// Null for true system operations with no user involvement.
  /// For impersonation scenarios, this shows who is actually performing the action.
  /// </summary>
  public string? ActualPrincipal { get; init; }

  /// <summary>
  /// The effective principal the operation runs as.
  /// May differ from ActualPrincipal when impersonating.
  /// For system operations, this is "SYSTEM".
  /// </summary>
  public string? EffectivePrincipal { get; init; }

  /// <summary>
  /// Type of security context establishment.
  /// Indicates whether this is a normal user operation, system operation,
  /// impersonation, or service account.
  /// </summary>
  public SecurityContextType ContextType { get; init; } = SecurityContextType.User;
}
