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
  public static OutboxMessage? TryBuildAuditMessage(OutboxMessage eventMessage, SystemEventOptions options, ILogger? logger = null) {
    if (!eventMessage.IsEvent || !options.EventAuditEnabled) {
      return null;
    }

    // Check if this event type should be audited based on AuditMode
    var eventType = _resolveEventType(eventMessage.MessageType, logger ?? NullLogger.Instance);
    if (eventType != null && !_shouldAudit(eventType, options)) {
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
      TenantId = eventMessage.Scope?.TenantId,
      UserId = eventMessage.Scope?.UserId,
      CorrelationId = correlationId,
      Scope = scope
    };

    // Serialize EventAudited to JsonElement
    var auditJson = AuditJsonSerializer.SerializeToJsonElement(auditEvent);

    // Build envelope — copy hops from the original event so security context (TenantId, UserId, claims)
    // propagates to the consuming service (BFF). The hops carry scope metadata that the
    // DefaultMessageSecurityContextProvider uses to establish security context.
    var sourceHops = eventMessage.Envelope.Hops?.ToList() ?? [];
    // Add a new hop indicating this is an audit relay
    sourceHops.Add(new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      TraceParent = System.Diagnostics.Activity.Current?.Id
    });

    var auditEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = auditJson,
      Hops = sourceHops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };

    // Sliding-ship safety floor: with the batched shipper enabled (AuditShipSlideSeconds > 0),
    // the single is minted DEFERRED — durable in the SAME transaction as the audited event, but
    // invisible to the normal claim pump until the max-delay deadline. A shipper folds it into a
    // batched composite well before then; if none ever runs, the single ships individually at the
    // deadline: degraded, never lost. Slide = 0 keeps today's immediate per-event shipping.
    // Stamped HERE (not at the append seams) so every mint path — the queued strategy path and
    // the stream-affinity batcher path — carries the same floor.
    DateTimeOffset? scheduledFor = options.AuditShipSlideSeconds > 0
      ? DateTimeOffset.UtcNow.AddSeconds(options.AuditShipMaxDelaySeconds)
      : null;

    var auditEventType = typeof(EventAudited);
    return new OutboxMessage {
      MessageId = auditEnvelope.MessageId.Value,
      ScheduledFor = scheduledFor,
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
