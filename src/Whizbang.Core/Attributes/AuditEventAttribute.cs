using Whizbang.Core.Audit;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks an event type for selective auditing.
/// Events with this attribute are captured by selective audit perspectives.
/// </summary>
/// <remarks>
/// <para>
/// Use this attribute when you want to audit specific event types rather than all events.
/// The <see cref="Reason"/> property documents why the event requires auditing.
/// </para>
/// <para>
/// For auditing all events, use a global audit perspective without checking for this attribute.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [AuditEvent(Reason = "PII access", Level = AuditLevel.Warning)]
/// public record CustomerDataViewed(Guid CustomerId, string ViewedBy);
///
/// [AuditEvent(Reason = "Payment processed")]
/// public record PaymentCompleted(Guid PaymentId, decimal Amount);
/// </code>
/// </example>
/// <docs>core-concepts/audit-logging#selective-auditing</docs>
/// <tests>Whizbang.Core.Tests/Audit/AuditEventAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class AuditEventAttribute : Attribute {
  /// <summary>
  /// Optional reason documenting why this event requires auditing.
  /// Stored in <see cref="AuditLogEntry.AuditReason"/>.
  /// </summary>
  public string? Reason { get; init; }

  /// <summary>
  /// Audit severity level. Default is <see cref="AuditLevel.Info"/>.
  /// </summary>
  public AuditLevel Level { get; init; } = AuditLevel.Info;
}
