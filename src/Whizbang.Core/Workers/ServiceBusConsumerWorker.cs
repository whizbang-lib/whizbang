using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background service that subscribes to messages from Azure Service Bus and invokes local perspectives.
/// Uses work coordinator pattern for atomic deduplication and stream-based ordering.
/// Events from remote services are stored in inbox via process_work_batch and perspectives are invoked with ordering guarantees.
/// </summary>
public partial class ServiceBusConsumerWorker(
  IServiceInstanceProvider instanceProvider,
  ITransport transport,
  IServiceScopeFactory scopeFactory,
  JsonSerializerOptions jsonOptions,
  ILogger<ServiceBusConsumerWorker> logger,
  OrderedStreamProcessor orderedProcessor,
  ServiceBusConsumerOptions? options = null,
  ILifecycleInvoker? lifecycleInvoker = null,
  ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
  IEnvelopeSerializer? envelopeSerializer = null
  ) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<ServiceBusConsumerWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly OrderedStreamProcessor _orderedProcessor = orderedProcessor ?? throw new ArgumentNullException(nameof(orderedProcessor));
  private readonly ILifecycleInvoker? _lifecycleInvoker = lifecycleInvoker;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
  private readonly IEnvelopeSerializer? _envelopeSerializer = envelopeSerializer;
  private readonly List<ISubscription> _subscriptions = [];
  private readonly ServiceBusConsumerOptions _options = options ?? new ServiceBusConsumerOptions();

  /// <summary>
  /// Pauses all subscriptions to temporarily stop receiving messages.
  /// Useful for test cleanup scenarios where draining is needed without competing consumers.
  /// </summary>
  public async Task PauseAllSubscriptionsAsync() {
    foreach (var subscription in _subscriptions) {
      await subscription.PauseAsync();
    }
  }

  /// <summary>
  /// Resumes all subscriptions to continue receiving messages.
  /// </summary>
  public async Task ResumeAllSubscriptionsAsync() {
    foreach (var subscription in _subscriptions) {
      await subscription.ResumeAsync();
    }
  }

  /// <summary>
  /// Starts the worker and creates all subscriptions BEFORE background processing begins.
  /// This ensures subscriptions are ready before ExecuteAsync runs (blocking initialization).
  /// </summary>
  public override async Task StartAsync(CancellationToken cancellationToken) {
    using var activity = WhizbangActivitySource.Hosting.StartActivity("ServiceBusConsumerWorker.Start");
    activity?.SetTag("worker.subscriptions_count", _options.Subscriptions.Count);
    activity?.SetTag("servicebus.has_filter", _options.Subscriptions.Any(s => !string.IsNullOrWhiteSpace(s.DestinationFilter)));

    LogWorkerStarting(_logger);

    try {
      // Subscribe to configured topics (BLOCKING - ensures subscriptions ready before ExecuteAsync)
      foreach (var topicConfig in _options.Subscriptions) {
        // Create destination with DestinationFilter metadata if specified
        var metadata = !string.IsNullOrWhiteSpace(topicConfig.DestinationFilter)
          ? new Dictionary<string, JsonElement> { ["DestinationFilter"] = JsonElementHelper.FromString(topicConfig.DestinationFilter) }
          : null;

        var destination = new TransportDestination(
          topicConfig.TopicName,
          topicConfig.SubscriptionName,
          metadata
        );

        var subscription = await _transport.SubscribeAsync(
          async (envelope, envelopeType, ct) => await _handleMessageAsync(envelope, envelopeType, ct),
          destination,
          cancellationToken
        );

        _subscriptions.Add(subscription);

        LogSubscribedToTopic(_logger, topicConfig.TopicName, topicConfig.SubscriptionName);
      }

      LogSubscriptionsReady(_logger, _subscriptions.Count);

      // Call base.StartAsync to trigger ExecuteAsync
      await base.StartAsync(cancellationToken);
    } catch (Exception ex) {
      LogFailedToStart(_logger, ex);
      throw;
    }
  }

  /// <summary>
  /// Background processing loop - keeps worker alive while subscriptions process messages.
  /// Subscriptions are already created in StartAsync (blocking), so this just waits.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogBackgroundProcessingStarted(_logger);

    try {
      // Keep the worker running while subscriptions are active
      await Task.Delay(Timeout.Infinite, stoppingToken);
    } catch (OperationCanceledException) {
      LogWorkerStopping(_logger);
    } catch (Exception ex) {
      LogFatalError(_logger, ex);
      throw;
    }
  }

  private async Task _handleMessageAsync(IMessageEnvelope envelope, string? envelopeType, CancellationToken ct) {
    try {
      // Create scope to resolve scoped services (IWorkCoordinatorStrategy, IPerspectiveInvoker)
      await using var scope = _scopeFactory.CreateAsyncScope();
      var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();

      LogProcessingMessage(_logger, envelope.MessageId);

      // 1. Serialize envelope to InboxMessage
      // Pass scope so we can resolve IEnvelopeSerializer if needed
      var newInboxMessage = _serializeToNewInboxMessage(envelope, envelopeType, scope.ServiceProvider);

      // 2. Queue for atomic deduplication via process_work_batch
      strategy.QueueInboxMessage(newInboxMessage);

      // 3. Flush - calls process_work_batch with atomic INSERT ... ON CONFLICT DO NOTHING
      var workBatch = await strategy.FlushAsync(WorkBatchFlags.None, ct);

      // 4. Check if work was returned - empty means duplicate (already processed)
      var myWork = workBatch.InboxWork.Where(w => w.MessageId == envelope.MessageId.Value).ToList();

      if (myWork.Count == 0) {
        LogMessageAlreadyProcessed(_logger, envelope.MessageId);
        return;
      }

      LogMessageAcceptedForProcessing(_logger, envelope.MessageId, myWork.Count);

      // 5. Process using OrderedStreamProcessor (maintains stream ordering)
      // NOTE: Inline perspective invocation has been removed - perspectives are now processed via:
      // 1. process_work_batch automatically creates perspective checkpoints (Migration 006)
      // 2. PerspectiveWorker picks up checkpoints and processes them asynchronously
      // This provides better reliability, scalability, and separation of concerns.

      // PreInbox lifecycle stages (before local receptor invocation)
      if (_lifecycleInvoker is not null && _lifecycleMessageDeserializer is not null) {
        foreach (var work in myWork) {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);

          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PreInboxAsync,
            EventId = null,
            StreamId = null,
            PerspectiveName = null,
            LastProcessedEventId = null
          };

          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreInboxAsync, lifecycleContext, ct);

          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreInboxInline };
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreInboxInline, lifecycleContext, ct);
        }
      }

      await _orderedProcessor.ProcessInboxWorkAsync(
        myWork,
        processor: async (work) => {
          // Deserialize event from work item
          var @event = _deserializeEvent(work);

          // Mark as EventStored - perspectives will be processed via PerspectiveWorker
          // from checkpoints created by process_work_batch
          if (@event is IEvent) {
            return MessageProcessingStatus.EventStored;
          }

          // Non-event messages (if any) - just mark as stored
          return MessageProcessingStatus.EventStored;
        },
        completionHandler: (msgId, status) => {
          strategy.QueueInboxCompletion(msgId, status);
          LogQueuedCompletion(_logger, msgId, status);
        },
        failureHandler: (msgId, status, error) => {
          strategy.QueueInboxFailure(msgId, status, error);
          LogQueuedFailure(_logger, msgId, error);
        },
        ct
      );

      // PostInbox lifecycle stages (after local receptor invocation)
      if (_lifecycleInvoker is not null && _lifecycleMessageDeserializer is not null) {
        foreach (var work in myWork) {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);

          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PostInboxAsync,
            EventId = null,
            StreamId = null,
            PerspectiveName = null,
            LastProcessedEventId = null
          };

          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostInboxAsync, lifecycleContext, ct);

          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostInboxInline };
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostInboxInline, lifecycleContext, ct);
        }
      }

      // 6. Report completions/failures back to database
      await strategy.FlushAsync(WorkBatchFlags.None, ct);

      LogSuccessfullyProcessedMessage(_logger, envelope.MessageId);

      // Scope will be disposed automatically by 'await using' at end of method
    } catch (Exception ex) {
      LogErrorProcessingMessage(_logger, envelope.MessageId, ex);
      throw; // Let the transport handle retry/dead-letter
    }
  }

  /// <summary>
  /// Creates InboxMessage for work coordinator pattern.
  /// Handles envelopes from transport which may be strongly-typed or JsonElement-typed.
  /// The actual type information is preserved in envelopeTypeFromTransport for later deserialization.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  private InboxMessage _serializeToNewInboxMessage(IMessageEnvelope envelope, string? envelopeTypeFromTransport, IServiceProvider scopeServiceProvider) {
    // Envelopes from transport can be:
    // 1. Strongly-typed: MessageEnvelope<ProductCreatedEvent> - needs serialization to JsonElement form
    // 2. JsonElement-typed: MessageEnvelope<JsonElement> - already in storage form

    if (string.IsNullOrWhiteSpace(envelopeTypeFromTransport)) {
      throw new InvalidOperationException(
        $"EnvelopeType is required from transport but was null/empty. MessageId: {envelope.MessageId}. " +
        $"This indicates a bug in the transport layer - envelope type must be preserved during transmission.");
    }

    // Extract message type from envelope type string
    // Example: "MessageEnvelope`1[[MyApp.ProductCreatedEvent, MyApp]], Whizbang.Core"
    // We need to extract: "MyApp.ProductCreatedEvent, MyApp"
    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeTypeFromTransport);

    // Get payload to check its type
    var payload = envelope.Payload;
    var payloadType = payload?.GetType() ?? typeof(object);

    // Check if envelope/payload is already in JsonElement form
    // CRITICAL: Must check payload type, not just envelope type, because envelope could be
    // IMessageEnvelope<object> with a JsonElement payload
    IMessageEnvelope<JsonElement> jsonEnvelope;
    if (envelope is IMessageEnvelope<JsonElement> alreadyJsonEnvelope) {
      // Envelope is correctly typed as IMessageEnvelope<JsonElement> - use directly
      jsonEnvelope = alreadyJsonEnvelope;
    } else if (payloadType == typeof(JsonElement)) {
      // Payload is JsonElement but envelope is not IMessageEnvelope<JsonElement>
      // This means envelope is probably IMessageEnvelope<object> with JsonElement payload
      // DEFENSIVE: This should not happen - envelopes from transport should be strongly-typed
      throw new InvalidOperationException(
        $"Envelope has JsonElement payload but envelope type is {envelope.GetType().Name}. " +
        $"MessageId: {envelope.MessageId}. " +
        $"This indicates double-serialization or incorrect envelope creation. " +
        $"Envelopes from transport must be strongly-typed (e.g., MessageEnvelope<ProductCreatedEvent>), " +
        $"not MessageEnvelope<object> or MessageEnvelope<JsonElement>.");
    } else {
      // Strongly-typed envelope - need to serialize it to JsonElement form for storage
      var serializer = _envelopeSerializer ?? scopeServiceProvider.GetService<IEnvelopeSerializer>();
      if (serializer == null) {
        throw new InvalidOperationException(
          "IEnvelopeSerializer is required but not registered. " +
          "Ensure you call services.AddWhizbang() to register core services.");
      }

      // Call generic SerializeEnvelope method via reflection (necessary because payload type is only known at runtime)
      var genericEnvelopeMethod = typeof(IEnvelopeSerializer).GetMethod(nameof(IEnvelopeSerializer.SerializeEnvelope));
      var boundMethod = genericEnvelopeMethod!.MakeGenericMethod(payloadType);
      var serialized = (SerializedEnvelope)boundMethod.Invoke(serializer, new object[] { envelope })!;
      jsonEnvelope = serialized.JsonEnvelope;

      // NOTE: We use envelopeTypeFromTransport instead of serialized.EnvelopeType
      // because the transport's metadata is authoritative
    }

    // Determine if message is an event
    var isEvent = payload is IEvent;

    // Extract simple type name for handler name (last part after last '.')
    var lastDotIndex = messageTypeName.LastIndexOf('.');
    var simpleTypeName = lastDotIndex >= 0
      ? messageTypeName.Substring(lastDotIndex + 1).Split(',')[0].Trim()
      : messageTypeName.Split(',')[0].Trim();
    var handlerName = simpleTypeName + "Handler";

    var streamId = _extractStreamId(envelope);

    LogSerializeInboxMessage(_logger, envelope.MessageId.Value, simpleTypeName, isEvent, streamId);

    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeFromTransport,  // Use the original type from transport!
      StreamId = streamId,
      IsEvent = isEvent,
      MessageType = messageTypeName
    };
  }

  /// <summary>
  /// Extracts the message type name from an envelope type name.
  /// Parses "MessageEnvelope`1[[MyApp.CreateProductCommand, MyApp]], Whizbang.Core"
  /// and returns "MyApp.CreateProductCommand, MyApp".
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException(
        $"Invalid envelope type name format: '{envelopeTypeName}'. " +
        $"Expected format: 'MessageEnvelope`1[[MessageType, Assembly]], EnvelopeAssembly'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException(
        $"Failed to extract message type name from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
  }

  /// <summary>
  /// Extracts event payload from InboxWork for processing.
  /// Envelope is deserialized as MessageEnvelope&lt;JsonElement&gt;, so we need to deserialize the payload.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  private object? _deserializeEvent(InboxWork work) {
    try {
      // InboxWork envelope is IMessageEnvelope<JsonElement>
      // Deserialize the JsonElement payload back to the actual event type
      var jsonElement = work.Envelope.Payload;

      // Use GetTypeInfoByName from JsonContextRegistry for AOT-safe cross-assembly type lookup
      // This queries all registered type name mappings from all assemblies via ModuleInitializers
      // Supports fuzzy matching on "TypeName, AssemblyName" (strips Version/Culture/PublicKeyToken)
      var jsonTypeInfo = Serialization.JsonContextRegistry.GetTypeInfoByName(work.MessageType, _jsonOptions);
      if (jsonTypeInfo == null) {
        LogCouldNotResolveJsonTypeInfo(_logger, work.MessageType, work.MessageId);
        return null;
      }

      var @event = JsonSerializer.Deserialize(jsonElement, jsonTypeInfo);
      return @event;
    } catch (Exception ex) {
      LogFailedToDeserializeEvent(_logger, work.MessageId, ex);
      return null;
    }
  }

  /// <summary>
  /// Serializes envelope metadata (MessageId + Hops) to JSON string.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
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
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
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
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    // Check first hop for aggregate ID or stream key
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var aggregateIdElem)) {
      // Try to parse as GUID from JsonElement
      if (aggregateIdElem.ValueKind == JsonValueKind.String) {
        var aggregateIdStr = aggregateIdElem.GetString();
        if (aggregateIdStr != null && Guid.TryParse(aggregateIdStr, out var parsedAggregateId)) {
          return parsedAggregateId;
        }
      }
    }

    // Fall back to message ID (ensures all messages have a stream)
    return envelope.MessageId.Value;
  }

  /// <summary>
  /// Stops the worker and disposes all subscriptions.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  public override async Task StopAsync(CancellationToken cancellationToken) {
    LogWorkerStoppingGracefully(_logger);

    // Dispose all subscriptions
    foreach (var subscription in _subscriptions) {
      subscription.Dispose();
    }

    await base.StopAsync(cancellationToken);
  }

  // ========================================
  // High-Performance LoggerMessage Delegates
  // ========================================

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker starting - creating subscriptions..."
  )]
  static partial void LogWorkerStarting(ILogger logger);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Information,
    Message = "Subscribed to topic {TopicName} with subscription {SubscriptionName}"
  )]
  static partial void LogSubscribedToTopic(ILogger logger, string topicName, string subscriptionName);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker subscriptions ready ({Count} subscriptions)"
  )]
  static partial void LogSubscriptionsReady(ILogger logger, int count);

  [LoggerMessage(
    EventId = 19,
    Level = LogLevel.Warning,
    Message = "[ServiceBusConsumer DIAGNOSTIC] _serializeToNewInboxMessage: MessageId={MessageId}, PayloadType={PayloadType}, IsEvent={IsEvent}, StreamId={StreamId}"
  )]
  static partial void LogSerializeInboxMessage(ILogger logger, Guid messageId, string payloadType, bool isEvent, Guid streamId);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Error,
    Message = "Failed to start ServiceBusConsumerWorker - subscriptions not ready"
  )]
  static partial void LogFailedToStart(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker background processing started"
  )]
  static partial void LogBackgroundProcessingStarted(ILogger logger);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker is stopping..."
  )]
  static partial void LogWorkerStopping(ILogger logger);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Error,
    Message = "Fatal error in ServiceBusConsumerWorker"
  )]
  static partial void LogFatalError(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Information,
    Message = "Processing message {MessageId} from Service Bus"
  )]
  static partial void LogProcessingMessage(ILogger logger, MessageId messageId);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Information,
    Message = "Message {MessageId} already processed (duplicate), skipping"
  )]
  static partial void LogMessageAlreadyProcessed(ILogger logger, MessageId messageId);

  [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Information,
    Message = "Message {MessageId} accepted for processing ({WorkCount} inbox work items)"
  )]
  static partial void LogMessageAcceptedForProcessing(ILogger logger, MessageId messageId, int workCount);

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Information,
    Message = "Invoked perspectives for {EventType} (message {MessageId})"
  )]
  static partial void LogInvokedPerspectives(ILogger logger, string eventType, Guid messageId);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Warning,
    Message = "Failed to invoke perspectives - Event: {EventType}, HasInvoker: {HasInvoker}"
  )]
  static partial void LogFailedToInvokePerspectives(ILogger logger, string eventType, bool hasInvoker);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Debug,
    Message = "Queued completion for {MessageId} with status {Status}"
  )]
  static partial void LogQueuedCompletion(ILogger logger, Guid messageId, MessageProcessingStatus status);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Error,
    Message = "Queued failure for {MessageId}: {Error}"
  )]
  static partial void LogQueuedFailure(ILogger logger, Guid messageId, string error);

  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Information,
    Message = "Successfully processed message {MessageId}"
  )]
  static partial void LogSuccessfullyProcessedMessage(ILogger logger, MessageId messageId);

  [LoggerMessage(
    EventId = 16,
    Level = LogLevel.Error,
    Message = "Error processing message {MessageId}"
  )]
  static partial void LogErrorProcessingMessage(ILogger logger, MessageId messageId, Exception ex);

  [LoggerMessage(
    EventId = 17,
    Level = LogLevel.Error,
    Message = "Could not resolve JsonTypeInfo for message type {MessageType} for message {MessageId}"
  )]
  static partial void LogCouldNotResolveJsonTypeInfo(ILogger logger, string messageType, Guid messageId);

  [LoggerMessage(
    EventId = 18,
    Level = LogLevel.Error,
    Message = "Failed to deserialize event payload from envelope for message {MessageId}"
  )]
  static partial void LogFailedToDeserializeEvent(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(
    EventId = 19,
    Level = LogLevel.Information,
    Message = "ServiceBusConsumerWorker stopping..."
  )]
  static partial void LogWorkerStoppingGracefully(ILogger logger);
}

/// <summary>
/// Configuration options for ServiceBusConsumerWorker.
/// </summary>
/// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
/// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
public class ServiceBusConsumerOptions {
  /// <summary>
  /// List of topic subscriptions to consume messages from.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
  /// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
  public List<TopicSubscription> Subscriptions { get; set; } = [];
}

/// <summary>
/// Configuration for a single topic subscription.
/// </summary>
/// <param name="TopicName">The Service Bus topic name</param>
/// <param name="SubscriptionName">The subscription name for this consumer</param>
/// <param name="DestinationFilter">Optional destination filter value (e.g., "inventory-service")</param>
/// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_InvokesPerspectives_BeforeScopeDisposalAsync</tests>
/// <tests>Whizbang.Core.Tests/Workers/ServiceBusConsumerWorkerTests.cs:HandleMessage_AlreadyProcessed_SkipsPerspectiveInvocationAsync</tests>
public record TopicSubscription(string TopicName, string SubscriptionName, string? DestinationFilter = null);
