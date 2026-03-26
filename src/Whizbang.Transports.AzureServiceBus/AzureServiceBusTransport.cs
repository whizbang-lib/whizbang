using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Azure Service Bus implementation of ITransport.
/// Provides reliable, ordered message delivery using Azure Service Bus topics and subscriptions.
/// </summary>
/// <tests>No tests found</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Transport implementation with diagnostic logging - I/O bound operations where LoggerMessage overhead isn't justified")]
public class AzureServiceBusTransport : ITransport, ITransportWithRecovery, IAsyncDisposable {
  private readonly ServiceBusClient _client;
  private readonly IServiceBusAdminClient? _adminClient;
  private readonly ILogger<AzureServiceBusTransport> _logger;
  private readonly Dictionary<string, ServiceBusSender> _senders = [];
  private readonly SemaphoreSlim _senderLock = new(1, 1);
  private readonly AzureServiceBusOptions _options;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly bool _isEmulator;
  private Func<CancellationToken, Task>? _recoveryHandler;
  private bool _disposed;
  private bool _isInitialized;

  /// <summary>
  /// Initializes a new instance of AzureServiceBusTransport with a shared ServiceBusClient.
  /// The transport does NOT dispose the injected client - the DI container manages its lifetime.
  /// </summary>
  /// <param name="client">Shared ServiceBusClient instance (managed by DI container)</param>
  /// <param name="jsonOptions">JSON serialization options</param>
  /// <param name="options">Optional transport configuration</param>
  /// <param name="logger">Optional logger instance</param>
  /// <param name="adminClient">Optional admin client for auto-provisioning infrastructure</param>
  public AzureServiceBusTransport(
    ServiceBusClient client,
    JsonSerializerOptions jsonOptions,
    AzureServiceBusOptions? options = null,
    ILogger<AzureServiceBusTransport>? logger = null,
    IServiceBusAdminClient? adminClient = null
  ) {
    using var activity = WhizbangActivitySource.Transport.StartActivity("AzureServiceBusTransport.Initialize");

    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(jsonOptions);

    _client = client;
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureServiceBusTransport>.Instance;
    _adminClient = adminClient;

    // Detect emulator from client endpoint
    var endpoint = client.FullyQualifiedNamespace;
    _isEmulator = endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                  endpoint.Contains("127.0.0.1");

    _jsonOptions = jsonOptions;
    _options = options ?? new AzureServiceBusOptions();

    // Log admin client availability
    if (_adminClient != null) {
      _logger.LogInformation("Admin client provided - auto-provisioning enabled");
    } else {
      _logger.LogInformation("No admin client - auto-provisioning disabled, infrastructure must be pre-provisioned");
    }

    // Add OTEL tags for observability
    activity?.SetTag("transport.type", "AzureServiceBus");
    activity?.SetTag("transport.emulator", _isEmulator);
    activity?.SetTag("transport.admin_client_available", _adminClient != null);
    activity?.SetTag("transport.auto_provision", _options.AutoProvisionInfrastructure);
  }

