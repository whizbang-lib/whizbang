using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// System event emitted when a command is audited.
/// Captures metadata about the command for compliance and debugging.
/// </summary>
/// <remarks>
/// <para>
/// CommandAudited is the command equivalent of <see cref="EventAudited"/>.
/// It captures information about commands processed by receptors.
/// </para>
/// <para>
/// Unlike EventAudited which captures events after they're stored,
/// CommandAudited captures commands when they're received by a receptor.
/// </para>
/// <para>
/// This event has <c>[AuditEvent(Exclude = true)]</c> to prevent self-auditing
/// loops - we don't want to audit the audit event itself.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Enable command auditing in Program.cs
/// services.AddWhizbang(options => {
///   options.SystemEvents.EnableCommandAudit(); // Audit commands only
///   // or
///   options.SystemEvents.EnableAudit(); // Audit both events and commands
/// });
/// </code>
/// </example>
/// <docs>core-concepts/audit-logging#command-auditing</docs>
[AuditEvent(Exclude = true, Reason = "System event - prevents infinite self-auditing loop")]
public sealed record CommandAudited : ISystemEvent {
  /// <summary>
  /// Unique identifier for this audit entry.
  /// </summary>
  [StreamKey]
  public required Guid Id { get; init; }

  /// <summary>
  /// Fully qualified type name of the command.
  /// </summary>
  public required string CommandType { get; init; }

  /// <summary>
  /// JSON representation of the command body.
  /// </summary>
  public required JsonElement CommandBody { get; init; }

  /// <summary>
  /// When the command was processed.
  /// </summary>
  public required DateTimeOffset Timestamp { get; init; }

  /// <summary>
  /// Tenant context from the command scope, if available.
  /// </summary>
  public string? TenantId { get; init; }

  /// <summary>
  /// User ID from the command scope, if available.
  /// </summary>
  public string? UserId { get; init; }

  /// <summary>
  /// User name from the command scope, if available.
  /// </summary>
  public string? UserName { get; init; }

  /// <summary>
  /// Correlation ID for distributed tracing, if available.
  /// </summary>
  public string? CorrelationId { get; init; }

  /// <summary>
  /// Causation ID (ID of the message that caused this command), if available.
  /// </summary>
  public string? CausationId { get; init; }

  /// <summary>
  /// Optional reason for the audit (e.g., "PII access", "Financial transaction").
  /// </summary>
  public string? AuditReason { get; init; }

  /// <summary>
  /// Audit level for categorization.
  /// </summary>
  public AuditLevel AuditLevel { get; init; } = AuditLevel.Info;

  /// <summary>
  /// Name of the receptor that handled the command.
  /// </summary>
  public string? ReceptorName { get; init; }

  /// <summary>
  /// Type of the response returned by the receptor, if applicable.
  /// </summary>
  public string? ResponseType { get; init; }

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
