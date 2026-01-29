using System.Text.Json;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace ECommerce.BFF.API.Audit;

/// <summary>
/// Message tag hook that logs events marked with <see cref="AuditEventAttribute"/>
/// for real-time audit alerts and monitoring.
/// </summary>
/// <remarks>
/// <para>
/// This hook provides <b>real-time logging</b> for audited events. For durable persistence,
/// use a global perspective (see <c>IGlobalPerspectiveFor&lt;AuditLogEntry, Guid, IEvent&gt;</c>).
/// </para>
/// <para>
/// <b>Recommended pattern:</b>
/// <list type="bullet">
///   <item><b>Tag hook</b>: Real-time alerts, logging, external notifications</item>
///   <item><b>Global perspective</b>: Durable persistence via <c>IPerspectiveStore</c></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Mark an event for auditing
/// [AuditEvent(Reason = "Payment processed", Level = AuditLevel.Info)]
/// public record PaymentProcessedEvent(Guid OrderId, decimal Amount, string Currency) : IEvent;
///
/// // Register hook for real-time alerts
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;AuditEventAttribute, AuditTagHook&gt;();
/// });
/// </code>
/// </example>
public sealed class AuditTagHook : IMessageTagHook<AuditEventAttribute> {
  private readonly ILogger<AuditTagHook> _logger;

  /// <summary>
  /// Creates a new audit tag hook.
  /// </summary>
  /// <param name="logger">Logger for audit output</param>
  public AuditTagHook(ILogger<AuditTagHook> logger) {
    _logger = logger;
  }

  /// <summary>
  /// Logs an audited event for real-time monitoring.
  /// Persistence is handled separately by a global perspective.
  /// </summary>
  public ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<AuditEventAttribute> context,
      CancellationToken ct) {
    var attribute = context.Attribute;

    // Extract scope values for logging
    var tenantId = context.Scope?.TryGetValue("TenantId", out var t) == true ? t?.ToString() : null;
    var userId = context.Scope?.TryGetValue("UserId", out var u) == true ? u?.ToString() : null;

    // Log the audit event
    _logger.LogInformation(
      "Audit [{Level}]: {EventType} - {Reason}. TenantId={TenantId}, UserId={UserId}",
      attribute.Level,
      context.MessageType.Name,
      attribute.Reason ?? "N/A",
      tenantId ?? "N/A",
      userId ?? "N/A"
    );

    // Return null to pass original payload to next hook
    return ValueTask.FromResult<JsonElement?>(null);
  }
}
