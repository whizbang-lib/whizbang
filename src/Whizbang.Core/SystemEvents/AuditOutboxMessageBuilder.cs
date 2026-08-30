using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Builds <see cref="EventAudited"/> outbox messages from domain event outbox messages.
/// Used by the work coordinator strategy to generate audit trail entries when events are queued.
/// </summary>
/// <docs>fundamentals/events/system-events#audit-builder</docs>
public static partial class AuditOutboxMessageBuilder {
  /// <summary>
  /// Attempts to build an audit <see cref="OutboxMessage"/> from a domain event outbox message.
  /// Returns null if the event should not be audited (excluded, not eligible, etc.).
  /// </summary>
  /// <param name="eventMessage">The domain event outbox message.</param>
  /// <param name="options">System event options controlling audit behavior.</param>
  /// <param name="logger">Optional logger; a resolution failure is logged rather than silently defaulting.</param>
  /// <returns>An audit outbox message, or null if the event should not be audited.</returns>
  public static OutboxMessage? TryBuildAuditMessage(OutboxMessage eventMessage, SystemEventOptions options, ILogger? logger = null, IAuditDecisionHook? auditDecisionHook = null) {
    if (!eventMessage.IsEvent || !options.EventAuditEnabled) {
      return null;
    }

    // Check if this event type should be audited based on AuditMode
    // One place decides, shared with the decorator: the attribute gates the TYPE, the hook may
    // veto or name the OCCURRENCE. These two call sites previously each had their own copy.
    var eventType = _resolveEventType(eventMessage.MessageType, logger ?? NullLogger.Instance);
    var auditDecision = eventType != null
      ? AuditEligibility.Decide(eventMessage.Envelope.Payload, eventType, options.AuditMode, auditDecisionHook)
      : AuditDecision.Record();
    if (eventType != null && !auditDecision.ShouldAudit) {
      return null;
    }

    // Extract full type name (namespace + type, without assembly qualifier)
    var eventTypeName = _extractFullTypeName(eventMessage.MessageType);

    // Build scope dictionary from the event's scope
    Dictionary<string, string?>? scope = null;
    if (eventMessage.Scope != null) {
      scope = [];
      if (eventMessage.Scope.TenantId != null) {
        scope["TenantId"] = eventMessage.Scope.TenantId;
      }
      if (eventMessage.Scope.UserId != null) {
        scope["UserId"] = eventMessage.Scope.UserId;
      }
    }

    // Extract correlation ID from envelope hops
    string? correlationId = null;
    if (eventMessage.Metadata.Hops is { Count: > 0 }) {
      var firstHop = eventMessage.Metadata.Hops[0];
      if (firstHop.Metadata != null &&
          firstHop.Metadata.TryGetValue("CorrelationId", out var corrElem) &&
          corrElem.ValueKind == JsonValueKind.String) {
        correlationId = corrElem.GetString();
      }
    }

    // Build the EventAudited payload
    var auditEvent = new EventAudited {
      Id = TrackedGuid.NewMedo(),
      OriginalEventId = eventMessage.MessageId,
      OriginalEventType = eventTypeName,
      OriginalStreamId = eventMessage.StreamId?.ToString() ?? string.Empty,
      OriginalStreamPosition = 0, // Position not available from outbox message
      OriginalBody = eventMessage.Envelope.Payload,
      Timestamp = DateTimeOffset.UtcNow,
      ActivityName = auditDecision.Name,
      ActivityDescription = auditDecision.Description,
      TenantId = eventMessage.Scope?.TenantId,
      UserId = eventMessage.Scope?.UserId,
      CorrelationId = correlationId,
      Scope = scope
    };

    // Serialize EventAudited to JsonElement
    var auditJson = AuditJsonSerializer.SerializeToJsonElement(auditEvent);

    // The audit record's OWN hop carries its scope: the audited tenant plus the system marker, via
    // the same helper every audit path uses. Not the acting user — scope is an access-control key,
    // so carrying the actor would hand the SUBJECT of an audit record a key to their own trail.
    //
    // The source hops were previously copied wholesale for exactly that security context, which is
    // what brought the user along. They are kept for their LINEAGE and demoted to Causation: scope
    // resolution merges Current hops only, so the trace back to the audited event survives while
    // its authority does not. The consumer still establishes a context, because the extractor needs
    // either a tenant or a user and the tenant is present.
    var sourceHops = (eventMessage.Envelope.Hops ?? [])
      .Select(h => h with { Type = HopType.Causation })
      .ToList();
    sourceHops.Insert(0, new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      TraceParent = System.Diagnostics.Activity.Current?.Id,
      Scope = AuditRecordScope.For(eventMessage.Scope?.TenantId),
    });

