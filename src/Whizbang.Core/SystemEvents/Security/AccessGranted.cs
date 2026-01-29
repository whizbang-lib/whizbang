using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents.Security;

/// <summary>
/// Emitted when access to a sensitive resource is granted.
/// Useful for audit trails of privileged access.
/// </summary>
/// <docs>core-concepts/system-events#access-granted</docs>
/// <tests>Whizbang.Core.Tests/SystemEvents/Security/SecuritySystemEventTests.cs</tests>
[AuditEvent(Exclude = true, Reason = "System event - security events are not self-audited")]
public sealed record AccessGranted : ISystemEvent {
  /// <summary>
  /// Unique identifier for this event.
  /// </summary>
  [StreamKey]
  public Guid Id { get; init; } = TrackedGuid.NewMedo();

  /// <summary>
  /// Type of resource access was granted to.
  /// </summary>
  public required string ResourceType { get; init; }

  /// <summary>
  /// Optional resource identifier.
  /// </summary>
  public string? ResourceId { get; init; }

  /// <summary>
  /// The permission that was used.
  /// </summary>
  public required Permission UsedPermission { get; init; }

  /// <summary>
  /// Access filter applied.
  /// </summary>
  public required ScopeFilter AccessFilter { get; init; }

  /// <summary>
  /// Scope context at time of access.
  /// </summary>
  public required PerspectiveScope Scope { get; init; }

  /// <summary>
  /// When access was granted.
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }
}