  /// <inheritdoc />
  public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) {
    _recoveryHandler = onRecovered;
  }

  /// <summary>
  /// Determines if a Service Bus exception indicates a connection-level error
  /// that warrants triggering subscription recovery.
  /// </summary>
  private static bool _isConnectionError(Exception ex) {
    if (ex is ServiceBusException sbEx) {
      return sbEx.Reason is
        ServiceBusFailureReason.ServiceCommunicationProblem or
        ServiceBusFailureReason.ServiceBusy or
        ServiceBusFailureReason.ServiceTimeout;
    }
    return false;
  }

  /// <summary>
  /// Invokes the recovery handler if set and appropriate.
  /// </summary>
  private async Task _invokeRecoveryHandlerAsync() {
    if (_recoveryHandler != null) {
      try {
        _logger.LogInformation("Azure Service Bus connection recovered, invoking recovery handler");
        await _recoveryHandler(CancellationToken.None);
      } catch (Exception ex) {
        _logger.LogError(ex, "Error in recovery handler after Service Bus connection recovery");
      }
    }
  }

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public bool IsInitialized => _isInitialized;

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    using var activity = WhizbangActivitySource.Transport.StartActivity("AzureServiceBusTransport.Initialize");

    cancellationToken.ThrowIfCancellationRequested();

    // Idempotent - only initialize once
    if (_isInitialized) {
      _logger.LogDebug("Transport already initialized, skipping");
      return;
    }

    try {
      // Verify client is not closed
      if (_client.IsClosed) {
        throw new InvalidOperationException("ServiceBusClient is closed and cannot be initialized");
      }

      // For emulator, we can't verify connectivity via admin API (not supported)
      // Just mark as initialized if client is not closed
      if (_isEmulator) {
        _logger.LogInformation("Emulator detected - marking transport as initialized (admin verification skipped)");
        _isInitialized = true;
        activity?.SetTag("transport.initialized", true);
        activity?.SetTag("transport.verification_method", "emulator_skip");
        return;
      }

      // For production Service Bus, verify connectivity via admin client
      if (_adminClient != null) {
        // Simple connectivity check - try to list namespaces (lightweight operation)
        // This will throw if Service Bus is not reachable
        await _adminClient.GetNamespacePropertiesAsync(cancellationToken);

        _logger.LogInformation("Azure Service Bus transport initialized successfully - connectivity verified");
        _isInitialized = true;
        activity?.SetTag("transport.initialized", true);
        activity?.SetTag("transport.verification_method", "admin_api");
      } else {
        // No admin client available - just check if regular client is open
        _logger.LogWarning("Admin client not available - marking transport as initialized without connectivity verification");
        _isInitialized = true;
        activity?.SetTag("transport.initialized", true);
        activity?.SetTag("transport.verification_method", "client_open_check");
      }
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to initialize Azure Service Bus transport");
      activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      throw new InvalidOperationException("Failed to initialize Azure Service Bus transport - Service Bus may not be reachable", ex);
    }
  }

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public TransportCapabilities Capabilities =>
    TransportCapabilities.PublishSubscribe |
    TransportCapabilities.Reliable |
    TransportCapabilities.Ordered |
    TransportCapabilities.BulkPublish;

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);
    return _publishCoreAsync(envelope, destination, envelopeType, cancellationToken);
  }

  private async Task _publishCoreAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType,
    CancellationToken cancellationToken
  ) {
    try {
      var sender = await _getOrCreateSenderAsync(destination.Address, cancellationToken);

      // Use provided envelope type name if available, otherwise get it from runtime type
      // IMPORTANT: The envelope object is already correctly typed (MessageEnvelope<JsonElement>), so we serialize using envelope.GetType()
      //            But for METADATA, we use the provided envelopeType string which preserves the original payload type information
      var envelopeTypeName = envelopeType ?? envelope.GetType().AssemblyQualifiedName
        ?? throw new InvalidOperationException("Envelope type must have an assembly qualified name");

      // For serialization, always use the actual runtime type of the envelope object (AOT-safe)
      var envelopeRuntimeType = envelope.GetType();

      // Serialize envelope to JSON using AOT-compatible options from registry
      var typeInfo = _jsonOptions.GetTypeInfo(envelopeRuntimeType)
        ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeRuntimeType.Name}. Ensure the message type is registered via JsonContextRegistry.");
      var json = JsonSerializer.Serialize(envelope, typeInfo);

      // DIAGNOSTIC: Log the first 500 chars of JSON to see if MessageId is in there
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var messageId = envelope.MessageId.Value;
        var jsonPreview = json.Length > 500 ? json[..500] + "..." : json;
        _logger.LogDebug(
          "DIAGNOSTIC [Publish]: Serialized envelope. MessageId={MessageId}, JSON preview: {JsonPreview}",
          messageId,
          jsonPreview
        );
      }

      var message = new ServiceBusMessage(json) {
        MessageId = envelope.MessageId.Value.ToString(),
        Subject = destination.RoutingKey ?? "message",
        ContentType = "application/json"
      };

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "[Publish] Setting Subject={Subject} on message {MessageId} to topic {TopicName} (RoutingKey={RoutingKey})",
          message.Subject,
          envelope.MessageId,
          destination.Address,
          destination.RoutingKey ?? "(null)");
      }

      // DIAGNOSTIC: Log the Service Bus message ID to compare
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var serviceBusMessageId = message.MessageId;
        _logger.LogDebug(
          "DIAGNOSTIC [Publish]: Created ServiceBusMessage with MessageId={ServiceBusMessageId}",
          serviceBusMessageId
        );
      }

      // Add envelope type information for deserialization
      message.ApplicationProperties["EnvelopeType"] = envelopeTypeName;

      // Add correlation ID if present
      var correlationId = envelope.GetCorrelationId();
      if (correlationId != null) {
        message.CorrelationId = correlationId.Value.Value.ToString();
      }

      // Add causation ID if present
      var causationId = envelope.GetCausationId();
      if (causationId != null) {
        message.ApplicationProperties["CausationId"] = causationId.Value.Value.ToString();
      }

      // Add custom metadata (converting JsonElement to AMQP-compatible primitives)
      if (destination.Metadata != null) {
        foreach (var (key, value) in destination.Metadata) {
          message.ApplicationProperties[key] = _convertJsonElementToAmqpValue(value);
        }
      }

      // WORKAROUND: Azure Service Bus Emulator sometimes hangs on first send
      // Use a task-based timeout instead of CancellationToken (which also hangs)
      // Increased to 30 seconds for emulator (originally 5s, too short for slow emulator with many topics)
      var sendTask = sender.SendMessageAsync(message, cancellationToken);
      var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

      var completedTask = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);

      if (completedTask == timeoutTask) {
        _logger.LogError("DIAGNOSTIC [PublishAsync]: SendMessageAsync timed out after 30 seconds for {MessageId} - emulator may not be ready", envelope.MessageId);
        throw new TimeoutException($"SendMessageAsync timed out after 30 seconds for message {envelope.MessageId}. The Azure Service Bus emulator may not be ready or topics/subscriptions may not exist.");
      }

      await sendTask; // Re-await to propagate exceptions

      if (_logger.IsEnabled(LogLevel.Debug)) {
        var messageId = envelope.MessageId;
        var topicName = destination.Address;
        var subject = message.Subject;
        _logger.LogDebug(
          "Published message {MessageId} to topic {TopicName} with subject {Subject}",
          messageId,
          topicName,
          subject
        );
      }