    var auditEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = auditJson,
      Hops = sourceHops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };

    // No floor stamping here: the sliding-ship safety floor (ScheduledFor = now + MaxDelay) and
    // the sys-audit group ride the GENERIC coalesce mint path — EventAudited carries
    // [SystemAuditTag], EnableAudit() registers the built-in Coalesce(SystemTags.AUDIT) binding,
    // and CoalesceGroupResolver stamps at every mint seam. The builder builds; the resolver
    // stamps. Slide = 0 registers no binding, keeping immediate per-event shipping.
    var auditEventType = typeof(EventAudited);
    return new OutboxMessage {
      MessageId = auditEnvelope.MessageId.Value,
      Destination = AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION,
      Envelope = auditEnvelope,
      Metadata = new EnvelopeMetadata {
        MessageId = auditEnvelope.MessageId,
        Hops = auditEnvelope.Hops?.ToList() ?? []
      },
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{auditEventType.AssemblyQualifiedName}]], Whizbang.Core",
      StreamId = auditEvent.Id,
      IsEvent = false, // Audit events are NOT stored in event store — only published to transport
      Scope = eventMessage.Scope,
      MessageType = auditEventType.AssemblyQualifiedName ?? auditEventType.FullName ?? auditEventType.Name
    };
  }

  private static bool _shouldAudit(Type eventType, SystemEventOptions options) {
    // EventAudited itself is excluded (prevents infinite loop)
    if (eventType == typeof(EventAudited)) {
      return false;
    }

    var attr = eventType
        .GetCustomAttributes(typeof(AuditEventAttribute), inherit: true)
        .FirstOrDefault() as AuditEventAttribute;

    return options.AuditMode == AuditMode.OptOut
      ? attr?.Exclude != true           // audit unless excluded
      : attr?.Exclude == false;         // audit only if marked
  }

  private static Type? _resolveEventType(string assemblyQualifiedName, ILogger logger) {
    try {
#pragma warning disable IL2057 // Type.GetType with dynamic string — needed to resolve event type for audit attribute check
      var resolved = Type.GetType(assemblyQualifiedName);
#pragma warning restore IL2057
      if (resolved is null) {
        LogAuditTypeUnresolved(logger, assemblyQualifiedName);
      }
      return resolved;
    } catch (Exception ex) {
      // A resolution failure silently falls back to the default audit decision — log it so a
      // renamed/removed event type showing up here is diagnosable rather than invisible.
      LogAuditTypeResolveFailed(logger, ex, assemblyQualifiedName);
      return null;
    }
  }

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Audit type name '{TypeName}' could not be resolved (Type.GetType returned null); the audit include/exclude decision falls back to the default.")]
  private static partial void LogAuditTypeUnresolved(ILogger logger, string typeName);

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Audit type name '{TypeName}' threw during resolution; the audit include/exclude decision falls back to the default.")]
  private static partial void LogAuditTypeResolveFailed(ILogger logger, Exception ex, string typeName);

  private static string _extractFullTypeName(string messageType) =>
    TypeNameFormatter.GetFullName(messageType);

}
