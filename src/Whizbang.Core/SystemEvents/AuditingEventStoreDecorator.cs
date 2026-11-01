// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Decorator that emits <see cref="EventAudited"/> audit events to a dedicated outbox topic
/// when domain events are appended. Uses <see cref="IDeferredOutboxChannel"/> to avoid
/// circular DI dependencies (no dependency on <see cref="IEventStore"/> or <see cref="ISystemEventEmitter"/>).
/// </summary>
/// <remarks>
/// <para>
/// This decorator is registered automatically when event auditing is enabled via
/// <see cref="SystemEventOptions.EnableEventAudit"/>. It intercepts <c>AppendAsync</c>
/// calls, builds an <see cref="EventAudited"/> envelope, and queues it to the deferred
/// outbox channel with destination <c>"whizbang.core.auditevents"</c>.
/// </para>
/// <para>
/// The <see cref="SystemEventOptions.AuditMode"/> controls which events are audited:
/// <list type="bullet">
///   <item><see cref="AuditMode.OptOut"/> (default): all events audited unless <c>[AuditEvent(Exclude = true)]</c></item>
///   <item><see cref="AuditMode.OptIn"/>: only events with <c>[AuditEvent]</c> (not excluded) are audited</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#event-auditing</docs>
/// <remarks>
/// Creates a new auditing event store decorator.
/// </remarks>
/// <param name="inner">The inner event store to wrap.</param>
/// <param name="outboxChannel">The deferred outbox channel for queuing audit events.</param>
/// <param name="options">System event configuration options.</param>
/// <param name="ephemeralModeResolver">
/// Recognizes ephemeral types, which are excluded from audit unless they keep their body (#1730); null recognizes the
/// <see cref="IEphemeralEvent"/> marker only.
/// </param>
public sealed class AuditingEventStoreDecorator(
    IEventStore inner,
    IDeferredOutboxChannel outboxChannel,
    IOptions<SystemEventOptions> options,
    Whizbang.Core.Observability.IServiceInstanceProvider instanceProvider,
    IAuditDecisionHook auditDecisionHook,
    ILogger<AuditingEventStoreDecorator> logger,
    IEphemeralModeResolver? ephemeralModeResolver = null) : ForwardingEventStoreDecorator(inner) {
  /// <summary>
  /// The dedicated audit topic destination for outbox messages.
  /// </summary>
#pragma warning disable CA1707
  public const string AUDIT_TOPIC_DESTINATION = "whizbang.core.auditevents";
#pragma warning restore CA1707

  private readonly IDeferredOutboxChannel _outboxChannel = ArgumentGuard.NotNull(outboxChannel);
  private readonly SystemEventOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();
  private readonly ILogger<AuditingEventStoreDecorator> _logger = logger ?? NullLogger<AuditingEventStoreDecorator>.Instance;

  // Optional: a service with no telemetry identity wired must still produce audit records.
  private readonly Whizbang.Core.Observability.IServiceInstanceProvider _instanceProvider = instanceProvider;

  // Optional per-occurrence decision. Without one, behavior is exactly the attribute's.
  private readonly IAuditDecisionHook _auditDecisionHook = auditDecisionHook;

  // Recognizes a type declared ephemeral through a base record or interface (#1730). Without one, only the
  // IEphemeralEvent marker is recognized, as at every other flag-derivation site.
  private readonly IEphemeralModeResolver? _ephemeralModeResolver = ephemeralModeResolver;

  /// <inheritdoc />
  public override async Task AppendAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) {
    // First, append to the inner store
    await Inner.AppendAsync(streamId, envelope, cancellationToken);

    // Emit audit event if eligible
    await _emitAuditIfEligibleAsync(streamId, envelope, cancellationToken);
  }

  /// <inheritdoc />
  public override async Task AppendAsync<TMessage>(
      Guid streamId,
      TMessage message,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);

    // The message goes down as an envelope built here, the one the security decorator would build, so the
    // id the store writes is the id the audit record names. Handing the store the bare message let it mint
    // an id of its own, and the record could point at no stored event (#1485).
    var envelope = Whizbang.Core.Messaging.SecurityContextEventStoreDecorator.CreateAmbientEnvelope(message);
    await Inner.AppendAsync(streamId, envelope, cancellationToken);

    // Emit audit event if eligible
    await _emitAuditIfEligibleAsync(streamId, envelope, cancellationToken);
  }

  private async Task _emitAuditIfEligibleAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken) {
    if (!_options.EventAuditEnabled) {
      return;
    }
    // One place decides: the attribute gates the TYPE, the hook may veto or name the OCCURRENCE.
    var auditDecision = AuditEligibility.Decide(
      envelope.Payload, typeof(TMessage), _options.AuditMode, _auditDecisionHook,
      isEphemeral: EphemeralFlagDeriver.Derive(envelope.Payload, _ephemeralModeResolver) == EventFlags.Ephemeral);
    if (!auditDecision.ShouldAudit) {
      return;
    }
    if (envelope.Payload is null) {
      return;
    }

    var streamPosition = await Inner.GetLastSequenceAsync(streamId, cancellationToken);
    var auditEvent = _buildEventAudited(streamId, streamPosition, envelope, auditDecision);
    var outboxMsg = _buildOutboxMessage(auditEvent);
    await _outboxChannel.QueueAsync(outboxMsg, cancellationToken);
  }

  internal bool _shouldAudit(Type eventType) {
    var attr = eventType
        .GetCustomAttributes(typeof(AuditEventAttribute), inherit: true)
        .FirstOrDefault() as AuditEventAttribute;

    return _options.AuditMode == AuditMode.OptOut
      ? attr?.Exclude != true           // audit unless excluded
      : attr?.Exclude == false;         // audit only if marked
  }

  private EventAudited _buildEventAudited<TMessage>(
      Guid streamId,
      long streamPosition,
      MessageEnvelope<TMessage> envelope,
      AuditDecision decision) {
    // Extract scope from envelope
    var scopeContext = envelope.GetCurrentScope();
    var correlationId = envelope.GetCorrelationId();

    var scope = EventAuditScope.Build(scopeContext, correlationId);

    // Extract audit attribute metadata
    var attr = AuditEligibility.AttributeOf(typeof(TMessage));

    // The body only when kept (#1588); otherwise the record points at the event and is not serialized at all
    JsonElement? payloadJson = AuditEligibility.IncludesBody(typeof(TMessage), _options.AuditBodyDefault)
      ? AuditJsonSerializer.SerializeToJsonElement(envelope.Payload, _jsonOptions, _logger)
      : null;

    return new EventAudited {
      // Derived from the event it audits, so a record derived again is the same record (#1724).
      Id = AuditLedger.RecordIdFor(envelope.MessageId.Value),
      OriginalEventId = envelope.MessageId.Value,
      OriginalEventType = typeof(TMessage).Name,
      OriginalStreamId = streamId.ToString(),
      OriginalStreamPosition = streamPosition,
      OriginalBody = payloadJson,
      Timestamp = DateTimeOffset.UtcNow,
      // Supplied per occurrence by the decision hook; null falls back to humanizing the type name.
      ActivityName = decision.Name,
      ActivityDescription = decision.Description,
      TenantId = scopeContext?.Scope.TenantId,
      UserId = scopeContext?.Scope.UserId,
      CorrelationId = correlationId?.ToString(),
      CausationId = envelope.GetCausationId()?.ToString(),
      AuditReason = attr?.Reason,
      AuditLevel = attr?.Level ?? AuditLevel.Info,
      Scope = scope
    };
  }

  /// <summary>The audit record's outbox row; its hop names the record's own stream (#1798).</summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:StoredStream_IsTheStreamTheProducerStoredItOnAsync</tests>
  private OutboxMessage _buildOutboxMessage(EventAudited auditEvent) {
    // Create envelope for the audit event
    var envelope = new MessageEnvelope<EventAudited> {
      // The audit band, idle by default; see SystemEventOptions.AuditPriority. All three stampings
      // in this method read the one value, so the record cannot travel at one priority and land at
      // another.
      Priority = _options.AuditPriority,
      MessageId = MessageId.New(),
      Payload = auditEvent,
      Hops = [
        new MessageHop {
          ServiceInstance = _instanceProvider.ToInfo(),
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = HopStamping.AmbientTraceParent,
          // The audited TENANT plus the system marker. Not the acting user: scope is an
          // access-control key, so writing the actor here would hand the SUBJECT of an audit
          // record a key to their own audit trail.
          Scope = AuditRecordScope.For(auditEvent.TenantId),
          // The record's own stream, which the receiver stores it on, as on the outbox path (#1798).
          Metadata = StreamIdentity.HopMetadata(auditEvent.Id),
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };

    // Serialize the envelope to JsonElement form for the outbox
    var serializedPayload = AuditJsonSerializer.SerializeToJsonElement(auditEvent, _jsonOptions, _logger);
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      Priority = _options.AuditPriority,
      MessageId = envelope.MessageId,
      Payload = serializedPayload,
      Hops = envelope.Hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };

    var eventType = typeof(EventAudited);
    return new OutboxMessage {
      Priority = _options.AuditPriority,
      MessageId = envelope.MessageId.Value,
      Destination = AUDIT_TOPIC_DESTINATION,
      Envelope = jsonEnvelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.CopyHops()
      },
      EnvelopeType = Whizbang.Core.Messaging.EnvelopeTypeNameHelper.Format(TypeNameFormatter.AssemblyQualifiedName(eventType)),
      // The tenant's audit ledger, as on the outbox path. Transport-only, as on the outbox path: an audit
      // record is never event-stored at its origin, so it neither takes the ledger's per-stream store lock
      // nor lands in the origin's digest lane (#1485).
      StreamId = AuditLedger.StreamFor(auditEvent.TenantId),
      IsEvent = false,
      MessageType = TypeNameFormatter.AssemblyQualifiedName(eventType)
    };
  }

}
