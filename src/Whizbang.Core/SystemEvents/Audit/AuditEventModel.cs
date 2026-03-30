using System.Text.RegularExpressions;

namespace Whizbang.Core.SystemEvents.Audit;

/// <summary>
/// Simple key-value entry for EF Core JSONB compatibility (KeyValuePair lacks parameterless ctor).
/// </summary>
public class ScopeEntry {
  /// <summary>The scope entry key (e.g., "TenantId", "UserId").</summary>
  public string Key { get; set; } = string.Empty;
  /// <summary>The scope entry value, or null if not set.</summary>
  public string? Value { get; set; }
}

/// <summary>
/// Read model for audit events materialized from <see cref="EventAudited"/>.
/// Stored in the <c>wh_per_audit_event</c> perspective table.
/// </summary>
/// <docs>fundamentals/events/system-events#audit-model</docs>
public class AuditEventModel {
  /// <summary>Unique identifier for this audit record.</summary>
  [StreamId]
  public Guid Id { get; set; }
  /// <summary>When this audit record was created.</summary>
  public DateTimeOffset CreatedAt { get; set; }
  /// <summary>When this audit record was last updated.</summary>
  public DateTimeOffset UpdatedAt { get; set; }

  /// <summary>The ID of the original event that was audited.</summary>
  public Guid OriginalEventId { get; set; }
  /// <summary>Fully-qualified type name of the original event.</summary>
  public string EventType { get; set; } = string.Empty;
  /// <summary>Human-readable name derived from the event type.</summary>
  public string EventName { get; set; } = string.Empty;
  /// <summary>Human-readable description derived from the event namespace/domain.</summary>
  public string EventDescription { get; set; } = string.Empty;
  /// <summary>Version of the event schema.</summary>
  public string EventVersion { get; set; } = "1.0";
  /// <summary>Stream ID of the original event.</summary>
  public string EventStreamId { get; set; } = string.Empty;
  /// <summary>Position of the original event within its stream.</summary>
  public long EventStreamPosition { get; set; }
  /// <summary>Timestamp when the original event occurred.</summary>
  public DateTimeOffset OccurredAt { get; set; }

  /// <summary>Snapshot of the audited event's scope context (TenantId, UserId, name, email, etc.).</summary>
  public List<ScopeEntry>? OriginalScope { get; set; }

  /// <summary>Correlation ID for distributed tracing.</summary>
  public string? CorrelationId { get; set; }
  /// <summary>Causation ID for distributed tracing.</summary>
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
/// <docs>fundamentals/events/system-events#audit-projection</docs>
public static class AuditEventProjection {
  private const string PASCAL_CASE_SPACE_REPLACEMENT = "$1 $2";

  /// <summary>
  /// Global custom humanizer set via <see cref="SystemEventOptions.EventNameHumanizer"/>.
  /// Called by <see cref="HumanizeEventType"/> before the default logic.
  /// </summary>
  public static Func<string, string?>? CustomHumanizer { get; set; }

  /// <summary>
  /// Global custom description humanizer set via <see cref="SystemEventOptions.EventDescriptionHumanizer"/>.
  /// Called by <see cref="HumanizeNamespace"/> before the default logic.
  /// </summary>
  public static Func<string, string?>? CustomDescriptionHumanizer { get; set; }

  /// <summary>
  /// Applies an <see cref="EventAudited"/> event to produce an <see cref="AuditEventModel"/>.
  /// </summary>
  /// <param name="currentData">The current model state (unused for create-only projections).</param>
  /// <param name="event">The audit event to apply.</param>
  /// <param name="eventNameHumanizer">
  /// Optional custom function to humanize event type names. Receives the raw event type name
  /// and returns a human-readable label, or null to fall back to the default humanizer.
  /// </param>
  public static AuditEventModel Apply(
      AuditEventModel currentData,
      EventAudited @event,
      Func<string, string?>? eventNameHumanizer = null) {
    var humanizer = eventNameHumanizer ?? CustomHumanizer;
    var humanizedName = HumanizeEventType(@event.OriginalEventType, humanizer);
    return new AuditEventModel {
      Id = @event.Id,
      CreatedAt = @event.Timestamp,
      UpdatedAt = @event.Timestamp,
      OriginalEventId = @event.OriginalEventId,
      EventType = @event.OriginalEventType,
      EventName = humanizedName,
      EventDescription = HumanizeNamespace(@event.OriginalEventType, CustomDescriptionHumanizer),
      EventVersion = "1.0",
      EventStreamId = @event.OriginalStreamId,
      EventStreamPosition = @event.OriginalStreamPosition,
      OccurredAt = @event.Timestamp,
      OriginalScope = @event.Scope?.Select(kvp => new ScopeEntry { Key = kvp.Key, Value = kvp.Value }).ToList(),
      CorrelationId = @event.CorrelationId,
      CausationId = @event.CausationId
    };
  }

