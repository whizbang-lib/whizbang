using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Tags;

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
/// <docs>fundamentals/events/system-events#audit</docs>
[AuditEvent(Exclude = true, Reason = "System event - prevents infinite self-auditing loop")]
// The framework audit tag: membership in the sys-audit coalesce group. EnableAudit() binds the
// built-in coalesce policy to this tag, so audit singles ride the generic tag-bound coalescing
// machinery — zero audit-specific shipping code. Tag set explicitly at the usage site because
// the MessageTagDiscoveryGenerator reads only what is syntactically present here.
[SystemAuditTag(Tag = SystemTags.AUDIT)]
[PinnedId("a917ce3a-52ce-4c20-92de-99ab649c2ebe")]
public sealed record EventAudited : ISystemEvent {
  /// <summary>
  /// Unique identifier for this audit event.
  /// Used as the stream key for routing to the system event stream.
  /// </summary>
  [StreamId]
  public required Guid Id { get; init; }

  /// <summary>
  /// The unique ID of the original domain event (matches event store event_id / outbox message_id).
  /// </summary>
  public Guid OriginalEventId { get; init; }

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
  /// Human-readable name for the ACTIVITY this record represents, supplied per occurrence by an
  /// <see cref="IAuditDecisionHook"/>. Null falls back to humanizing the event type name.
  /// </summary>
  /// <remarks>
  /// The type name is often the wrong unit. A saga's boundary event says "SagaStartedEvent", which
  /// tells an auditor nothing; the activity says "Bulk acknowledgment assignment". Only the hook can
  /// know which activity an occurrence belongs to, so the label has to travel with the record —
  /// the name humanizers run at projection time from the type name alone and cannot produce it.
  /// </remarks>
  public string? ActivityName { get; init; }

  /// <summary>
  /// Human-readable detail for this occurrence, typically drawn from the payload — "Imported 500
  /// records". Null falls back to the description humanizer.
  /// </summary>
  public string? ActivityDescription { get; init; }

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
