// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
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
  /// <param name="auditDecisionHook">The registered decision hook; null behaves as no opinion.</param>
  /// <param name="eventTypes">
  /// Resolves the event's type through the message-type catalog, as the ledger rebuild does; null resolves nothing, so
  /// every type is decided by the audit mode alone.
  /// </param>
  /// <returns>An audit outbox message, or null if the event should not be audited.</returns>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerRebuildTests.cs:LiveAndRebuild_AgreeWhereTypeGetTypeAndTheCatalogDisagree_UnderEitherModeAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:StoredStream_IsTheStreamTheProducerStoredItOnAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditOutboxMessageBuilderTests.cs:TryBuildAuditMessage_OptInMode_UnresolvableType_IsNotAuditedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditOutboxMessageBuilderTests.cs:TryBuildAuditMessage_OptInMode_UnresolvableType_WarnsThatItWasNotAuditedNamingTheTypeAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerRebuildTests.cs:LiveAndRebuild_AgreeOnAnUnresolvableType_UnderEitherModeAsync</tests>
  /// <remarks>
  /// An event type that cannot be resolved is decided as the ledger rebuild decides it (#1980): audited under
  /// <see cref="AuditMode.OptOut"/>, not audited under <see cref="AuditMode.OptIn"/>, and every such skip logs a warning
  /// naming the type. The type is resolved through <paramref name="eventTypes"/>, the catalog the rebuild resolves
  /// through, never by reflection, so the two paths agree on which types they can resolve (#2040).
  /// </remarks>
  public static OutboxMessage? TryBuildAuditMessage(
      OutboxMessage eventMessage, SystemEventOptions options, ILogger? logger = null, IAuditDecisionHook? auditDecisionHook = null,
      AuditTypeResolver? eventTypes = null) {
    if (!eventMessage.IsEvent || !options.EventAuditEnabled) {
      return null;
    }

    // Check if this event type should be audited based on AuditMode
    // One place decides, shared with the decorator: the attribute gates the TYPE, the hook may
    // veto or name the OCCURRENCE. These two call sites previously each had their own copy.
    // The dispatcher stamped the ephemeral flag on the message, so it decides here without the type (#1730).
    var isEphemeral = (eventMessage.Flags & EventFlags.Ephemeral) != 0;
    var eventType = _resolveEventType(eventTypes, eventMessage.MessageType, logger ?? NullLogger.Instance);
    var auditDecision = _decide(eventMessage, eventType, isEphemeral, options, auditDecisionHook, logger ?? NullLogger.Instance);
    if (!auditDecision.ShouldAudit) {
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

    var correlationId = _correlationIdOf(eventMessage);
    var attribute = eventType is null ? null : AuditEligibility.AttributeOf(eventType);

    // Build the EventAudited payload
    var auditEvent = new EventAudited {
      // Derived from the event it audits, so a record derived again is the same record (#1724).
      Id = AuditLedger.RecordIdFor(eventMessage.MessageId),
      OriginalEventId = eventMessage.MessageId,
      OriginalEventType = eventTypeName,
      OriginalStreamId = eventMessage.StreamId?.ToString() ?? string.Empty,
      // The event has no version yet: the emit chain assigns it when the event is stored. 0 here, and
      // store_outbox_messages fills the stored version in (migration 216).
      OriginalStreamPosition = 0,
      // The body only when kept (#1588); otherwise the record points at the event.
      OriginalBody = AuditEligibility.IncludesBody(eventType, options.AuditBodyDefault) ? eventMessage.Envelope.Payload : null,
      Timestamp = DateTimeOffset.UtcNow,
      ActivityName = auditDecision.Name,
      ActivityDescription = auditDecision.Description,
      TenantId = eventMessage.Scope?.TenantId,
      UserId = eventMessage.Scope?.UserId,
      CorrelationId = correlationId,
      CausationId = eventMessage.Envelope.GetCausationId()?.ToString(),
      AuditReason = attribute?.Reason,
      AuditLevel = attribute?.Level ?? Whizbang.Core.Audit.AuditLevel.Info,
      Scope = scope
    };

    // The source hops are kept for their LINEAGE and demoted to Causation; see ToOutboxMessage.
    var sourceHops = (eventMessage.Envelope.Hops ?? [])
      .ConvertAll(h => h with { Type = HopType.Causation });
    return ToOutboxMessage(auditEvent, sourceHops, eventMessage.Scope, options);
  }

  /// <summary>
  /// The outbox row that carries <paramref name="auditEvent"/> to the audit topic, on its tenant's ledger. Shared by the
  /// live outbox path and the ledger rebuild (#1724), so a re-derived record travels exactly as the original did.
  /// </summary>
  /// <param name="auditEvent">The record.</param>
  /// <param name="causationHops">The audited event's hops, already demoted to causation; the record's own hop goes first.</param>
  /// <param name="scope">The audited event's scope, stored on the row.</param>
  /// <param name="options">The audit options, which give the record its priority band.</param>
  /// <returns>The outbox row.</returns>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditLedgerRebuildTests.cs:Rederive_AnEligibleEvent_IsARecordOfItOnTheTenantLedgerAsync</tests>
  internal static OutboxMessage ToOutboxMessage(
      EventAudited auditEvent, List<MessageHop> causationHops, PerspectiveScope? scope, SystemEventOptions options) {
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
    causationHops.Insert(0, new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      TraceParent = HopStamping.AmbientTraceParent,
      Scope = AuditRecordScope.For(auditEvent.TenantId),
      // The record's own stream, which the receiver stores it on, as it does a record shipped in a bundle. Unnamed,
      // the receiver stored it on this envelope's id (#1798). The outbox row below rides the tenant's ledger lane.
      Metadata = StreamIdentity.HopMetadata(auditEvent.Id),
    });

    var auditEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = auditJson,
      Hops = causationHops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      // An audit record is written on the audit band, whatever number the audited event carried. It
      // never competes with the work it records. The band is SystemEventOptions.AuditPriority,
      // idle by default, and read from there rather than restated so the record, this envelope and
      // the queue row below cannot disagree about it.
      Priority = options.AuditPriority
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
        Hops = auditEnvelope.CopyHops()
      },
      EnvelopeType = Whizbang.Core.Messaging.EnvelopeTypeNameHelper.Format(TypeNameFormatter.AssemblyQualifiedName(auditEventType)),
      Priority = options.AuditPriority,
      // The tenant's audit ledger: one deterministic stream per tenant, so a tenant's records are one
      // orderable ledger and a fold of them inherits it (see AuditLedger).
      StreamId = AuditLedger.StreamFor(auditEvent.TenantId),
      IsEvent = false, // Audit events are NOT stored in event store — only published to transport
      Scope = scope,
      MessageType = TypeNameFormatter.AssemblyQualifiedName(auditEventType)
    };
  }

  private static AuditDecision _decide(
      OutboxMessage eventMessage, Type? eventType, bool isEphemeral, SystemEventOptions options, IAuditDecisionHook? hook,
      ILogger logger) {
    if (eventType is not null) {
      return AuditEligibility.Decide(eventMessage.Envelope.Payload, eventType, options.AuditMode, hook, isEphemeral);
    }
    // An unresolvable type is decided as the ledger rebuild decides it (#1980): not when ephemeral, since it cannot have
    // asked for its body; not under OptIn, since it carries no mark; audited under OptOut, since nothing excludes it.
    if (isEphemeral) {
      return AuditDecision.Skip;
    }
    if (options.AuditMode == AuditMode.OptIn) {
      // Never silent: in the ledger this record's absence looks like a type nobody opted in.
      LogAuditUnresolvedTypeSkipped(logger, eventMessage.MessageType);
      return AuditDecision.Skip;
    }
    return AuditDecision.Record();
  }

  private static Type? _resolveEventType(AuditTypeResolver? eventTypes, string typeName, ILogger logger) {
    if (eventTypes is not null && eventTypes.TryResolve(typeName, out var resolved)) {
      return resolved;
    }
    // Never silent: the decision falls back to the audit mode, which a renamed or removed type should not do unseen.
    LogAuditTypeUnresolved(logger, typeName);
    return null;
  }

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Audit type name '{TypeName}' is not in the message-type catalog; it is audited under OptOut and not audited under OptIn.")]
  private static partial void LogAuditTypeUnresolved(ILogger logger, string typeName);

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Event of type '{TypeName}' was not audited: the type could not be resolved, and under OptIn only a type marked [AuditEvent] is audited.")]
  private static partial void LogAuditUnresolvedTypeSkipped(ILogger logger, string typeName);

  private static string _extractFullTypeName(string messageType) =>
    TypeNameFormatter.GetFullName(messageType);

  /// <summary>
  /// The correlation id from the envelope hops: the first hop's metadata dictionary (older producers) or the hop's
  /// own correlation (the envelope's accessor), whichever is present.
  /// </summary>
  private static string? _correlationIdOf(OutboxMessage eventMessage) {
    if (eventMessage.Metadata.Hops is { Count: > 0 }) {
      var firstHop = eventMessage.Metadata.Hops[0];
      if (firstHop.Metadata != null &&
          firstHop.Metadata.TryGetValue("CorrelationId", out var corrElem) &&
          corrElem.ValueKind == JsonValueKind.String) {
        return corrElem.GetString();
      }
    }
    return eventMessage.Envelope.GetCorrelationId()?.ToString();
  }
}
