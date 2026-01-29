using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents.Security;

/// <summary>
/// Emitted when a scope context is established for a request/operation.
/// Useful for auditing request authentication.
/// </summary>
/// <docs>core-concepts/system-events#scope-context-established</docs>
/// <tests>Whizbang.Core.Tests/SystemEvents/Security/SecuritySystemEventTests.cs</tests>
[AuditEvent(Exclude = true, Reason = "System event - security events are not self-audited")]
public sealed record ScopeContextEstablished : ISystemEvent {
  /// <summary>
  /// Unique identifier for this event.
  /// </summary>
  [StreamKey]
  public Guid Id { get; init; } = TrackedGuid.NewMedo();

  /// <summary>
  /// The established scope.
  /// </summary>
  public required PerspectiveScope Scope { get; init; }

  /// <summary>
  /// Roles in the context.
  /// </summary>
  public required IReadOnlySet<string> Roles { get; init; }

  /// <summary>
  /// Permissions in the context.
  /// </summary>
  public required IReadOnlySet<Permission> Permissions { get; init; }

  /// <summary>
  /// Source of the context (JWT, API Key, etc.).
  /// </summary>
  public required string Source { get; init; }

  /// <summary>
  /// When the context was established.
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }
}
