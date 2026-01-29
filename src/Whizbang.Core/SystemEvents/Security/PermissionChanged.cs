using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents.Security;

/// <summary>
/// Emitted when a user's permissions or roles change.
/// </summary>
/// <docs>core-concepts/system-events#permission-changed</docs>
/// <tests>Whizbang.Core.Tests/SystemEvents/Security/SecuritySystemEventTests.cs</tests>
[AuditEvent(Exclude = true, Reason = "System event - security events are not self-audited")]
public sealed record PermissionChanged : ISystemEvent {
  /// <summary>
  /// Unique identifier for this event.
  /// </summary>
  [StreamKey]
  public Guid Id { get; init; } = TrackedGuid.NewMedo();

  /// <summary>
  /// User whose permissions changed.
  /// </summary>
  public required string UserId { get; init; }

  /// <summary>
  /// Tenant context.
  /// </summary>
  public required string TenantId { get; init; }

  /// <summary>
  /// Type of change.
  /// </summary>
  public required PermissionChangeType ChangeType { get; init; }

  /// <summary>
  /// Roles added (if any).
  /// </summary>
  public IReadOnlySet<string>? RolesAdded { get; init; }

  /// <summary>
  /// Roles removed (if any).
  /// </summary>
  public IReadOnlySet<string>? RolesRemoved { get; init; }

  /// <summary>
  /// Permissions added (if any).
  /// </summary>
  public IReadOnlySet<Permission>? PermissionsAdded { get; init; }

  /// <summary>
  /// Permissions removed (if any).
  /// </summary>
  public IReadOnlySet<Permission>? PermissionsRemoved { get; init; }

  /// <summary>
  /// Who made the change.
  /// </summary>
  public required string ChangedBy { get; init; }

  /// <summary>
  /// When the change occurred.
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Type of permission change.
/// </summary>
public enum PermissionChangeType {
  /// <summary>
  /// Roles were added to the user.
  /// </summary>
  RolesAdded,

  /// <summary>
  /// Roles were removed from the user.
  /// </summary>
  RolesRemoved,

  /// <summary>
  /// Permissions were directly added to the user.
  /// </summary>
  PermissionsAdded,

  /// <summary>
  /// Permissions were directly removed from the user.
  /// </summary>
  PermissionsRemoved,

  /// <summary>
  /// All roles and permissions were reassigned.
  /// </summary>
  FullReassignment
}
