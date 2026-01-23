using System.Diagnostics.CodeAnalysis;
using Whizbang.Core.Audit;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks an event type for selective auditing via the message tag system.
/// Events with this attribute are captured by audit hooks registered with TagOptions.
/// </summary>
/// <remarks>
/// <para>
/// Use this attribute when you want to audit specific event types rather than all events.
/// The <see cref="Reason"/> property documents why the event requires auditing.
/// </para>
/// <para>
/// This attribute inherits from <see cref="MessageTagAttribute"/> to integrate with
/// the message tag hook system. Register an <c>IMessageTagHook&lt;AuditEventAttribute&gt;</c>
/// to capture audited events.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Mark an event for auditing
/// [AuditEvent(Reason = "PII access", Level = AuditLevel.Warning)]
/// public record CustomerDataViewed(Guid CustomerId, string ViewedBy) : IEvent;
///
/// // Register audit hook
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;AuditEventAttribute, AuditTagHook&gt;();
/// });
/// </code>
/// </example>
/// <docs>core-concepts/audit-logging#selective-auditing</docs>
/// <tests>Whizbang.Core.Tests/Audit/AuditEventAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class AuditEventAttribute : MessageTagAttribute {
  /// <summary>
  /// Creates a new audit event attribute with default tag "audit".
  /// </summary>
  [SetsRequiredMembers]
  public AuditEventAttribute() {
    Tag = "audit";
    IncludeEvent = true; // Always include full event body for audit
  }

  /// <summary>
  /// Optional reason documenting why this event requires auditing.
  /// Stored in <see cref="AuditLogEntry.AuditReason"/>.
  /// </summary>
  public string? Reason { get; init; }

  /// <summary>
  /// Audit severity level. Default is <see cref="AuditLevel.Info"/>.
  /// </summary>
  public AuditLevel Level { get; init; } = AuditLevel.Info;

  /// <summary>
  /// When <c>true</c>, excludes this event type from system audit.
  /// Default is <c>false</c> (all events are audited when system audit is enabled).
  /// </summary>
  /// <remarks>
  /// <para>
  /// When system audit is enabled via <c>options.SystemEvents.EnableAudit()</c>,
  /// all domain events are audited by default. Use this property to opt-out
  /// high-frequency or non-essential event types.
  /// </para>
  /// <para>
  /// Consider setting <see cref="Reason"/> to document why the event is excluded.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Exclude high-frequency event from audit
  /// [AuditEvent(Exclude = true, Reason = "High-frequency heartbeat event")]
  /// public record HeartbeatEvent(Guid ServiceId) : IEvent;
  /// </code>
  /// </example>
  public bool Exclude { get; init; }
}
