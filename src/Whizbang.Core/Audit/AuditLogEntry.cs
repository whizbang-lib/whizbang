using System.Text.Json;

namespace Whizbang.Core.Audit;

/// <summary>
/// Read model for audit log entries.
/// Captures event scope (tenant, user, correlation) as queryable fields for compliance scenarios.
/// </summary>
/// <remarks>
/// <para>
/// Audit entries are created by global perspectives that observe all events.
/// Scope fields from <see cref="EventMetadata"/> are copied into the body for efficient querying.
/// </para>
/// <para>
/// This enables compliance queries like:
/// <list type="bullet">
///   <item>Who changed entity X? (filter by StreamId)</item>
///   <item>What did user Y modify? (filter by UserId)</item>
///   <item>When was data accessed? (filter by Timestamp, TenantId)</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Query audit entries for a specific user
/// var userChanges = await auditLens.QueryAsync(q => q
///     .Where(a => a.UserId == userId)
///     .Where(a => a.Timestamp >= startDate)
///     .OrderByDescending(a => a.Timestamp));
///
/// // Query entity history
/// var entityHistory = await auditLens.QueryAsync(q => q
///     .Where(a => a.StreamId == $"Order-{orderId}")
///     .OrderBy(a => a.StreamPosition));
/// </code>
/// </example>
/// <docs>core-concepts/audit-logging</docs>
/// <tests>Whizbang.Core.Tests/Audit/AuditLogEntryTests.cs</tests>
public sealed record AuditLogEntry {
  /// <summary>
  /// Unique identifier for this audit entry.
  /// </summary>
  public required Guid Id { get; init; }

  /// <summary>
  /// The event stream identifier (e.g., "Order-abc123").
  /// </summary>
  public required string StreamId { get; init; }

  /// <summary>
  /// Position within the stream where this event occurred.
  /// </summary>
  public required long StreamPosition { get; init; }

  /// <summary>
  /// The type name of the event (e.g., "OrderCreated").
  /// </summary>
  public required string EventType { get; init; }

  /// <summary>
  /// When the event was recorded.
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }

  /// <summary>
  /// Tenant identifier from event scope (copied for querying).
  /// </summary>
  public string? TenantId { get; init; }

  /// <summary>
  /// User identifier from event scope (copied for querying).
  /// </summary>
  public string? UserId { get; init; }

  /// <summary>
  /// User display name from event scope (copied for querying).
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
  /// The full event body as JSON for detailed inspection.
  /// </summary>
  public required JsonElement Body { get; init; }

  /// <summary>
  /// Optional reason for auditing this event (from <see cref="AuditEventAttribute"/>).
  /// </summary>
  public string? AuditReason { get; init; }
}
