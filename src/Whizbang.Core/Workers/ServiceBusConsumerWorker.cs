using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background service that subscribes to messages from Azure Service Bus and invokes local perspectives.
/// IMPORTANT: Does NOT re-publish events to avoid infinite loops.
/// Events from remote services are stored in inbox for deduplication and perspectives are invoked directly.
/// </summary>
public class ServiceBusConsumerWorker(
  IServiceInstanceProvider instanceProvider,
  ITransport transport,
  IServiceScopeFactory scopeFactory,
  JsonSerializerOptions jsonOptions,
  ILogger<ServiceBusConsumerWorker> logger,
  ServiceBusConsumerOptions? options = null
  ) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<ServiceBusConsumerWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly List<ISubscription> _subscriptions = [];
  private readonly ServiceBusConsumerOptions _options = options ?? new ServiceBusConsumerOptions();

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    using var activity = WhizbangActivitySource.Hosting.StartActivity("ServiceBusConsumerWorker.Start");
    activity?.SetTag("worker.subscriptions_count", _options.Subscriptions.Count);
    activity?.SetTag("servicebus.has_filter", _options.Subscriptions.Any(s => !string.IsNullOrWhiteSpace(s.DestinationFilter)));

    _logger.LogInformation("ServiceBusConsumerWorker starting...");

    try {
      // Subscribe to configured topics
      foreach (var topicConfig in _options.Subscriptions) {
        // Create destination with DestinationFilter metadata if specified
        var metadata = !string.IsNullOrWhiteSpace(topicConfig.DestinationFilter)
          ? new Dictionary<string, object> { ["DestinationFilter"] = topicConfig.DestinationFilter }
          : null;

        var destination = new TransportDestination(
          topicConfig.TopicName,
          topicConfig.SubscriptionName,
          metadata
        );

        var subscription = await _transport.SubscribeAsync(
          async (envelope, ct) => await HandleMessageAsync(envelope, ct),
          destination,
          stoppingToken
        );

        _subscriptions.Add(subscription);

        _logger.LogInformation(
          "Subscribed to topic {TopicName} with subscription {SubscriptionName}",
          topicConfig.TopicName,
          topicConfig.SubscriptionName
        );
      }

      // Keep the worker running while subscriptions are active
      await Task.Delay(Timeout.Infinite, stoppingToken);
    } catch (OperationCanceledException) {
      _logger.LogInformation("ServiceBusConsumerWorker is stopping...");
    } catch (Exception ex) {
      _logger.LogError(ex, "Fatal error in ServiceBusConsumerWorker");
      throw;
    }
  }

  private async Task HandleMessageAsync(IMessageEnvelope envelope, CancellationToken ct) {
    try {
      // Create scope early to resolve scoped services (IInbox, IPerspectiveInvoker)
      await using var scope = _scopeFactory.CreateAsyncScope();
      var inbox = scope.ServiceProvider.GetRequiredService<IInbox>();

      // Check inbox for deduplication
      if (await inbox.HasProcessedAsync(envelope.MessageId, ct)) {
        _logger.LogInformation(
          "Message {MessageId} already processed, skipping",
          envelope.MessageId
        );
        return;
      }

      _logger.LogInformation(
        "Processing message {MessageId} from Service Bus",
        envelope.MessageId
      );

      // Deserialize the JsonElement payload to the actual event type
      // The PayloadType is stored in the last hop's metadata by OutboxPublisherWorker
      // IMPORTANT: Do this BEFORE adding receivedHop so lastHop refers to OutboxPublisherWorker's hop
      var payload = envelope.GetPayload();
      object? deserializedEvent = null;

      // DEBUG: Log hop information
      _logger.LogInformation("DEBUG: Envelope has {HopCount} hops", envelope.Hops.Count);

      // Try to find PayloadType in the most recent hop metadata
      var lastHop = envelope.Hops.LastOrDefault();
      _logger.LogInformation("DEBUG: Last hop exists: {Exists}, Has metadata: {HasMetadata}",
        lastHop != null,
        lastHop?.Metadata != null);

      if (lastHop?.Metadata != null && lastHop.Metadata.TryGetValue("PayloadType", out var payloadTypeObj)) {
        _logger.LogInformation("DEBUG: PayloadType found in metadata");

        var payloadTypeElem = (System.Text.Json.JsonElement)payloadTypeObj;
        var payloadTypeName = payloadTypeElem.GetString();
        _logger.LogInformation("DEBUG: PayloadType name: '{PayloadTypeName}'", payloadTypeName);

        if (!string.IsNullOrEmpty(payloadTypeName)) {
          // Get the Type from the fully-qualified name
          var payloadType = Type.GetType(payloadTypeName);
          _logger.LogInformation("DEBUG: Type.GetType() returned: {TypeFound} (Type: {TypeName})",
            payloadType != null,
            payloadType?.FullName ?? "null");

          if (payloadType != null) {
            // Get the JsonTypeInfo for this type from the JSON context
            var typeInfo = _jsonOptions.GetTypeInfo(payloadType);
            _logger.LogInformation("DEBUG: GetTypeInfo() returned: {TypeInfoFound}", typeInfo != null);

            if (typeInfo != null && payload is System.Text.Json.JsonElement jsonElem) {
              _logger.LogInformation("DEBUG: Payload is JsonElement, attempting deserialization...");
              // Re-serialize the JsonElement and deserialize to the correct type
              var json = jsonElem.GetRawText();
              deserializedEvent = JsonSerializer.Deserialize(json, typeInfo);
              _logger.LogInformation("DEBUG: Deserialization result: {Success} (Type: {TypeName})",
                deserializedEvent != null,
                deserializedEvent?.GetType().Name ?? "null");
              _logger.LogInformation("DEBUG: AFTER DESERIALIZATION - Event is IEvent: {IsIEvent}",
                deserializedEvent is IEvent);
            } else {
              _logger.LogWarning("DEBUG: Deserialization skipped - TypeInfo: {HasTypeInfo}, IsJsonElement: {IsJsonElement}",
                typeInfo != null,
                payload is System.Text.Json.JsonElement);
            }
          }
        }
      } else {
        _logger.LogWarning("DEBUG: PayloadType NOT found in last hop metadata");
        if (lastHop?.Metadata != null) {
          _logger.LogWarning("DEBUG: Last hop metadata keys: {Keys}",
            string.Join(", ", lastHop.Metadata.Keys));
        }
      }

      // Fallback: If deserialization didn't produce an event, use payload if it's already an IEvent
      // This happens in test scenarios or when the envelope already contains a typed event
      if (deserializedEvent == null && payload is IEvent typedPayload) {
        deserializedEvent = typedPayload;
        _logger.LogInformation(
          "DEBUG: Using typed payload directly as event: {EventType}",
          typedPayload.GetType().Name
        );
      }

      _logger.LogInformation("DEBUG: BEFORE ADDING HOP");

      // Add hop indicating message was received from Service Bus
      // This preserves the distributed trace from the sending service
      // IMPORTANT: Add AFTER deserialization so PayloadType lookup works correctly
      var receivedHop = new MessageHop {
        Type = HopType.Current,
        ServiceName = _instanceProvider.ServiceName,
        ServiceInstanceId = _instanceProvider.InstanceId,
        Topic = _options.Subscriptions.FirstOrDefault()?.TopicName ?? "unknown-topic",
        Timestamp = DateTimeOffset.UtcNow
      };
      envelope.AddHop(receivedHop);

      _logger.LogInformation("DEBUG: BEFORE RESOLVING INVOKER");

      // Resolve perspective invoker from the scope (already created at method start)
      var perspectiveInvoker = scope.ServiceProvider.GetService<Perspectives.IPerspectiveInvoker>();

      _logger.LogInformation("DEBUG: INVOKER RESOLVED: {InvokerType}",
        perspectiveInvoker?.GetType().FullName ?? "NULL");

      _logger.LogInformation("DEBUG: ABOUT TO LOG INVOKER TYPE");

      // DEBUG: Log which invoker type is being resolved
      _logger.LogInformation(
        "DEBUG: PerspectiveInvoker type: {InvokerType}, Assembly: {Assembly}",
        perspectiveInvoker?.GetType().FullName ?? "null",
        perspectiveInvoker?.GetType().Assembly.GetName().Name ?? "null"
      );

      // Log deserialization result
      _logger.LogInformation(
        "Deserialized event: {EventType}, PerspectiveInvoker: {HasInvoker}",
        deserializedEvent?.GetType().Name ?? "null",
        perspectiveInvoker != null
      );

      // Queue event to perspective invoker if available and if deserialized event is IEvent
      if (perspectiveInvoker != null && deserializedEvent is IEvent @event) {
        perspectiveInvoker.QueueEvent(@event);
        _logger.LogInformation(
          "Queued event {EventType} to perspective invoker",
          @event.GetType().Name
        );

        // IMPORTANT: Invoke perspectives BEFORE disposing scope
        // If we wait for scope disposal, the service provider will already be disposed
        // when the invoker tries to resolve perspectives via GetServices()
        await perspectiveInvoker.InvokePerspectivesAsync(ct);
        _logger.LogInformation(
          "Invoked perspectives for {EventType}",
          @event.GetType().Name
        );
      } else {
        _logger.LogWarning(
          "Failed to queue event - Deserialized: {Deserialized}, IsIEvent: {IsIEvent}, HasInvoker: {HasInvoker}",
          deserializedEvent != null,
          deserializedEvent is IEvent,
          perspectiveInvoker != null
        );
      }

      // Mark as processed in inbox (for deduplication)
      await inbox.MarkProcessedAsync(envelope.MessageId, ct);

      _logger.LogInformation(
        "Successfully processed message {MessageId}",
        envelope.MessageId
      );

      // Scope will be disposed automatically by 'await using' at end of method
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Error processing message {MessageId}",
        envelope.MessageId
      );
      throw; // Let the transport handle retry/dead-letter
    }
  }

  public override async Task StopAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("ServiceBusConsumerWorker stopping...");

    // Dispose all subscriptions
    foreach (var subscription in _subscriptions) {
      subscription.Dispose();
    }

    await base.StopAsync(cancellationToken);
  }
}

/// <summary>
/// Configuration options for ServiceBusConsumerWorker.
/// </summary>
public class ServiceBusConsumerOptions {
  /// <summary>
  /// List of topic subscriptions to consume messages from.
  /// </summary>
  public List<TopicSubscription> Subscriptions { get; set; } = [];
}

/// <summary>
/// Configuration for a single topic subscription.
/// </summary>
/// <param name="TopicName">The Service Bus topic name</param>
/// <param name="SubscriptionName">The subscription name for this consumer</param>
/// <param name="DestinationFilter">Optional destination filter value (e.g., "inventory-service")</param>
public record TopicSubscription(string TopicName, string SubscriptionName, string? DestinationFilter = null);
