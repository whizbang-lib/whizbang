using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// System event emitted when a domain event is audited.
/// Captures metadata about the original event for compliance and audit trail purposes.
/// </summary>
/// <remarks>
/// <para>
/// When system audit is enabled (<c>options.SystemEvents.EnableAudit()</c>), Whizbang
/// emits an <see cref="EventAudited"/> event for each domain event appended to a stream.
/// </para>
/// <para>
/// By default, <b>all events are audited</b>. Use <c>[AuditEvent(Exclude = true)]</c>
/// to opt-out specific event types from auditing.
/// </para>
/// <para>
/// Create a perspective listening for <see cref="EventAudited"/> to persist audit entries:
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Simple audit perspective
/// public class AuditPerspective : IPerspectiveFor&lt;AuditLogEntry, EventAudited&gt; {
///   public AuditLogEntry Apply(AuditLogEntry current, EventAudited @event) {
///     return new AuditLogEntry {
///       Id = @event.Id,
///       StreamId = @event.OriginalStreamId,
///       StreamPosition = @event.OriginalStreamPosition,
///       EventType = @event.OriginalEventType,
///       Timestamp = @event.Timestamp,
///       TenantId = @event.TenantId,
///       UserId = @event.UserId,
///       Body = @event.OriginalBody
///     };
///   }
/// }
/// </code>
/// </example>
/// <docs>core-concepts/system-events#audit</docs>
[AuditEvent(Exclude = true, Reason = "System event - prevents infinite self-auditing loop")]
public sealed record EventAudited : ISystemEvent {
  /// <summary>
  /// Unique identifier for this audit event.
  /// Used as the stream key for routing to the system event stream.
  /// </summary>
  [StreamKey]
  public required Guid Id { get; init; }

  /// <summary>
  /// The type name of the original domain event (e.g., "OrderCreated").
  /// </summary>
  public required string OriginalEventType { get; init; }

  /// <summary>
  /// The stream ID where the original event was appended.
  /// </summary>
  public required string OriginalStreamId { get; init; }

  /// <summary>
  /// The position within the stream where the original event was appended.
  /// </summary>
  public required long OriginalStreamPosition { get; init; }

  /// <summary>
  /// The full body of the original event as JSON.
  /// </summary>
  public required JsonElement OriginalBody { get; init; }

  /// <summary>
  /// When the original event was recorded.
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }

  /// <summary>
  /// Tenant identifier from event scope (copied for filtering).
  /// </summary>
  public string? TenantId { get; init; }

  /// <summary>
  /// User identifier from event scope (copied for filtering).
  /// </summary>
  public string? UserId { get; init; }

  /// <summary>
  /// User display name from event scope (copied for display).
  /// </summary>
  public string? UserName { get; init; }

  /// <summary>
  /// Correlation identifier for distributed tracing.
  /// </summary>
  public string? CorrelationId { get; init; }

  /// <summary>
  /// Causation identifier linking to the triggering event/command.
  /// </summary>
  public string? CausationId { get; init; }

  /// <summary>
  /// Audit reason from <see cref="AuditEventAttribute.Reason"/> if present.
  /// </summary>
  public string? AuditReason { get; init; }

  /// <summary>
  /// Audit level from <see cref="AuditEventAttribute.Level"/> if present.
  /// Defaults to <see cref="AuditLevel.Info"/>.
  /// </summary>
  public AuditLevel AuditLevel { get; init; } = AuditLevel.Info;

  /// <summary>
  /// Generic scope dictionary containing all security context values.
  /// Allows flexible row-based security beyond just TenantId/UserId.
  /// Keys are scope names (e.g., "TenantId", "UserId", "OrganizationId", "Region").
  /// </summary>
  /// <remarks>
  /// This property enables applications to store custom scope values for row-level security.
  /// The individual TenantId, UserId, etc. properties are kept for backward compatibility
  /// and common query patterns, but Scope provides full flexibility.
  /// </remarks>
  public IReadOnlyDictionary<string, string?>? Scope { get; init; }
}