#pragma warning disable S2139 // Intentional log-and-rethrow: transport errors cross async/DI boundaries where the original exception context may be lost.
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Failed to publish message {MessageId} to {Destination}",
        envelope.MessageId,
        destination.Address
      );
      throw;
#pragma warning restore S2139
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
    IReadOnlyList<BulkPublishItem> items,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(items);
    ArgumentNullException.ThrowIfNull(destination);

    if (items.Count == 0) {
      return [];
    }

    var results = new List<BulkPublishItemResult>(items.Count);
    var sender = await _getOrCreateSenderAsync(destination.Address, cancellationToken);

    var currentBatch = await sender.CreateMessageBatchAsync(cancellationToken);
    var batchItemIds = new List<Guid>();

    for (var i = 0; i < items.Count; i++) {
      var item = items[i];
      try {
        var message = _createServiceBusMessage(item, destination);

        if (!currentBatch.TryAddMessage(message)) {
          // Current batch is full -- send and start new
          await _sendAndRecordBatchAsync(sender, currentBatch, batchItemIds, results, cancellationToken);
          currentBatch = await sender.CreateMessageBatchAsync(cancellationToken);
          batchItemIds = [];

          if (!currentBatch.TryAddMessage(message)) {
            results.Add(new BulkPublishItemResult {
              MessageId = item.MessageId,
              Success = false,
              Error = $"Message {item.MessageId} exceeds maximum batch message size"
            });
            continue;
          }
        }

        batchItemIds.Add(item.MessageId);
      } catch (Exception ex) {
        results.Add(new BulkPublishItemResult {
          MessageId = item.MessageId,
          Success = false,
          Error = $"{ex.GetType().Name}: {ex.Message}"
        });
      }
    }

    // Send remaining batch
    await _sendAndRecordBatchAsync(sender, currentBatch, batchItemIds, results, cancellationToken);

    return results;
  }

  private static async Task _sendAndRecordBatchAsync(
    ServiceBusSender sender,
    ServiceBusMessageBatch batch,
    List<Guid> batchItemIds,
    List<BulkPublishItemResult> results,
    CancellationToken cancellationToken) {

    if (batch.Count == 0) {
      return;
    }

    try {
      await sender.SendMessagesAsync(batch, cancellationToken);
      foreach (var id in batchItemIds) {
        results.Add(new BulkPublishItemResult { MessageId = id, Success = true });
      }
    } catch (Exception ex) {
      foreach (var id in batchItemIds) {
        results.Add(new BulkPublishItemResult {
          MessageId = id,
          Success = false,
          Error = $"{ex.GetType().Name}: {ex.Message}"
        });
      }
    }
  }

  private ServiceBusMessage _createServiceBusMessage(BulkPublishItem item, TransportDestination destination) {
    var envelope = item.Envelope;
    var envelopeTypeName = item.EnvelopeType ?? envelope.GetType().AssemblyQualifiedName
      ?? throw new InvalidOperationException("Envelope type must have an assembly qualified name");

    var envelopeRuntimeType = envelope.GetType();
    var typeInfo = _jsonOptions.GetTypeInfo(envelopeRuntimeType)
      ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeRuntimeType.Name}. Ensure the message type is registered via JsonContextRegistry.");
    var json = JsonSerializer.Serialize(envelope, typeInfo);

    var message = new ServiceBusMessage(json) {
      MessageId = envelope.MessageId.Value.ToString(),
      Subject = item.RoutingKey ?? destination.RoutingKey ?? "message",
      ContentType = "application/json"
    };

    message.ApplicationProperties["EnvelopeType"] = envelopeTypeName;

    var correlationId = envelope.GetCorrelationId();
    if (correlationId != null) {
      message.CorrelationId = correlationId.Value.Value.ToString();
    }

    var causationId = envelope.GetCausationId();
    if (causationId != null) {
      message.ApplicationProperties["CausationId"] = causationId.Value.Value.ToString();
    }

    if (destination.Metadata != null) {
      foreach (var (key, value) in destination.Metadata) {
        message.ApplicationProperties[key] = _convertJsonElementToAmqpValue(value);
      }
    }

    return message;
  }

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(handler);
    ArgumentNullException.ThrowIfNull(destination);
    return _subscribeCoreAsync(handler, destination, cancellationToken);
  }

  private async Task<ISubscription> _subscribeCoreAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken
  ) {
    try {
      var topicName = destination.Address;

      // FIXED: Derive subscription name from SubscriberName metadata, NOT from RoutingKey
      // The Core layer sets RoutingKey="#" for "subscribe to all" which is invalid for ASB
      var subscriptionName = _deriveSubscriptionName(destination, topicName);

      // Ensure infrastructure exists when auto-provisioning is enabled
      await _ensureInfrastructureExistsAsync(topicName, subscriptionName, cancellationToken);

      // Apply routing and correlation filters from metadata
      await _applySubscriptionFiltersAsync(destination, topicName, subscriptionName, cancellationToken);

      // Create processor for the topic/subscription
      var processorOptions = new ServiceBusProcessorOptions {
        MaxConcurrentCalls = _options.MaxConcurrentCalls,
        AutoCompleteMessages = false, // We'll complete manually after successful handling
        MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration
      };

      var processor = _client.CreateProcessor(
        topicName,
        subscriptionName,
        processorOptions
      );

      var subscription = new AzureServiceBusSubscription(processor, _logger);

      // Configure message handler
      processor.ProcessMessageAsync += async args => {
        if (!subscription.IsActive) {
          _logger.LogWarning(
            "ABANDON reason: Subscription paused - requeueing message {MessageId} from {TopicName}/{SubscriptionName}",
            args.Message.MessageId,
            destination.Address,
            destination.RoutingKey ?? _options.DefaultSubscriptionName
          );
          await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
          return;
        }

        await _processReceivedMessageAsync(args, handler, destination);
      };

      // Configure error handler
      processor.ProcessErrorAsync += async args => {
        await _handleProcessorErrorAsync(args, destination);
      };

      // Start processing
      await processor.StartProcessingAsync(cancellationToken);

      if (_logger.IsEnabled(LogLevel.Information)) {
        var topic = destination.Address;
        var sub = destination.RoutingKey ?? _options.DefaultSubscriptionName;
        _logger.LogInformation(
          "Started subscription to {TopicName}/{SubscriptionName}",
          topic,
          sub
        );
      }

      return subscription;
#pragma warning disable S2139 // Intentional log-and-rethrow: subscription failures cross async/worker boundaries where the original exception context may be lost.
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Failed to create subscription to {TopicName}/{SubscriptionName}",
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName
      );
      throw;
#pragma warning restore S2139
    }
  }

  private async Task _processReceivedMessageAsync(
    ProcessMessageEventArgs args,
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination
      ) {
    try {
      var (envelope, envelopeTypeName) = await _deserializeReceivedMessageAsync(args, destination);
      if (envelope is null) {
        return; // Message was dead-lettered by _deserializeReceivedMessageAsync
      }

      await handler(envelope, envelopeTypeName, args.CancellationToken);
      await args.CompleteMessageAsync(args.Message, cancellationToken: args.CancellationToken);

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "Processed message {MessageId} from {TopicName}/{SubscriptionName}",
          args.Message.MessageId,
          destination.Address,
          destination.RoutingKey ?? _options.DefaultSubscriptionName
        );
      }
    } catch (Exception ex) {
      await _handleMessageProcessingErrorAsync(args, ex, destination);
    }
  }

  private async Task<(IMessageEnvelope? Envelope, string? EnvelopeTypeName)> _deserializeReceivedMessageAsync(
    ProcessMessageEventArgs args,
    TransportDestination destination
  ) {
    // Get envelope type from message metadata
    if (!args.Message.ApplicationProperties.TryGetValue("EnvelopeType", out var envelopeTypeObj) ||
        envelopeTypeObj is not string envelopeTypeName) {
      _logger.LogWarning(
        "DEAD-LETTER reason: Missing EnvelopeType metadata for message {MessageId} from {TopicName}/{SubscriptionName}",
        args.Message.MessageId,
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName
      );
      await args.DeadLetterMessageAsync(
        args.Message,
        "MissingEnvelopeType",
        "Message does not contain EnvelopeType metadata",
        cancellationToken: args.CancellationToken
      );
      return (null, null);
    }

    var json = args.Message.Body.ToString();

    if (_logger.IsEnabled(LogLevel.Debug)) {
      var jsonPreview = json.Length > 500 ? json[..500] + "..." : json;
      _logger.LogDebug(
        "DIAGNOSTIC [Subscribe]: Received message. ServiceBusMessageId={ServiceBusMessageId}, JSON preview: {JsonPreview}",
        args.Message.MessageId,
        jsonPreview
      );
    }

    var typeInfo = Whizbang.Core.Serialization.JsonContextRegistry.GetTypeInfoByName(envelopeTypeName, _jsonOptions);
    if (typeInfo == null) {
      _logger.LogWarning(
        "DEAD-LETTER reason: No JsonTypeInfo found for envelope type {EnvelopeType} - message {MessageId} from {TopicName}/{SubscriptionName}",
        envelopeTypeName,
        args.Message.MessageId,
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName
      );
      await args.DeadLetterMessageAsync(
        args.Message,
        "MissingJsonTypeInfo",
        $"No JsonTypeInfo found for envelope type: {envelopeTypeName}",
        cancellationToken: args.CancellationToken
      );
      return (null, envelopeTypeName);
    }

    if (JsonSerializer.Deserialize(json, typeInfo) is not IMessageEnvelope envelope) {
      _logger.LogWarning(
        "DEAD-LETTER reason: Deserialization failed for message {MessageId} as {EnvelopeType} from {TopicName}/{SubscriptionName}",
        args.Message.MessageId,
        envelopeTypeName,
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName
      );
      await args.DeadLetterMessageAsync(
        args.Message,
        "DeserializationFailed",
        "Could not deserialize message envelope",
        cancellationToken: args.CancellationToken
      );
      return (null, envelopeTypeName);
    }

    if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug(
        "DIAGNOSTIC [Subscribe]: Deserialized envelope. MessageId={MessageId}",
        envelope.MessageId.Value
      );
    }

    return (envelope, envelopeTypeName);
  }

  private async Task _handleMessageProcessingErrorAsync(
    ProcessMessageEventArgs args,
    Exception ex,
    TransportDestination destination
  ) {
    _logger.LogError(
      ex,
      "Error processing message {MessageId} from {TopicName}/{SubscriptionName}",
      args.Message.MessageId,
      destination.Address,
      destination.RoutingKey ?? _options.DefaultSubscriptionName
    );

    var deliveryCount = args.Message.DeliveryCount;
    if (deliveryCount >= _options.MaxDeliveryAttempts) {
      _logger.LogWarning(
        "DEAD-LETTER reason: Handler exception after max delivery attempts ({DeliveryCount}/{MaxAttempts}) for message {MessageId} from {TopicName}/{SubscriptionName}. Exception: {ExceptionType}: {ExceptionMessage}",
        deliveryCount,
        _options.MaxDeliveryAttempts,
        args.Message.MessageId,
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName,
        ex.GetType().Name,
        ex.Message
      );
      await args.DeadLetterMessageAsync(
        args.Message,
        "MaxDeliveryAttemptsExceeded",
        ex.Message,
        cancellationToken: args.CancellationToken
      );
    } else {
      _logger.LogWarning(
        "ABANDON reason: Handler exception (attempt {DeliveryCount}/{MaxAttempts}) for message {MessageId} from {TopicName}/{SubscriptionName} - requeueing for retry. Exception: {ExceptionType}: {ExceptionMessage}",
        deliveryCount,
        _options.MaxDeliveryAttempts,
        args.Message.MessageId,
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName,
        ex.GetType().Name,
        ex.Message
      );
      await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
    }
  }

  private async Task _handleProcessorErrorAsync(
    ProcessErrorEventArgs args,
    TransportDestination destination
  ) {
    _logger.LogError(
      args.Exception,
      "Error in Service Bus processor for {TopicName}/{SubscriptionName}: {ErrorSource}",
      destination.Address,
      destination.RoutingKey ?? _options.DefaultSubscriptionName,
      args.ErrorSource
    );

    if (_isConnectionError(args.Exception)) {
      _logger.LogWarning(
        "Detected connection-level error in Service Bus processor, triggering recovery: {ErrorReason}",
        (args.Exception as ServiceBusException)?.Reason
      );
      await _invokeRecoveryHandlerAsync();
    }
  }

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public async Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) where TRequest : notnull where TResponse : notnull {
    ObjectDisposedException.ThrowIf(_disposed, this);

    // Azure Service Bus doesn't natively support request/response pattern
    // This would typically be implemented using sessions or request/response store
    // For now, throw not supported
    throw new NotSupportedException(
      "Request/response pattern is not supported by Azure Service Bus transport. " +
      "Use IRequestResponseStore with publish/subscribe instead."
    );
  }

  private async Task _applySubscriptionFiltersAsync(
    TransportDestination destination,
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken
  ) {
    // DIAGNOSTIC: Log metadata to trace RoutingPatterns flow
    _logger.LogWarning(
      "DIAGNOSTIC [SubscribeAsync]: Topic={TopicName}, Subscription={SubscriptionName}, MetadataNull={MetadataNull}, MetadataKeys=[{MetadataKeys}]",
      topicName,
      subscriptionName,
      destination.Metadata == null,
      destination.Metadata != null ? string.Join(", ", destination.Metadata.Keys) : "N/A");

    // Apply routing pattern filter if RoutingPatterns metadata exists
    await _applyRoutingPatternsFromMetadataAsync(destination, topicName, subscriptionName, cancellationToken);

    // Apply CorrelationFilter if specified in metadata (production without Aspire)
    await _applyCorrelationFilterFromMetadataAsync(destination, topicName, subscriptionName, cancellationToken);
  }

  private async Task _applyRoutingPatternsFromMetadataAsync(
    TransportDestination destination,
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken
  ) {
    if (destination.Metadata?.TryGetValue("RoutingPatterns", out var routingPatternsElement) != true) {
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "[Subscribe] RoutingPatterns not found in metadata for {TopicName}/{SubscriptionName}",
          topicName,
          subscriptionName);
      }
      return;
    }

    if (routingPatternsElement.ValueKind != JsonValueKind.Array) {
      return;
    }

    var patterns = new List<string>();
    foreach (var pattern in routingPatternsElement.EnumerateArray()) {
      if (pattern.ValueKind == JsonValueKind.String) {
        var patternStr = pattern.GetString();
        if (!string.IsNullOrWhiteSpace(patternStr)) {
          patterns.Add(patternStr);
        }
      }
    }
    if (patterns.Count > 0) {
      await _applyRoutingPatternFilterAsync(topicName, subscriptionName, patterns, cancellationToken);
    }
  }

  private async Task _applyCorrelationFilterFromMetadataAsync(
    TransportDestination destination,
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken
  ) {
    // Skip if emulator (filters provisioned by Aspire AppHost)
    if (_isEmulator) {
      return;
    }

    if (destination.Metadata?.TryGetValue("DestinationFilter", out var destinationFilterElem) != true ||
        destinationFilterElem.ValueKind != JsonValueKind.String) {
      return;
    }

    var destinationFilter = destinationFilterElem.GetString();
    if (string.IsNullOrWhiteSpace(destinationFilter)) {
      return;
    }

    if (_adminClient != null) {
      try {
        await _applyCorrelationFilterAsync(topicName, subscriptionName, destinationFilter, cancellationToken);
      } catch (Exception ex) {
        _logger.LogWarning(
          ex,
          "DestinationFilter '{DestinationFilter}' for {TopicName}/{SubscriptionName}: failed to apply, proceeding without filter",
          destinationFilter,
          topicName,
          subscriptionName
        );
      }
    } else if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug(
        "DestinationFilter '{DestinationFilter}' specified for {TopicName}/{SubscriptionName} but administration client is not available",
        destinationFilter,
        topicName,
        subscriptionName
      );
    }
  }

  /// <summary>
  /// Applies a CorrelationFilter to a subscription by replacing the default rule.
  /// Filters messages based on the Destination application property.
  /// </summary>
  private async Task _applyCorrelationFilterAsync(
    string topicName,
    string subscriptionName,
    string destination,
    CancellationToken cancellationToken
  ) {
    using var activity = WhizbangActivitySource.Hosting.StartActivity("ApplyCorrelationFilter");
    activity?.SetTag("servicebus.topic", topicName);
    activity?.SetTag("servicebus.subscription", subscriptionName);
    activity?.SetTag("servicebus.filter_type", "CorrelationRuleFilter");
    activity?.SetTag("servicebus.destination", destination);

    if (_adminClient == null) {
      throw new InvalidOperationException("Administration client is not available");
    }

    const string defaultRuleName = "$Default";
    const string customRuleName = "DestinationFilter";

    try {
      // Check if subscription exists
      var subscriptionExists = await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken);
      if (!subscriptionExists) {
        activity?.SetTag("servicebus.subscription_exists", false);
        _logger.LogWarning(
          "Subscription {TopicName}/{SubscriptionName} does not exist. Cannot apply CorrelationFilter.",
          topicName,
          subscriptionName
        );
        return;
      }
      activity?.SetTag("servicebus.subscription_exists", true);

      // Remove default rule if it exists
      var rules = _adminClient.GetRulesAsync(topicName, subscriptionName, cancellationToken);
      var deletedRules = 0;
      // S3267: Loop contains await — LINQ doesn't support async lambdas
#pragma warning disable S3267
      await foreach (var rule in rules) {
        if (rule.Name == defaultRuleName || rule.Name == customRuleName) {
          await _adminClient.DeleteRuleAsync(topicName, subscriptionName, rule.Name, cancellationToken);
          deletedRules++;
          if (_logger.IsEnabled(LogLevel.Debug)) {
            var ruleName = rule.Name;
            var topic = topicName;
            var subscription = subscriptionName;
            _logger.LogDebug(
              "Deleted rule '{RuleName}' from {TopicName}/{SubscriptionName}",
              ruleName,
              topic,
              subscription
            );
          }
        }
      }
#pragma warning restore S3267
      activity?.SetTag("servicebus.rules_deleted", deletedRules);

      // Create new rule with CorrelationFilter on Destination application property
      var ruleOptions = new CreateRuleOptions {
        Name = customRuleName,
        Filter = new CorrelationRuleFilter {
          ApplicationProperties = { ["Destination"] = destination }
        }
      };

      await _adminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions, cancellationToken);
      activity?.SetTag("servicebus.rule_created", true);

      if (_logger.IsEnabled(LogLevel.Information)) {
        var dest = destination;
        var topic = topicName;
        var subscription = subscriptionName;
        _logger.LogInformation(
          "Applied CorrelationFilter for Destination='{Destination}' to {TopicName}/{SubscriptionName}",
          dest,
          topic,
          subscription
        );
      }
