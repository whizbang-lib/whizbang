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
/// Uses work coordinator pattern for atomic deduplication and stream-based ordering.
/// Events from remote services are stored in inbox via process_work_batch and perspectives are invoked with ordering guarantees.
/// </summary>
public class ServiceBusConsumerWorker(
  IServiceInstanceProvider instanceProvider,
  ITransport transport,
  IServiceScopeFactory scopeFactory,
  JsonSerializerOptions jsonOptions,
  ILogger<ServiceBusConsumerWorker> logger,
  OrderedStreamProcessor orderedProcessor,
  ServiceBusConsumerOptions? options = null
  ) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<ServiceBusConsumerWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly OrderedStreamProcessor _orderedProcessor = orderedProcessor ?? throw new ArgumentNullException(nameof(orderedProcessor));
  private readonly List<ISubscription> _subscriptions = [];
  private readonly ServiceBusConsumerOptions _options = options ?? new ServiceBusConsumerOptions();

  /// <summary>
  /// Starts the worker and creates all subscriptions BEFORE background processing begins.
  /// This ensures subscriptions are ready before ExecuteAsync runs (blocking initialization).
  /// </summary>
  public override async Task StartAsync(CancellationToken cancellationToken) {
    using var activity = WhizbangActivitySource.Hosting.StartActivity("ServiceBusConsumerWorker.Start");
    activity?.SetTag("worker.subscriptions_count", _options.Subscriptions.Count);
    activity?.SetTag("servicebus.has_filter", _options.Subscriptions.Any(s => !string.IsNullOrWhiteSpace(s.DestinationFilter)));

    _logger.LogInformation("ServiceBusConsumerWorker starting - creating subscriptions...");

    try {
      // Subscribe to configured topics (BLOCKING - ensures subscriptions ready before ExecuteAsync)
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
          cancellationToken
        );

        _subscriptions.Add(subscription);

        _logger.LogInformation(
          "Subscribed to topic {TopicName} with subscription {SubscriptionName}",
          topicConfig.TopicName,
          topicConfig.SubscriptionName
        );
      }

      _logger.LogInformation("ServiceBusConsumerWorker subscriptions ready ({Count} subscriptions)", _subscriptions.Count);

      // Call base.StartAsync to trigger ExecuteAsync
      await base.StartAsync(cancellationToken);
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to start ServiceBusConsumerWorker - subscriptions not ready");
      throw;
    }
  }

  /// <summary>
  /// Background processing loop - keeps worker alive while subscriptions process messages.
  /// Subscriptions are already created in StartAsync (blocking), so this just waits.
  /// </summary>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation("ServiceBusConsumerWorker background processing started");

    try {
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
      // Create scope to resolve scoped services (IWorkCoordinatorStrategy, IPerspectiveInvoker)
      await using var scope = _scopeFactory.CreateAsyncScope();
      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

      _logger.LogInformation(
        "Processing message {MessageId} from Service Bus",
        envelope.MessageId
      );

      // 1. Serialize envelope to NewInboxMessage
      var newInboxMessage = _serializeToNewInboxMessage(envelope);

      // 2. Queue for atomic deduplication via process_work_batch
      strategy.QueueInboxMessage(newInboxMessage);

      // 3. Flush - calls process_work_batch with atomic INSERT ... ON CONFLICT DO NOTHING
      var workBatch = await strategy.FlushAsync(WorkBatchFlags.None, ct);

      // 4. Check if work was returned - empty means duplicate (already processed)
      var myWork = workBatch.InboxWork.Where(w => w.MessageId == envelope.MessageId.Value).ToList();

      if (myWork.Count == 0) {
        _logger.LogInformation(
          "Message {MessageId} already processed (duplicate), skipping",
          envelope.MessageId
        );
        return;
      }

      _logger.LogInformation(
        "Message {MessageId} accepted for processing ({WorkCount} inbox work items)",
        envelope.MessageId,
        myWork.Count
      );

      // 5. Process using OrderedStreamProcessor (maintains stream ordering)
      // Resolve perspective invoker once for all work items
      var perspectiveInvoker = scope.ServiceProvider.GetService<Perspectives.IPerspectiveInvoker>();

      await _orderedProcessor.ProcessInboxWorkAsync(
        myWork,
        processor: async (work) => {
          // Deserialize event from work item
          var @event = _deserializeEvent(work);

          // Queue event to perspective invoker if available
          if (perspectiveInvoker != null && @event is IEvent typedEvent) {
            perspectiveInvoker.QueueEvent(typedEvent);

            // Invoke perspectives for this event
            await perspectiveInvoker.InvokePerspectivesAsync(ct);

            _logger.LogInformation(
              "Invoked perspectives for {EventType} (message {MessageId})",
              typedEvent.GetType().Name,
              work.MessageId
            );

            return MessageProcessingStatus.ReceptorProcessed | MessageProcessingStatus.PerspectiveProcessed;
          } else {
            _logger.LogWarning(
              "Failed to invoke perspectives - Event: {EventType}, HasInvoker: {HasInvoker}",
              @event?.GetType().Name ?? "null",
              perspectiveInvoker != null
            );

            // Still mark as processed even if perspective invocation failed
            return MessageProcessingStatus.ReceptorProcessed;
          }
        },
        completionHandler: (msgId, status) => {
          strategy.QueueInboxCompletion(msgId, status);
          _logger.LogDebug("Queued completion for {MessageId} with status {Status}", msgId, status);
        },
        failureHandler: (msgId, status, error) => {
          strategy.QueueInboxFailure(msgId, status, error);
          _logger.LogError("Queued failure for {MessageId}: {Error}", msgId, error);
        },
        ct
      );

      // 6. Report completions/failures back to database
      await strategy.FlushAsync(WorkBatchFlags.None, ct);

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

  /// <summary>
  /// Creates NewInboxMessage for work coordinator pattern.
  /// Envelope remains as object - serialization happens at work coordinator layer.
  /// </summary>
  private NewInboxMessage _serializeToNewInboxMessage(IMessageEnvelope envelope) {
    // Get payload and its type for metadata
    var payload = envelope.GetPayload();
    var payloadType = payload.GetType();

    // Determine handler name from payload type
    var handlerName = payloadType.Name + "Handler";

    // Extract stream_id from envelope (aggregate ID or message ID)
    var streamId = _extractStreamId(envelope);

    return new NewInboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = envelope,  // Pass object, not serialized string
      StreamId = streamId,
      IsEvent = payload is IEvent
    };
  }

  /// <summary>
  /// Extracts event payload from InboxWork for processing.
  /// Envelope is already deserialized - just get the payload.
  /// </summary>
  private object? _deserializeEvent(InboxWork work) {
    try {
      // Extract and return the payload from the envelope object
      return work.Envelope.GetPayload();
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to extract payload from envelope for message {MessageId}", work.MessageId);
      return null;
    }
  }

  /// <summary>
  /// Serializes envelope metadata (MessageId + Hops) to JSON string.
  /// </summary>
  private string _serializeEnvelopeMetadata(IMessageEnvelope envelope) {
    var metadata = new EnvelopeMetadata {
      MessageId = envelope.MessageId,
      Hops = envelope.Hops.ToList()
    };

    var metadataTypeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<EnvelopeMetadata>)_jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata));
    return JsonSerializer.Serialize(metadata, metadataTypeInfo);
  }

  /// <summary>
  /// Serializes security scope (tenant, user) from first hop's security context.
  /// Returns null if no security context is present.
  /// </summary>
  private static string? _serializeSecurityScope(IMessageEnvelope envelope) {
    // Extract security context from first hop if available
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.SecurityContext == null) {
      return null;
    }

    // Manual JSON construction for AOT compatibility
    var userId = firstHop.SecurityContext.UserId?.ToString();
    var tenantId = firstHop.SecurityContext.TenantId?.ToString();

    return $"{{\"UserId\":{(userId == null ? "null" : $"\"{userId}\"")},\"TenantId\":{(tenantId == null ? "null" : $"\"{tenantId}\"")}}}";
  }

  /// <summary>
  /// Extracts stream_id from envelope for stream-based ordering.
  /// Tries to get aggregate ID from first hop metadata, falls back to message ID.
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    // Check first hop for aggregate ID or stream key
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var aggregateIdObj)) {
      if (aggregateIdObj is Guid aggregateId) {
        return aggregateId;
      }
      if (aggregateIdObj is string aggregateIdStr && Guid.TryParse(aggregateIdStr, out var parsedAggregateId)) {
        return parsedAggregateId;
      }
    }

    // Fall back to message ID (ensures all messages have a stream)
    return envelope.MessageId.Value;
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
