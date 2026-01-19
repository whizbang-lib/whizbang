using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance (not critical for worker startup/shutdown)

namespace Whizbang.Core.Workers;

/// <summary>
/// Generic background service that consumes messages from any ITransport implementation.
/// Subscribes to configured destinations and dispatches received messages to IDispatcher.
/// </summary>
/// <docs>components/workers/transport-consumer</docs>
public class TransportConsumerWorker : BackgroundService {
  private readonly ITransport _transport;
  private readonly TransportConsumerOptions _options;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly OrderedStreamProcessor _orderedProcessor;
  private readonly ILifecycleInvoker? _lifecycleInvoker;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly ILogger<TransportConsumerWorker> _logger;
  private readonly List<ISubscription> _subscriptions = new();

  /// <summary>
  /// Initializes a new instance of TransportConsumerWorker.
  /// </summary>
  /// <param name="transport">The transport to consume messages from</param>
  /// <param name="options">Configuration options specifying destinations</param>
  /// <param name="scopeFactory">Service scope factory for creating scoped services</param>
  /// <param name="jsonOptions">JSON serialization options</param>
  /// <param name="orderedProcessor">Ordered stream processor for message ordering</param>
  /// <param name="lifecycleInvoker">Optional lifecycle invoker for PreInbox and PostInbox stages</param>
  /// <param name="lifecycleMessageDeserializer">Optional lifecycle message deserializer</param>
  /// <param name="logger">Logger instance</param>
  public TransportConsumerWorker(
    ITransport transport,
    TransportConsumerOptions options,
    IServiceScopeFactory scopeFactory,
    JsonSerializerOptions jsonOptions,
    OrderedStreamProcessor orderedProcessor,
    ILifecycleInvoker? lifecycleInvoker,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    ILogger<TransportConsumerWorker> logger
  ) {
    ArgumentNullException.ThrowIfNull(transport);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(jsonOptions);
    ArgumentNullException.ThrowIfNull(orderedProcessor);
    ArgumentNullException.ThrowIfNull(logger);

    _transport = transport;
    _options = options;
    _scopeFactory = scopeFactory;
    _jsonOptions = jsonOptions;
    _orderedProcessor = orderedProcessor;
    _lifecycleInvoker = lifecycleInvoker;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
    _logger = logger;
  }

  /// <summary>
  /// Executes the worker, creating subscriptions for all configured destinations.
  /// </summary>
  /// <param name="stoppingToken">Token to signal shutdown</param>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    _logger.LogInformation("TransportConsumerWorker starting");

    // Wait for transport readiness if readiness check is configured
    using (var scope = _scopeFactory.CreateScope()) {
      var readinessCheck = scope.ServiceProvider.GetService<ITransportReadinessCheck>();
      if (readinessCheck != null) {
        _logger.LogInformation("Waiting for transport readiness");
        var isReady = await readinessCheck.IsReadyAsync(stoppingToken);
        if (!isReady) {
          _logger.LogWarning("Transport readiness check returned false");
          return;
        }
        _logger.LogInformation("Transport is ready");
      }
    }

    // Subscribe to each destination
    foreach (var destination in _options.Destinations) {
      _logger.LogInformation(
        "Creating subscription for destination: {Address}, routing key: {RoutingKey}",
        destination.Address,
        destination.RoutingKey
      );

      var subscription = await _transport.SubscribeAsync(
        async (envelope, envelopeType, ct) => await _handleMessageAsync(envelope, envelopeType, ct),
        destination,
        stoppingToken
      );

      _subscriptions.Add(subscription);
    }

    _logger.LogInformation(
      "TransportConsumerWorker started with {Count} subscriptions",
      _subscriptions.Count
    );

