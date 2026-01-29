using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents.Security;

/// <summary>
/// Emitted when access to a resource is denied due to insufficient permissions.
/// </summary>
/// <docs>core-concepts/system-events#access-denied</docs>
/// <tests>Whizbang.Core.Tests/SystemEvents/Security/SecuritySystemEventTests.cs</tests>
[AuditEvent(Exclude = true, Reason = "System event - security events are not self-audited")]
public sealed record AccessDenied : ISystemEvent {
  /// <summary>
  /// Unique identifier for this event.
  /// </summary>
  [StreamKey]
  public Guid Id { get; init; } = TrackedGuid.NewMedo();

  /// <summary>
  /// Type of resource access was denied to.
  /// </summary>
  public required string ResourceType { get; init; }

  /// <summary>
  /// Optional resource identifier.
  /// </summary>
  public string? ResourceId { get; init; }

  /// <summary>
  /// The permission that was required.
  /// </summary>
  public required Permission RequiredPermission { get; init; }

  /// <summary>
  /// Permissions the caller had.
  /// </summary>
  public required IReadOnlySet<Permission> CallerPermissions { get; init; }

  /// <summary>
  /// Roles the caller had.
  /// </summary>
  public required IReadOnlySet<string> CallerRoles { get; init; }

  /// <summary>
  /// Scope context at time of denial.
  /// </summary>
  public required PerspectiveScope Scope { get; init; }

  /// <summary>
  /// Reason for denial.
  /// </summary>
  public required AccessDenialReason Reason { get; init; }

  /// <summary>
  /// When access was denied.
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Reason for access denial.
/// </summary>
public enum AccessDenialReason {
  /// <summary>
  /// The caller did not have the required permission.
  /// </summary>
  InsufficientPermission,

  /// <summary>
  /// The caller did not have the required role.
  /// </summary>
  InsufficientRole,

  /// <summary>
  /// The caller's scope (tenant, user, etc.) did not match the resource.
  /// </summary>
  ScopeViolation,

  /// <summary>
  /// A security policy rejected the access.
  /// </summary>
  PolicyRejected
}
