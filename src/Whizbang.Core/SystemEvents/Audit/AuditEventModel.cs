using System.Text.RegularExpressions;

namespace Whizbang.Core.SystemEvents.Audit;

/// <summary>
/// Simple key-value entry for EF Core JSONB compatibility (KeyValuePair lacks parameterless ctor).
/// </summary>
public class ScopeEntry {
  public string Key { get; set; } = string.Empty;
  public string? Value { get; set; }
}

/// <summary>
/// Read model for audit events materialized from <see cref="EventAudited"/>.
/// Stored in the <c>wh_per_audit_event</c> perspective table.
/// </summary>
/// <docs>core-concepts/system-events#audit-model</docs>
public class AuditEventModel {
  [StreamId]
  public Guid Id { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }

  // Audit fields
  public string EventType { get; set; } = string.Empty;
  public string EventName { get; set; } = string.Empty;
  public string EventDescription { get; set; } = string.Empty;
  public string EventVersion { get; set; } = "1.0";
  public string EventStreamId { get; set; } = string.Empty;
  public long EventStreamPosition { get; set; }
  public DateTimeOffset OccurredAt { get; set; }

  // Snapshot of the audited event's scope context (TenantId, UserId, name, email, etc.)
  public List<ScopeEntry>? CapturedScope { get; set; }

  // Correlation/causation for distributed tracing
  public string? CorrelationId { get; set; }
  public string? CausationId { get; set; }
}

/// <summary>
/// Projection logic for materializing <see cref="EventAudited"/> into <see cref="AuditEventModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// This class does NOT implement <c>IPerspectiveFor</c> directly because that would
/// trigger source generators on Whizbang.Core itself. Instead, consumer projects should
/// create a thin wrapper:
/// </para>
/// <code>
/// public class AuditProjection : IPerspectiveFor&lt;AuditEventModel, EventAudited&gt; {
///   public AuditEventModel Apply(AuditEventModel current, EventAudited @event)
///     =&gt; AuditEventProjection.Apply(current, @event);
/// }
/// </code>
/// <para>
/// Or when using <c>SubscribeToAudit()</c>, the source generator in the consumer project
/// will discover the perspective automatically from the referenced assembly.
/// </para>
/// </remarks>
/// <docs>core-concepts/system-events#audit-projection</docs>
public static class AuditEventProjection {
  public static AuditEventModel Apply(AuditEventModel currentData, EventAudited @event) {
    return new AuditEventModel {
      Id = @event.Id,
      CreatedAt = @event.Timestamp,
      UpdatedAt = @event.Timestamp,
      EventType = @event.OriginalEventType,
      EventName = HumanizeEventType(@event.OriginalEventType),
      EventDescription = $"Event {@event.OriginalEventType} at position {@event.OriginalStreamPosition}",
      EventVersion = "1.0",
      EventStreamId = @event.OriginalStreamId,
      EventStreamPosition = @event.OriginalStreamPosition,
      OccurredAt = @event.Timestamp,
      CapturedScope = @event.Scope?.Select(kvp => new ScopeEntry { Key = kvp.Key, Value = kvp.Value }).ToList(),
      CorrelationId = @event.CorrelationId,
      CausationId = @event.CausationId
    };
  }

  /// <summary>
  /// Converts a fully-qualified event type name into a human-readable label.
  /// e.g. "JDX.Contracts.Job.JobCreatedEvent" → "Job Created"
  /// </summary>
  internal static string HumanizeEventType(string eventType) {
    // Take after last '.' to strip namespace
    var name = eventType.Contains('.')
      ? eventType[(eventType.LastIndexOf('.') + 1)..]
      : eventType;

    // Remove "Event" suffix
    if (name.EndsWith("Event", StringComparison.Ordinal)) {
      name = name[..^5];
    }

    // Insert spaces before capitals (e.g. "JobCreated" → "Job Created")
    return Regex.Replace(name, "(?<!^)([A-Z])", " $1");
  }
}