    // Keep running until cancellation is requested
    try {
      await Task.Delay(Timeout.Infinite, stoppingToken);
    } catch (OperationCanceledException) {
      _logger.LogInformation("TransportConsumerWorker cancellation requested");
    }
  }

  /// <summary>
  /// Handles a received message using the inbox/work-coordinator pattern.
  /// Messages are stored in the inbox via process_work_batch for atomic deduplication.
  /// Perspectives are processed asynchronously by PerspectiveWorker.
  /// </summary>
  private async Task _handleMessageAsync(
    IMessageEnvelope envelope,
    string? envelopeType,
    CancellationToken cancellationToken
  ) {
    try {
      // Create scope to resolve scoped services (IWorkCoordinatorStrategy)
      await using var scope = _scopeFactory.CreateAsyncScope();
      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

      _logger.LogDebug(
        "Processing message {MessageId} from transport",
        envelope.MessageId
      );

      // 1. Serialize envelope to InboxMessage
      var newInboxMessage = _serializeToNewInboxMessage(envelope, envelopeType, scope.ServiceProvider);

      // 2. Queue for atomic deduplication via process_work_batch
      strategy.QueueInboxMessage(newInboxMessage);

      // 3. Flush - calls process_work_batch with atomic INSERT ... ON CONFLICT DO NOTHING
      var workBatch = await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

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

      // 5. Invoke PreInbox lifecycle stages (before local receptor invocation)
      if (_lifecycleInvoker is not null && _lifecycleMessageDeserializer is not null) {
        foreach (var work in myWork) {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);

          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PreInboxAsync,
            EventId = null,
            StreamId = null,
            LastProcessedEventId = null,
            MessageSource = MessageSource.Inbox,
            AttemptNumber = null // Attempt info not tracked for inbox work
          };

          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreInboxAsync, lifecycleContext, cancellationToken);

          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreInboxInline };
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreInboxInline, lifecycleContext, cancellationToken);
        }
      }

      // 6. Process using OrderedStreamProcessor (maintains stream ordering)
      // Perspectives are created automatically by process_work_batch and processed by PerspectiveWorker
      await _orderedProcessor.ProcessInboxWorkAsync(
        myWork,
        processor: async (work) => {
          // Deserialize event from work item
          var @event = _deserializeEvent(work);

          // Mark as EventStored - perspectives will be processed via PerspectiveWorker
          if (@event is IEvent) {
            return MessageProcessingStatus.EventStored;
          }

          // Non-event messages - just mark as stored
          return MessageProcessingStatus.EventStored;
        },
        completionHandler: (msgId, status) => {
          strategy.QueueInboxCompletion(msgId, status);
          _logger.LogDebug("Queued completion for {MessageId} with status {Status}", msgId, status);
        },
        failureHandler: (msgId, status, error) => {
          strategy.QueueInboxFailure(msgId, status, error);
          _logger.LogError("Queued failure for {MessageId}: {Error}", msgId, error);
        },
        cancellationToken
      );

      // 7. Invoke PostInbox lifecycle stages (after local receptor invocation)
      if (_lifecycleInvoker is not null && _lifecycleMessageDeserializer is not null) {
        foreach (var work in myWork) {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);

          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PostInboxAsync,
            EventId = null,
            StreamId = null,
            LastProcessedEventId = null,
            MessageSource = MessageSource.Inbox,
            AttemptNumber = null // Attempt info not tracked for inbox work
          };

          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostInboxAsync, lifecycleContext, cancellationToken);

          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostInboxInline };
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostInboxInline, lifecycleContext, cancellationToken);
        }
      }

      // 8. Report completions/failures back to database
      await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

      _logger.LogInformation("Successfully processed message {MessageId}", envelope.MessageId);
    } catch (Exception ex) {
      _logger.LogError(ex, "Error processing message {MessageId}", envelope.MessageId);
      throw; // Let the transport handle retry/dead-letter
    }
  }

  /// <summary>
  /// Creates InboxMessage for work coordinator pattern.
  /// Handles envelopes from transport which may be strongly-typed or JsonElement-typed.
  /// </summary>
  private InboxMessage _serializeToNewInboxMessage(
    IMessageEnvelope envelope,
    string? envelopeTypeFromTransport,
    IServiceProvider scopeServiceProvider
  ) {
    if (string.IsNullOrWhiteSpace(envelopeTypeFromTransport)) {
      throw new InvalidOperationException(
        $"EnvelopeType is required from transport but was null/empty. MessageId: {envelope.MessageId}");
    }

    // Extract message type from envelope type string
    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeTypeFromTransport);

    // Get payload to check its type
    var payload = envelope.Payload;
    var payloadType = payload?.GetType() ?? typeof(object);

    // Check if envelope/payload is already in JsonElement form
    IMessageEnvelope<JsonElement> jsonEnvelope;
    if (envelope is IMessageEnvelope<JsonElement> alreadyJsonEnvelope) {
      jsonEnvelope = alreadyJsonEnvelope;
    } else if (payloadType == typeof(JsonElement)) {
      throw new InvalidOperationException(
        $"Envelope has JsonElement payload but envelope type is {envelope.GetType().Name}. MessageId: {envelope.MessageId}");
    } else {
      // Strongly-typed envelope - serialize it
      var serializer = scopeServiceProvider.GetService<IEnvelopeSerializer>();
      if (serializer == null) {
        throw new InvalidOperationException("IEnvelopeSerializer is required but not registered");
      }

      // Call generic SerializeEnvelope method via reflection
      var genericMethod = typeof(IEnvelopeSerializer).GetMethod(nameof(IEnvelopeSerializer.SerializeEnvelope));
      var boundMethod = genericMethod!.MakeGenericMethod(payloadType);
      var serialized = (SerializedEnvelope)boundMethod.Invoke(serializer, new object[] { envelope })!;
      jsonEnvelope = serialized.JsonEnvelope;
    }

    // Determine if message is an event using IEventTypeProvider
    // This is more reliable than "payload is IEvent" when payload is JsonElement
    var isEvent = false;
    var eventTypeProvider = scopeServiceProvider.GetService<IEventTypeProvider>();
    if (eventTypeProvider != null) {
      var eventTypes = eventTypeProvider.GetEventTypes();
      isEvent = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);
    } else {
      // Fallback to runtime check if provider not available
      isEvent = payload is IEvent;
    }

    // Extract simple type name
    var lastDotIndex = messageTypeName.LastIndexOf('.');
    var simpleTypeName = lastDotIndex >= 0
      ? messageTypeName.Substring(lastDotIndex + 1).Split(',')[0].Trim()
      : messageTypeName.Split(',')[0].Trim();
    var handlerName = simpleTypeName + "Handler";

    var streamId = _extractStreamId(envelope);

    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeFromTransport,
      StreamId = streamId,
      IsEvent = isEvent,
      MessageType = messageTypeName
    };
  }

  /// <summary>
  /// Extracts message type from envelope type name.
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException($"Invalid envelope type name format: '{envelopeTypeName}'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException($"Failed to extract message type from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
  }

  /// <summary>
  /// Deserializes event payload from InboxWork.
  /// </summary>
  private object? _deserializeEvent(InboxWork work) {
    try {
      var jsonElement = work.Envelope.Payload;
      var jsonTypeInfo = Serialization.JsonContextRegistry.GetTypeInfoByName(work.MessageType, _jsonOptions);
      if (jsonTypeInfo == null) {
        _logger.LogError("Could not resolve JsonTypeInfo for type {MessageType} for message {MessageId}",
          work.MessageType, work.MessageId);
        return null;
      }

      return JsonSerializer.Deserialize(jsonElement, jsonTypeInfo);
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to deserialize event for message {MessageId}", work.MessageId);
      return null;
    }
  }

  /// <summary>
  /// Extracts stream_id from envelope for stream-based ordering.
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var aggregateIdElem) &&
        aggregateIdElem.ValueKind == JsonValueKind.String) {
      var aggregateIdStr = aggregateIdElem.GetString();
      if (aggregateIdStr != null && Guid.TryParse(aggregateIdStr, out var parsedAggregateId)) {
        return parsedAggregateId;
      }
    }

    return envelope.MessageId.Value;
  }

  /// <summary>
  /// Pauses all active subscriptions.
  /// Messages will not be processed until resumed.
  /// </summary>
  public async Task PauseAllSubscriptionsAsync() {
    _logger.LogInformation("Pausing all subscriptions");

    foreach (var subscription in _subscriptions) {
      await subscription.PauseAsync();
    }

    _logger.LogInformation("All subscriptions paused");
  }

  /// <summary>
  /// Resumes all paused subscriptions.
  /// Message processing will continue.
  /// </summary>
  public async Task ResumeAllSubscriptionsAsync() {
    _logger.LogInformation("Resuming all subscriptions");

    foreach (var subscription in _subscriptions) {
      await subscription.ResumeAsync();
    }

    _logger.LogInformation("All subscriptions resumed");
  }

  /// <summary>
  /// Stops the worker and disposes all subscriptions.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  public override async Task StopAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Stopping TransportConsumerWorker");

    // Dispose all subscriptions
    foreach (var subscription in _subscriptions) {
      subscription.Dispose();
    }

    _subscriptions.Clear();

    _logger.LogInformation("TransportConsumerWorker stopped");

    await base.StopAsync(cancellationToken);
  }
}
