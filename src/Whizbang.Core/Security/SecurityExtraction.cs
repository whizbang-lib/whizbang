// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Security;

/// <summary>
/// Result of security context extraction from a message.
/// Contains all security-related information extracted from the message.
/// </summary>
/// <docs>fundamentals/security/message-security#extraction</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ImmutableScopeContextTests.cs:Constructor_WithValidExtraction_CreatesContextAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ImmutableScopeContextTests.cs:Scope_DelegatesToExtractionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs:EstablishContextAsync_PropagateToOutgoingTrue_SetsContextForPropagationAsync</tests>
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

  /// <summary>
  /// The claim types whose values may leave this process on the scope of an outgoing message. Empty by
  /// default, so no claim is ever copied into message metadata unless the application opted in for it.
  /// </summary>
  /// <remarks>
  /// The edge that reads the claims decides: the HTTP scope middleware copies its
  /// <c>WhizbangScopeOptions.PropagatedClaimTypes</c> here. A scope restored from a message's hops lists
  /// every claim it carries, because each of them was opted in where the chain started.
  /// </remarks>
  /// <docs>fundamentals/security/message-security#hop-scope</docs>
  /// <tests>tests/Whizbang.Core.Tests/Security/HopScopeTests.cs</tests>
  public IReadOnlySet<string> PropagatedClaimTypes { get; init; } = FrozenSet<string>.Empty;
}
