using System.Text.Json;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Tags;

namespace ECommerce.BFF.API.Audit;

/// <summary>
/// Message tag hook that captures events marked with <see cref="AuditEventAttribute"/>
/// and creates <see cref="AuditLogEntry"/> records for compliance tracking.
/// </summary>
/// <remarks>
/// <para>
/// This hook demonstrates the recommended pattern for audit logging in Whizbang:
/// using the message tag system to capture events with cross-cutting concerns.
/// </para>
/// <para>
/// Registration example:
/// <code>
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;AuditEventAttribute, AuditTagHook&gt;();
/// });
/// </code>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Mark an event for auditing
/// [AuditEvent(Reason = "Payment processed", Level = AuditLevel.Info)]
/// public record PaymentProcessedEvent(Guid OrderId, decimal Amount, string Currency) : IEvent;
///
/// // The hook will automatically capture this event when processed
/// </code>
/// </example>
public sealed class AuditTagHook : IMessageTagHook<AuditEventAttribute> {
  private readonly ILogger<AuditTagHook> _logger;
  private readonly IAuditLogWriter? _auditLogWriter;

  /// <summary>
  /// Creates a new audit tag hook.
  /// </summary>
  /// <param name="logger">Logger for diagnostic output</param>
  /// <param name="auditLogWriter">Optional writer for persisting audit entries</param>
  public AuditTagHook(ILogger<AuditTagHook> logger, IAuditLogWriter? auditLogWriter = null) {
    _logger = logger;
    _auditLogWriter = auditLogWriter;
  }

  /// <summary>
  /// Captures an audited event and creates an audit log entry.
  /// </summary>
  public async ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<AuditEventAttribute> context,
      CancellationToken ct) {
    var attribute = context.Attribute;

    // Extract scope values for audit fields
    var tenantId = context.Scope?.TryGetValue("TenantId", out var t) == true ? t?.ToString() : null;
    var userId = context.Scope?.TryGetValue("UserId", out var u) == true ? u?.ToString() : null;
    var userName = context.Scope?.TryGetValue("UserName", out var un) == true ? un?.ToString() : null;
    var correlationId = context.Scope?.TryGetValue("CorrelationId", out var c) == true ? c?.ToString() : null;

    // Create audit entry
    var entry = new AuditLogEntry {
      Id = Guid.NewGuid(),
      StreamId = _extractStreamId(context.Message),
      StreamPosition = 0, // Would come from event metadata in real implementation
      EventType = context.MessageType.Name,
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = tenantId,
      UserId = userId,
      UserName = userName,
      CorrelationId = correlationId,
      Body = context.Payload,
      AuditReason = attribute.Reason
    };

    // Log the audit entry
    _logger.LogInformation(
      "Audit [{Level}]: {EventType} - {Reason}. TenantId={TenantId}, UserId={UserId}",
      attribute.Level,
      entry.EventType,
      attribute.Reason ?? "N/A",
      tenantId ?? "N/A",
      userId ?? "N/A"
    );

    // Persist if writer is available
    if (_auditLogWriter is not null) {
      await _auditLogWriter.WriteAsync(entry, ct);
    }

    // Return null to pass original payload to next hook
    return null;
  }

  private static string _extractStreamId(object message) {
    // Try common patterns for stream ID extraction
    var type = message.GetType();

    // Try OrderId, ProductId, etc.
    foreach (var prop in type.GetProperties()) {
      if (prop.Name.EndsWith("Id") && prop.PropertyType == typeof(Guid)) {
        var value = prop.GetValue(message);
        if (value is Guid id) {
          var prefix = prop.Name[..^2]; // Remove "Id" suffix
          return $"{prefix}-{id}";
        }
      }
    }

    return $"Unknown-{Guid.NewGuid()}";
  }
}

/// <summary>
/// Interface for persisting audit log entries.
/// Implement this to store entries in a database, file, or external service.
/// </summary>
public interface IAuditLogWriter {
  /// <summary>
  /// Writes an audit log entry to persistent storage.
  /// </summary>
  Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default);
}
