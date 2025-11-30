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
  ITransport transport,
  IServiceScopeFactory scopeFactory,
  IInbox inbox,
  JsonSerializerOptions jsonOptions,
  ILogger<ServiceBusConsumerWorker> logger,
  ServiceBusConsumerOptions? options = null
  ) : BackgroundService {
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IInbox _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<ServiceBusConsumerWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly List<ISubscription> _subscriptions = [];
  private readonly ServiceBusConsumerOptions _options = options ?? new ServiceBusConsumerOptions();

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation("ServiceBusConsumerWorker starting...");

    try {
      // Subscribe to configured topics
      foreach (var topicConfig in _options.Subscriptions) {
        var destination = new TransportDestination(
          topicConfig.TopicName,
          topicConfig.SubscriptionName
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
      // Check inbox for deduplication
      if (await _inbox.HasProcessedAsync(envelope.MessageId, ct)) {
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

      // Add hop indicating message was received from Service Bus
      // This preserves the distributed trace from the sending service
      var receivedHop = new MessageHop {
        Type = HopType.Current,
        ServiceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown",
        Topic = _options.Subscriptions.FirstOrDefault()?.TopicName ?? "unknown-topic",
        Timestamp = DateTimeOffset.UtcNow
      };
      envelope.AddHop(receivedHop);

      // Deserialize the JsonElement payload to the actual event type
      // The PayloadType is stored in the last hop's metadata by OutboxPublisherWorker
      var payload = envelope.GetPayload();
      object? deserializedEvent = null;

      // Try to find PayloadType in the most recent hop metadata
      var lastHop = envelope.Hops.LastOrDefault();
      if (lastHop?.Metadata != null && lastHop.Metadata.TryGetValue("PayloadType", out var payloadTypeObj)) {
        var payloadTypeElem = (System.Text.Json.JsonElement)payloadTypeObj;
        var payloadTypeName = payloadTypeElem.GetString();
        if (!string.IsNullOrEmpty(payloadTypeName)) {
          // Get the Type from the fully-qualified name
          var payloadType = Type.GetType(payloadTypeName);
          if (payloadType != null) {
            // Get the JsonTypeInfo for this type from the JSON context
            var typeInfo = _jsonOptions.GetTypeInfo(payloadType);
            if (typeInfo != null && payload is System.Text.Json.JsonElement jsonElem) {
              // Re-serialize the JsonElement and deserialize to the correct type
              var json = jsonElem.GetRawText();
              deserializedEvent = JsonSerializer.Deserialize(json, typeInfo);
            }
          }
        }
      }

      // Invoke perspectives in a scope (mimics Event Store behavior)
      // Create scope to resolve scoped services (IEventStore, IPerspectiveInvoker)
      await using var scope = _scopeFactory.CreateAsyncScope();
      var perspectiveInvoker = scope.ServiceProvider.GetService<Perspectives.IPerspectiveInvoker>();

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
      } else {
        _logger.LogWarning(
          "Failed to queue event - Deserialized: {Deserialized}, IsIEvent: {IsIEvent}, HasInvoker: {HasInvoker}",
          deserializedEvent != null,
          deserializedEvent is IEvent,
          perspectiveInvoker != null
        );
      }

      // Dispose scope to trigger perspective invocation
      // (IPerspectiveInvoker.DisposeAsync invokes all queued perspectives)
      await scope.DisposeAsync();

      // Mark as processed in inbox (for deduplication)
      await _inbox.MarkProcessedAsync(envelope.MessageId, ct);

      _logger.LogInformation(
        "Successfully processed message {MessageId}",
        envelope.MessageId
      );
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
public record TopicSubscription(string TopicName, string SubscriptionName);
