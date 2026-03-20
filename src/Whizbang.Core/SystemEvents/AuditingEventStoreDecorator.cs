using System.Text.Json;
using Microsoft.Extensions.Options;
using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
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
public sealed class AuditingEventStoreDecorator(
    IEventStore inner,
    IDeferredOutboxChannel outboxChannel,
    IOptions<SystemEventOptions> options) : IEventStore {
  /// <summary>
  /// The dedicated audit topic destination for outbox messages.
  /// </summary>
#pragma warning disable CA1707
  public const string AUDIT_TOPIC_DESTINATION = "whizbang.core.auditevents";
#pragma warning restore CA1707

  private readonly IEventStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly IDeferredOutboxChannel _outboxChannel = outboxChannel ?? throw new ArgumentNullException(nameof(outboxChannel));
  private readonly SystemEventOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();

  /// <inheritdoc />
  public async Task AppendAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) {
    // First, append to the inner store
    await _inner.AppendAsync(streamId, envelope, cancellationToken);

    // Emit audit event if eligible
    await _emitAuditIfEligibleAsync(streamId, envelope, cancellationToken);
  }

  /// <inheritdoc />
  public async Task AppendAsync<TMessage>(
      Guid streamId,
      TMessage message,
      CancellationToken cancellationToken = default)
      where TMessage : notnull {
    // Delegate to inner store for persistence
    await _inner.AppendAsync(streamId, message, cancellationToken);

    // Create a minimal envelope for audit purposes
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = System.Diagnostics.Activity.Current?.Id
        }
      ]
    };

    // Emit audit event if eligible
    await _emitAuditIfEligibleAsync(streamId, envelope, cancellationToken);
  }

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId,
      long fromSequence,
      CancellationToken cancellationToken = default) =>
      _inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId,
      Guid? fromEventId,
      CancellationToken cancellationToken = default) =>
      _inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);

  /// <inheritdoc />
  public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId,
      Guid? fromEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
      _inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken);

  /// <inheritdoc />
  public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      CancellationToken cancellationToken = default) =>
      _inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);

  /// <inheritdoc />
  public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
      _inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);

  /// <inheritdoc />
  public Task<long> GetLastSequenceAsync(
      Guid streamId,
      CancellationToken cancellationToken = default) =>
      _inner.GetLastSequenceAsync(streamId, cancellationToken);

  private async Task _emitAuditIfEligibleAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken) {
    if (!_options.EventAuditEnabled) {
      return;
    }
    if (!_shouldAudit(typeof(TMessage))) {
      return;
    }
    if (envelope.Payload is null) {
      return;
    }

    var streamPosition = await _inner.GetLastSequenceAsync(streamId, cancellationToken);
    var auditEvent = _buildEventAudited(streamId, streamPosition, envelope);
    var outboxMsg = _buildOutboxMessage(auditEvent);
    await _outboxChannel.QueueAsync(outboxMsg, cancellationToken);
  }

  internal bool _shouldAudit(Type eventType) {
    var attr = eventType
        .GetCustomAttributes(typeof(AuditEventAttribute), inherit: true)
        .FirstOrDefault() as AuditEventAttribute;

    return _options.AuditMode == AuditMode.OptOut
      ? attr?.Exclude != true         // audit unless excluded
      : attr != null && !attr.Exclude; // audit only if marked
  }

  private EventAudited _buildEventAudited<TMessage>(
      Guid streamId,
      long streamPosition,
      MessageEnvelope<TMessage> envelope) {
    // Extract scope from envelope
    var scopeContext = envelope.GetCurrentScope();
    var correlationId = envelope.GetCorrelationId();

    // Build scope dictionary
    var scope = new Dictionary<string, string?>();
    if (scopeContext?.Scope?.TenantId != null) {
      scope["TenantId"] = scopeContext.Scope.TenantId;
    }
    if (scopeContext?.Scope?.UserId != null) {
      scope["UserId"] = scopeContext.Scope.UserId;
    }
    if (correlationId != null) {
      scope["CorrelationId"] = correlationId.ToString();
    }
    if (scopeContext?.Claims is not null) {
      foreach (var claim in scopeContext.Claims) {
        scope[claim.Key] = claim.Value;
      }
    }

    // Extract audit attribute metadata
    var attr = typeof(TMessage)
        .GetCustomAttributes(typeof(AuditEventAttribute), inherit: true)
        .FirstOrDefault() as AuditEventAttribute;

    // Serialize payload to JsonElement (AOT-compatible)
    var payloadJson = AuditJsonSerializer.SerializeToJsonElement(envelope.Payload, _jsonOptions);

    return new EventAudited {
      Id = TrackedGuid.NewMedo(),
      OriginalEventType = typeof(TMessage).Name,
      OriginalStreamId = streamId.ToString(),
      OriginalStreamPosition = streamPosition,
      OriginalBody = payloadJson,
      Timestamp = DateTimeOffset.UtcNow,
      TenantId = scopeContext?.Scope?.TenantId,
      UserId = scopeContext?.Scope?.UserId,
      CorrelationId = correlationId?.ToString(),
      AuditReason = attr?.Reason,
      AuditLevel = attr?.Level ?? AuditLevel.Info,
      Scope = scope.Count > 0 ? scope : null
    };
  }

  private OutboxMessage _buildOutboxMessage(EventAudited auditEvent) {
    // Create envelope for the audit event
    var envelope = new MessageEnvelope<EventAudited> {
      MessageId = MessageId.New(),
      Payload = auditEvent,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = System.Diagnostics.Activity.Current?.Id
        }
      ]
    };

    // Serialize the envelope to JsonElement form for the outbox
    var serializedPayload = AuditJsonSerializer.SerializeToJsonElement(auditEvent, _jsonOptions);
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = envelope.MessageId,
      Payload = serializedPayload,
      Hops = envelope.Hops
    };

    var eventType = typeof(EventAudited);
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = AUDIT_TOPIC_DESTINATION,
      Envelope = jsonEnvelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{eventType.AssemblyQualifiedName}]], Whizbang.Core",
      StreamId = auditEvent.Id,
      IsEvent = true,
      MessageType = eventType.AssemblyQualifiedName ?? eventType.FullName ?? eventType.Name
    };
  }

}
