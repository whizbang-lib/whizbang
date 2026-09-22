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
/// <docs>fundamentals/events/system-events#scope-context-established</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/Security/SecuritySystemEventTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityContextProviderTests.cs:EstablishContextAsync_EnableAuditLoggingTrue_EmitsAuditEventAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/Security/SecuritySystemEventTests.cs:ScopeContextEstablished_HasAllFieldsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/Security/SecuritySystemEventTests.cs:ScopeContextEstablished_IsSystemEvent_ReturnsTrueAsync</tests>
[AuditEvent(Exclude = true, Reason = "System event - security events are not self-audited")]
[PinnedId("8c5ce34e-8a74-4bb6-b56f-07e33fb8716c")]
public sealed record ScopeContextEstablished : ISystemEvent {
  /// <summary>
  /// Unique identifier for this event.
  /// </summary>
  [StreamId]
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
