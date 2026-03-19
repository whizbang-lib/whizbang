using System.Text.Json;
using Microsoft.Extensions.Options;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Default implementation of <see cref="ISystemEventEmitter"/> that emits system events
/// to the dedicated <c>$wb-system</c> stream.
/// </summary>
/// <remarks>
/// <para>
/// This emitter respects <see cref="SystemEventOptions"/> configuration and only emits
/// system events when the corresponding feature is enabled.
/// </para>
/// <para>
/// Events with <c>[AuditEvent(Exclude = true)]</c> are not re-audited to prevent
/// infinite loops.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#emitter</docs>
public sealed class SystemEventEmitter : ISystemEventEmitter {
  private readonly SystemEventOptions _options;
  private readonly IEventStore _systemEventStore;
  private readonly JsonSerializerOptions _jsonOptions;

  /// <summary>
  /// Creates a new system event emitter.
  /// </summary>
  /// <param name="options">System event configuration options.</param>
  /// <param name="systemEventStore">Event store for persisting system events.</param>
  public SystemEventEmitter(
      IOptions<SystemEventOptions> options,
      IEventStore systemEventStore) {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _systemEventStore = systemEventStore ?? throw new ArgumentNullException(nameof(systemEventStore));
    _jsonOptions = JsonContextRegistry.CreateCombinedOptions();
  }

  /// <inheritdoc />
  public async Task EmitEventAuditedAsync<TEvent>(
      Guid streamId,
      long streamPosition,
      MessageEnvelope<TEvent> envelope,
      CancellationToken cancellationToken = default) {
    // Check if event audit is enabled
    if (!_options.EventAuditEnabled) {
      return;
    }

    // Skip if payload is null
    if (envelope.Payload is null) {
      return;
    }

    // Check if this event type should be excluded from audit
    if (ShouldExcludeFromAudit(typeof(TEvent))) {
      return;
    }

    // Extract scope from envelope - generic dictionary for flexibility
    var scopeContext = envelope.GetCurrentScope();
    var correlationId = envelope.GetCorrelationId();

    // Build scope dictionary from scope context
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

    // Add all claims to scope dictionary (includes name, email, etc.)
    if (scopeContext?.Claims is not null) {
      foreach (var claim in scopeContext.Claims) {
        scope[claim.Key] = claim.Value;
      }
    }

    // Serialize payload to JsonElement in AOT-compatible way
    var payloadJson = AuditJsonSerializer.SerializeToJsonElement(envelope.Payload, _jsonOptions);

    // Create the audit event with generic scope
    var auditEvent = new EventAudited {
      Id = TrackedGuid.NewMedo(),
      OriginalEventType = typeof(TEvent).Name,
      OriginalStreamId = streamId.ToString(),
      OriginalStreamPosition = streamPosition,
      OriginalBody = payloadJson,
      Timestamp = DateTimeOffset.UtcNow,
      // Store individual properties for backward compatibility
      TenantId = scopeContext?.Scope?.TenantId,
      UserId = scopeContext?.Scope?.UserId,
      CorrelationId = correlationId?.ToString(),
      // Store full scope for generic access
      Scope = scope.Count > 0 ? scope : null
    };

    // Emit to the system stream
    await EmitAsync(auditEvent, cancellationToken);
  }

  /// <inheritdoc />
  public async Task EmitCommandAuditedAsync<TCommand, TResponse>(
      TCommand command,
      TResponse response,
      string receptorName,
      IMessageContext? context,
      CancellationToken cancellationToken = default) where TCommand : notnull {
    // Check if command audit is enabled
    if (!_options.CommandAuditEnabled) {
      return;
    }

    // Check if this command type should be excluded from audit
    if (ShouldExcludeFromAudit(typeof(TCommand))) {
      return;
    }

    // Build scope dictionary from context metadata
    var scope = new Dictionary<string, string?>();
    if (context?.Metadata.TryGetValue("TenantId", out var tenantId) == true) {
      scope["TenantId"] = tenantId?.ToString();
    }
    if (context?.UserId != null) {
      scope["UserId"] = context.UserId;
    }
    if (context?.CorrelationId != null) {
      scope["CorrelationId"] = context.CorrelationId.ToString();
    }

    // Serialize command to JsonElement in AOT-compatible way
    var commandJson = AuditJsonSerializer.SerializeToJsonElement(command, _jsonOptions);

    // Create the audit event with generic scope
    var auditEvent = new CommandAudited {
      Id = TrackedGuid.NewMedo(),
      CommandType = typeof(TCommand).Name,
      CommandBody = commandJson,
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = receptorName,
      ResponseType = typeof(TResponse).Name,
      // Store individual properties for backward compatibility
      TenantId = scope.TryGetValue("TenantId", out var t) ? t : null,
      UserId = context?.UserId,
      CorrelationId = context?.CorrelationId.ToString(),
      // Store full scope for generic access
      Scope = scope.Count > 0 ? scope : null
    };

    // Emit to the system stream
    await EmitAsync(auditEvent, cancellationToken);
  }

  /// <inheritdoc />
  public async Task EmitAsync<TSystemEvent>(
      TSystemEvent systemEvent,
      CancellationToken cancellationToken = default) where TSystemEvent : ISystemEvent {
    // Check if this specific system event type is enabled
    // Always allow audit events if any audit is enabled
    if (!_options.IsEnabled<TSystemEvent>() &&
        (!_options.AuditEnabled ||
         (typeof(TSystemEvent) != typeof(EventAudited) && typeof(TSystemEvent) != typeof(CommandAudited)))) {
      return;
    }

    // Create envelope for the system event
    var envelope = new MessageEnvelope<TSystemEvent> {
      MessageId = MessageId.New(),
      Payload = systemEvent,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = System.Diagnostics.Activity.Current?.Id
        }
      ]
    };

    // Append to the system stream
    await _systemEventStore.AppendAsync(SystemEventStreams.StreamId, envelope, cancellationToken);
  }

  /// <inheritdoc />
  public bool ShouldExcludeFromAudit(Type type) {
    // Check for [AuditEvent(Exclude = true)] attribute
    var attribute = type
        .GetCustomAttributes(typeof(AuditEventAttribute), inherit: true)
        .FirstOrDefault() as AuditEventAttribute;

    return attribute?.Exclude == true;
  }

}