#pragma warning disable S2139 // Intentional log-and-rethrow: infrastructure provisioning errors cross async/DI boundaries where the original exception context may be lost.
    } catch (Exception ex) {
      activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      _logger.LogError(
        ex,
        "Error applying CorrelationFilter for Destination='{Destination}' to {TopicName}/{SubscriptionName}",
        destination,
        topicName,
        subscriptionName
      );
      throw;
#pragma warning restore S2139
    }
  }

  #region Subscription Name Derivation

  /// <summary>
  /// Derives subscription name from SubscriberName metadata, NOT RoutingKey.
  /// The Core layer sets RoutingKey for routing patterns (e.g., "#" for all messages),
  /// which are invalid for Azure Service Bus subscription names.
  /// </summary>
  /// <param name="destination">The transport destination containing metadata.</param>
  /// <param name="topicName">The topic name being subscribed to.</param>
  /// <returns>A valid Azure Service Bus subscription name.</returns>
  /// <docs>messaging/transports/azure-service-bus#subscription-naming</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/SubscriptionNameDerivationTests.cs</tests>
  private string _deriveSubscriptionName(TransportDestination destination, string topicName) {
    // Try to get SubscriberName from metadata (set by TransportSubscriptionBuilder)
    if (destination.Metadata?.TryGetValue("SubscriberName", out var elem) == true &&
        elem.ValueKind == JsonValueKind.String) {
      var subscriberName = elem.GetString();
      if (!string.IsNullOrWhiteSpace(subscriberName)) {
        var derivedName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(subscriberName, topicName);
        if (_logger.IsEnabled(LogLevel.Debug)) {
          _logger.LogDebug(
            "Derived subscription name '{SubscriptionName}' from SubscriberName metadata '{SubscriberName}' for topic '{TopicName}'",
            derivedName,
            subscriberName,
            topicName
          );
        }
        return derivedName;
      }
    }

    // Fallback - use routing key if it's a valid subscription name (no wildcards)
    var routingKey = destination.RoutingKey;
    if (!string.IsNullOrWhiteSpace(routingKey) && !_isWildcardPattern(routingKey)) {
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "Using RoutingKey '{RoutingKey}' as subscription name for topic '{TopicName}'",
          routingKey,
          topicName
        );
      }
      return routingKey;
    }

    // Final fallback - use default subscription name
    if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug(
        "Using default subscription name '{DefaultName}' for topic '{TopicName}' (RoutingKey '{RoutingKey}' is wildcard or empty)",
        _options.DefaultSubscriptionName,
        topicName,
        routingKey ?? "(null)"
      );
    }
    return _options.DefaultSubscriptionName;
  }

  /// <summary>
  /// Determines if a routing key contains wildcard patterns that are invalid for subscription names.
  /// </summary>
  /// <param name="routingKey">The routing key to check.</param>
  /// <returns>True if the routing key contains wildcard characters.</returns>
  private static bool _isWildcardPattern(string routingKey) =>
    routingKey.Contains('#') || routingKey.Contains('*') || routingKey.Contains(',');

  #endregion

  #region Infrastructure Provisioning

  /// <summary>
  /// Ensures topic and subscription exist when AutoProvisionInfrastructure is enabled.
  /// Handles race conditions gracefully by ignoring 409 Conflict errors.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>messaging/transports/azure-service-bus#auto-provisioning</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs</tests>
  private async Task _ensureInfrastructureExistsAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken) {

    if (_adminClient == null || !_options.AutoProvisionInfrastructure) {
      return;
    }

    // Ensure topic exists
    await _ensureTopicExistsViaAdminAsync(topicName, cancellationToken);

    // Ensure subscription exists
    try {
      if (!await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken)) {
        if (_logger.IsEnabled(LogLevel.Information)) {
          _logger.LogInformation("Creating subscription {TopicName}/{SubscriptionName}", topicName, subscriptionName);
        }
        await _adminClient.CreateSubscriptionAsync(topicName, subscriptionName, cancellationToken);
      }
    } catch (Azure.RequestFailedException ex) when (ex.Status == 409) {
      // Race condition - subscription created by another instance, safe to ignore
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(ex, "Subscription {TopicName}/{SubscriptionName} already exists (409 conflict)", topicName, subscriptionName);
      }
    }
  }

  /// <summary>
  /// Applies SqlFilter rules for routing pattern matching.
  /// Translates RabbitMQ-style patterns (e.g., "ns.#") to SQL LIKE patterns.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="routingPatterns">The routing patterns to filter by.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>messaging/transports/azure-service-bus#routing-filters</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/SubscriptionNameDerivationTests.cs</tests>
  private async Task _applyRoutingPatternFilterAsync(
    string topicName,
    string subscriptionName,
    IEnumerable<string> routingPatterns,
    CancellationToken cancellationToken) {

    if (_adminClient == null || !_options.AutoProvisionInfrastructure) {
      return;
    }

    // Build SQL filter expression
    // "ns1.#,ns2.#" → "sys.Label LIKE 'ns1.%' OR sys.Label LIKE 'ns2.%'"
    // NOTE: Azure Service Bus SqlFilter uses sys.Label for the Subject/Label property,
    // NOT [Subject]. The [Subject] syntax doesn't work for SqlRuleFilter expressions.
    // See: https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-sql-filter
    var likePatterns = routingPatterns
      .Select(p => p.Replace(".#", ".%").Replace(".*", ".%").Replace("#", "%").Replace("*", "%"))
      .Select(p => $"sys.Label LIKE '{p}'");

    var sqlExpression = string.Join(" OR ", likePatterns);

    const string ruleName = "RoutingPatternFilter";

    try {
      // Delete existing rules (including $Default)
      var deletedRules = new List<string>();
      // S3267: Loop contains await — LINQ doesn't support async lambdas
#pragma warning disable S3267
      await foreach (var rule in _adminClient.GetRulesAsync(topicName, subscriptionName, cancellationToken)) {
        await _adminClient.DeleteRuleAsync(topicName, subscriptionName, rule.Name, cancellationToken);
        deletedRules.Add(rule.Name);
      }
#pragma warning restore S3267

      // Create SqlFilter rule
      var ruleOptions = new CreateRuleOptions(ruleName, new SqlRuleFilter(sqlExpression));
      await _adminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions, cancellationToken);

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "[SqlFilter] Deleted {RuleCount} existing rules and applied SqlFilter '{SqlExpression}' to {TopicName}/{SubscriptionName}",
          deletedRules.Count,
          sqlExpression,
          topicName,
          subscriptionName);
      }
    } catch (Exception ex) {
      _logger.LogWarning(
        ex,
        "Failed to apply routing pattern filter to {TopicName}/{SubscriptionName}. Proceeding without filter.",
        topicName,
        subscriptionName
      );
    }
  }

  #endregion

  /// <summary>
  /// Converts a JsonElement to an AMQP-compatible primitive value.
  /// AMQP application properties only support: string, bool, byte, sbyte, short, ushort,
  /// int, uint, long, ulong, float, double, decimal, Guid, DateTimeOffset, TimeSpan, Uri.
  /// </summary>
  private static object? _convertJsonElementToAmqpValue(JsonElement element) {
    return element.ValueKind switch {
      JsonValueKind.String => element.GetString(),
      JsonValueKind.Number when element.TryGetInt64(out var longVal) => longVal,
      JsonValueKind.Number when element.TryGetDouble(out var doubleVal) => doubleVal,
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      JsonValueKind.Null => null,
      // For arrays and objects, serialize back to JSON string (AMQP doesn't support complex types)
      JsonValueKind.Array or JsonValueKind.Object => element.GetRawText(),
      _ => element.ToString()
    };
  }

  /// <summary>
  /// Ensures a topic exists via the admin client, handling race conditions gracefully.
  /// Shared by both subscribe-path and publish-path auto-provisioning.
  /// </summary>
  /// <docs>messaging/transports/azure-service-bus#publish-auto-provisioning</docs>
  private async Task _ensureTopicExistsViaAdminAsync(string topicName, CancellationToken cancellationToken) {
    if (_adminClient == null || !_options.AutoProvisionInfrastructure) {
      return;
    }

    try {
      if (!await _adminClient.TopicExistsAsync(topicName, cancellationToken)) {
        await _adminClient.CreateTopicAsync(topicName, cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information)) {
          _logger.LogInformation("Auto-created topic '{TopicName}'", topicName);
        }
      }
    } catch (Azure.RequestFailedException ex) when (ex.Status == 409) {
      // Race condition — another instance created it, safe to ignore
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(ex, "Topic '{TopicName}' already exists (race condition)", topicName);
      }
    }
  }

  private async Task<ServiceBusSender> _getOrCreateSenderAsync(string topicName, CancellationToken cancellationToken) {
    if (_senders.TryGetValue(topicName, out var existingSender)) {
      return existingSender;
    }

    await _senderLock.WaitAsync(cancellationToken);
    try {
      // Double-check after acquiring lock
      if (_senders.TryGetValue(topicName, out existingSender)) {
        return existingSender;
      }

      // Ensure topic exists before creating sender (on-demand provisioning)
      // This matches RabbitMQ's idempotent ExchangeDeclareAsync behavior
      await _ensureTopicExistsViaAdminAsync(topicName, cancellationToken);

      var sender = _client.CreateSender(topicName);
      _senders[topicName] = sender;

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("Created sender for topic {TopicName}", topicName);
      }

      return sender;
    } finally {
      _senderLock.Release();
    }
  }

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    // Clear recovery handler to prevent memory leak
    _recoveryHandler = null;

    // Dispose all senders
    foreach (var sender in _senders.Values) {
      await sender.DisposeAsync();
    }
    _senders.Clear();

    // DON'T dispose _client - it's injected and managed by DI container
    _logger.LogInformation("Transport disposed (client managed by DI)");

    _senderLock.Dispose();

    GC.SuppressFinalize(this);
  }
}