  /// <summary>
  /// Converts a fully-qualified event type name into a human-readable label.
  /// If a custom humanizer is provided and returns a non-null value, that value is used.
  /// Otherwise falls back to the built-in logic.
  /// </summary>
  /// <param name="eventType">The raw event type name (e.g., "JobCreatedEvent").</param>
  /// <param name="customHumanizer">Optional override. Return null to fall back to default.</param>
  /// <returns>A human-readable label (e.g., "Job Created").</returns>
  public static string HumanizeEventType(string eventType, Func<string, string?>? customHumanizer = null) {
    // Try custom humanizer first
    if (customHumanizer != null) {
      var custom = customHumanizer(eventType);
      if (custom != null) {
        return custom;
      }
    }

    // Strip namespace (dots) but preserve nested type context (plus signs)
    // e.g. "JDX.Contracts.Session.SessionContracts+EndedEvent" → "SessionContracts+EndedEvent"
    var withoutNamespace = TypeNameFormatter.GetSimpleName(eventType);

    // Split on '+' to get nested type segments
    // e.g. "SessionContracts+EndedEvent" → ["SessionContracts", "EndedEvent"]
    var segments = withoutNamespace.Split('+');

    // Humanize each segment: remove "Contracts"/"Event" suffixes, insert spaces
    var humanized = new List<string>();
    for (var i = 0; i < segments.Length; i++) {
      var seg = segments[i];
      // Last segment: remove "Event" suffix
      if (i == segments.Length - 1 && seg.EndsWith("Event", StringComparison.Ordinal)) {
        seg = seg[..^5];
      }
      // All segments: remove "Contracts" suffix
      if (seg.EndsWith("Contracts", StringComparison.Ordinal)) {
        seg = seg[..^9];
      }
      // Insert spaces in PascalCase, keeping acronyms together
      seg = Regex.Replace(seg, "([A-Z]+)([A-Z][a-z])", PASCAL_CASE_SPACE_REPLACEMENT, RegexOptions.None, TimeSpan.FromSeconds(1));
      seg = Regex.Replace(seg, "([a-z])([A-Z])", PASCAL_CASE_SPACE_REPLACEMENT, RegexOptions.None, TimeSpan.FromSeconds(1));
      if (!string.IsNullOrWhiteSpace(seg)) {
        humanized.Add(seg.Trim());
      }
    }

    return humanized.Count > 1
      ? string.Join(" → ", humanized)   // "Session → Ended"
      : humanized.FirstOrDefault() ?? eventType;
  }

  /// <summary>
  /// Extracts and humanizes the namespace/domain of an event type for use as a description.
  /// If a custom humanizer is provided and returns a non-null value, that value is used.
  /// Otherwise falls back to built-in namespace extraction.
  /// </summary>
  /// <param name="eventType">The raw event type name.</param>
  /// <param name="customHumanizer">Optional override. Return null to fall back to default.</param>
  /// <returns>A humanized namespace description (e.g., "Session", "Job → Bulk Import").</returns>
  public static string HumanizeNamespace(string eventType, Func<string, string?>? customHumanizer = null) {
    if (customHumanizer != null) {
      var custom = customHumanizer(eventType);
      if (custom != null) {
        return custom;
      }
    }

    // Strip namespace (dots) first
    var lastDot = eventType.LastIndexOf('.');
    var withoutNamespace = lastDot >= 0 ? eventType[(lastDot + 1)..] : eventType;

    // Split on '+' to get nested type segments
    var plusSegments = withoutNamespace.Split('+');
    if (plusSegments.Length <= 1) {
      // No nested types — use the dot-namespace as context if available
      if (lastDot >= 0) {
        return _humanizeSegments(eventType[..lastDot].Split('.'));
      }
      return string.Empty;
    }

    // Use all segments EXCEPT the last one (the event itself) as the domain context
    // e.g. "SessionContracts+EndedEvent" → humanize "SessionContracts" → "Session"
    // e.g. "ChatConversationsContracts+AgentMessage+SentEvent" → "Chat Conversations → Agent Message"
    return _humanizeSegments(plusSegments[..^1]);
  }

  private static string _humanizeSegments(string[] segments) {
    var meaningful = new List<string>();
    foreach (var seg in segments) {
      var name = seg;
      // Humanize PascalCase, keeping acronyms together
      name = Regex.Replace(name, "([A-Z]+)([A-Z][a-z])", PASCAL_CASE_SPACE_REPLACEMENT, RegexOptions.None, TimeSpan.FromSeconds(1));
      name = Regex.Replace(name, "([a-z])([A-Z])", PASCAL_CASE_SPACE_REPLACEMENT, RegexOptions.None, TimeSpan.FromSeconds(1));
      if (!string.IsNullOrWhiteSpace(name)) {
        meaningful.Add(name.Trim());
      }
    }
    return meaningful.Count > 0 ? string.Join(" → ", meaningful) : string.Empty;
  }
}
