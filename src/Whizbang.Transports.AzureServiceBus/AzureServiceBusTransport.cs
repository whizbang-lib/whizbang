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
    TransportCapabilities.Ordered;

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public async Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);

    try {
      _logger.LogWarning("DIAGNOSTIC [PublishAsync]: About to get sender for {Destination}", destination.Address);
      var sender = await _getOrCreateSenderAsync(destination.Address, cancellationToken);
      _logger.LogWarning("DIAGNOSTIC [PublishAsync]: Got sender for {Destination}", destination.Address);

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

      // DIAGNOSTIC: Log the Subject being set (WARNING level to always show)
      _logger.LogWarning(
        "DIAGNOSTIC [PublishAsync]: Setting Subject={Subject} on message {MessageId} to topic {TopicName} (RoutingKey={RoutingKey})",
        message.Subject,
        envelope.MessageId,
        destination.Address,
        destination.RoutingKey ?? "(null)");

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

      _logger.LogWarning("DIAGNOSTIC [PublishAsync]: About to send message {MessageId} to {Destination}", envelope.MessageId, destination.Address);

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
      _logger.LogWarning("DIAGNOSTIC [PublishAsync]: Message sent successfully {MessageId}", envelope.MessageId);

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
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Failed to publish message {MessageId} to {Destination}",
        envelope.MessageId,
        destination.Address
      );
      throw;
    }
  }

  /// <inheritdoc />
  /// <tests>No tests found</tests>
  public async Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(handler);
    ArgumentNullException.ThrowIfNull(destination);

    try {
      var topicName = destination.Address;

      // FIXED: Derive subscription name from SubscriberName metadata, NOT from RoutingKey
      // The Core layer sets RoutingKey="#" for "subscribe to all" which is invalid for ASB
      var subscriptionName = _deriveSubscriptionName(destination, topicName);

      // Ensure infrastructure exists when auto-provisioning is enabled
      await _ensureInfrastructureExistsAsync(topicName, subscriptionName, cancellationToken);

      // DIAGNOSTIC: Log metadata to trace RoutingPatterns flow
      _logger.LogWarning(
        "DIAGNOSTIC [SubscribeAsync]: Topic={TopicName}, Subscription={SubscriptionName}, MetadataNull={MetadataNull}, MetadataKeys=[{MetadataKeys}]",
        topicName,
        subscriptionName,
        destination.Metadata == null,
        destination.Metadata != null ? string.Join(", ", destination.Metadata.Keys) : "N/A");

      if (destination.Metadata?.TryGetValue("RoutingPatterns", out var routingPatternsElement) == true) {
        _logger.LogWarning(
          "DIAGNOSTIC [SubscribeAsync]: Found RoutingPatterns! ValueKind={ValueKind}, RawText={RawText}",
          routingPatternsElement.ValueKind,
          routingPatternsElement.GetRawText());
      } else {
        _logger.LogWarning(
          "DIAGNOSTIC [SubscribeAsync]: RoutingPatterns NOT FOUND in metadata for {TopicName}/{SubscriptionName}",
          topicName,
          subscriptionName);
      }

      // Apply routing pattern filter if RoutingPatterns metadata exists (inbox pattern)
      if (destination.Metadata?.TryGetValue("RoutingPatterns", out var patternsElem) == true &&
          patternsElem.ValueKind == JsonValueKind.Array) {
        var patterns = new List<string>();
        foreach (var pattern in patternsElem.EnumerateArray()) {
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

      // Apply CorrelationFilter if specified in metadata (production without Aspire)
      // Skip if emulator (filters provisioned by Aspire AppHost)
      if (!_isEmulator &&
          destination.Metadata?.TryGetValue("DestinationFilter", out var destinationFilterElem) == true &&
          destinationFilterElem.ValueKind == JsonValueKind.String) {

        var destinationFilter = destinationFilterElem.GetString();
        if (!string.IsNullOrWhiteSpace(destinationFilter)) {

          if (_adminClient != null) {
            try {
              await _applyCorrelationFilterAsync(topicName, subscriptionName, destinationFilter, cancellationToken);
            } catch (Exception ex) {
              _logger.LogWarning(
                ex,
                "Failed to apply CorrelationFilter '{DestinationFilter}' to {TopicName}/{SubscriptionName}. Proceeding without filter.",
                destinationFilter,
                topicName,
                subscriptionName
              );
            }
          } else {
            _logger.LogWarning(
              "DestinationFilter '{DestinationFilter}' specified for {TopicName}/{SubscriptionName} but administration client is not available",
              destinationFilter,
              topicName,
              subscriptionName
            );
          }
        }
      }

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
        Console.WriteLine($"[TRANSPORT DIAGNOSTIC] ProcessMessageAsync invoked! MessageId={args.Message.MessageId}, IsActive={subscription.IsActive}");

        if (!subscription.IsActive) {
          Console.WriteLine("[TRANSPORT DIAGNOSTIC] Subscription NOT active - abandoning message");
          _logger.LogWarning(
            "ABANDON reason: Subscription paused - requeueing message {MessageId} from {TopicName}/{SubscriptionName}",
            args.Message.MessageId,
            destination.Address,
            destination.RoutingKey ?? _options.DefaultSubscriptionName
          );
          // If paused, abandon the message so it can be reprocessed
          await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
          return;
        }

        try {
          // Get envelope type from message metadata
          if (!args.Message.ApplicationProperties.TryGetValue("EnvelopeType", out var envelopeTypeObj) ||
              envelopeTypeObj is not string envelopeTypeName) {
            Console.WriteLine($"[TRANSPORT DIAGNOSTIC] Missing EnvelopeType metadata! MessageId={args.Message.MessageId}");
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
            return;
          }
          Console.WriteLine($"[TRANSPORT DIAGNOSTIC] EnvelopeType={envelopeTypeName}");

          // Deserialize envelope using AOT-compatible JsonContextRegistry
          // Use JsonContextRegistry.GetTypeInfoByName() instead of Type.GetType() to support
          // cross-assembly generic types like MessageEnvelope<TEvent> where TEvent is from a different assembly
          var json = args.Message.Body.ToString();

          // DIAGNOSTIC: Log the JSON and Service Bus MessageId before deserializing
          if (_logger.IsEnabled(LogLevel.Debug)) {
            var serviceBusMessageId = args.Message.MessageId;
            var jsonPreview = json.Length > 500 ? json[..500] + "..." : json;
            _logger.LogDebug(
              "DIAGNOSTIC [Subscribe]: Received message. ServiceBusMessageId={ServiceBusMessageId}, JSON preview: {JsonPreview}",
              serviceBusMessageId,
              jsonPreview
            );
          }

          // Resolve JsonTypeInfo for the envelope type using JsonContextRegistry
          // This supports fuzzy matching and cross-assembly type resolution
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
            return;
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
            return;
          }

          // DIAGNOSTIC: Log the deserialized MessageId to see if it survived
          if (_logger.IsEnabled(LogLevel.Debug)) {
            var messageId = envelope.MessageId.Value;
            _logger.LogDebug(
              "DIAGNOSTIC [Subscribe]: Deserialized envelope. MessageId={MessageId}",
              messageId
            );
          }

          // Invoke handler with envelope type metadata
          Console.WriteLine($"[TRANSPORT DIAGNOSTIC] Invoking handler for MessageId={envelope.MessageId.Value}");
          await handler(envelope, envelopeTypeName, args.CancellationToken);
          Console.WriteLine($"[TRANSPORT DIAGNOSTIC] Handler completed, completing message MessageId={envelope.MessageId.Value}");

          // Complete the message
          await args.CompleteMessageAsync(args.Message, cancellationToken: args.CancellationToken);
          Console.WriteLine($"[TRANSPORT DIAGNOSTIC] Message completed MessageId={envelope.MessageId.Value}");

          if (_logger.IsEnabled(LogLevel.Debug)) {
            var messageId = args.Message.MessageId;
            var topicName = destination.Address;
            var subscriptionName = destination.RoutingKey ?? _options.DefaultSubscriptionName;
            _logger.LogDebug(
              "Processed message {MessageId} from {TopicName}/{SubscriptionName}",
              messageId,
              topicName,
              subscriptionName
            );
          }
        } catch (Exception ex) {
          _logger.LogError(
            ex,
            "Error processing message {MessageId} from {TopicName}/{SubscriptionName}",
            args.Message.MessageId,
            destination.Address,
            destination.RoutingKey ?? _options.DefaultSubscriptionName
          );

          // Check retry count
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
            // Abandon to retry
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
          }
        }
      };

      // Configure error handler
      processor.ProcessErrorAsync += async args => {
        Console.WriteLine($"[TRANSPORT DIAGNOSTIC] ProcessErrorAsync invoked! ErrorSource={args.ErrorSource}, Exception={args.Exception.Message}");
        _logger.LogError(
          args.Exception,
          "Error in Service Bus processor for {TopicName}/{SubscriptionName}: {ErrorSource}",
          destination.Address,
          destination.RoutingKey ?? _options.DefaultSubscriptionName,
          args.ErrorSource
        );

        // If this is a connection-level error, trigger recovery handler
        if (_isConnectionError(args.Exception)) {
          _logger.LogWarning(
            "Detected connection-level error in Service Bus processor, triggering recovery: {ErrorReason}",
            (args.Exception as ServiceBusException)?.Reason
          );
          await _invokeRecoveryHandlerAsync();
        }
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
    } catch (Exception ex) {
      _logger.LogError(
        ex,
        "Failed to create subscription to {TopicName}/{SubscriptionName}",
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName
      );
      throw;
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
  /// <docs>components/transports/azure-service-bus#subscription-naming</docs>
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
  /// <docs>components/transports/azure-service-bus#auto-provisioning</docs>
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
        _logger.LogDebug("Subscription {TopicName}/{SubscriptionName} already exists (409 conflict)", topicName, subscriptionName);
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
  /// <docs>components/transports/azure-service-bus#routing-filters</docs>
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
      await foreach (var rule in _adminClient.GetRulesAsync(topicName, subscriptionName, cancellationToken)) {
        await _adminClient.DeleteRuleAsync(topicName, subscriptionName, rule.Name, cancellationToken);
        deletedRules.Add(rule.Name);
      }

      // Log deleted rules at WARNING level for diagnostic visibility
      _logger.LogWarning(
        "DIAGNOSTIC [SqlFilter]: Deleted {RuleCount} existing rules from {TopicName}/{SubscriptionName}: [{DeletedRules}]",
        deletedRules.Count,
        topicName,
        subscriptionName,
        string.Join(", ", deletedRules));

      // Create SqlFilter rule
      var ruleOptions = new CreateRuleOptions(ruleName, new SqlRuleFilter(sqlExpression));
      await _adminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions, cancellationToken);

      // Log at WARNING level for diagnostic visibility
      _logger.LogWarning(
        "DIAGNOSTIC [SqlFilter]: Applied SqlFilter '{SqlExpression}' to {TopicName}/{SubscriptionName}",
        sqlExpression,
        topicName,
        subscriptionName
      );
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
  /// <docs>transports/azure-service-bus#publish-auto-provisioning</docs>
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
        _logger.LogDebug("Topic '{TopicName}' already exists (race condition)", topicName);
      }
    }
  }

  private async Task<ServiceBusSender> _getOrCreateSenderAsync(string topicName, CancellationToken cancellationToken) {
    if (_senders.TryGetValue(topicName, out var existingSender)) {
      _logger.LogWarning("DIAGNOSTIC [GetOrCreateSender]: Using existing sender for {TopicName}", topicName);
      return existingSender;
    }

    _logger.LogWarning("DIAGNOSTIC [GetOrCreateSender]: Waiting for semaphore for {TopicName}", topicName);
    await _senderLock.WaitAsync(cancellationToken);
    _logger.LogWarning("DIAGNOSTIC [GetOrCreateSender]: Acquired semaphore for {TopicName}", topicName);
    try {
      // Double-check after acquiring lock
      if (_senders.TryGetValue(topicName, out existingSender)) {
        _logger.LogWarning("DIAGNOSTIC [GetOrCreateSender]: Found existing sender after lock for {TopicName}", topicName);
        return existingSender;
      }

      // Ensure topic exists before creating sender (on-demand provisioning)
      // This matches RabbitMQ's idempotent ExchangeDeclareAsync behavior
      await _ensureTopicExistsViaAdminAsync(topicName, cancellationToken);

      _logger.LogWarning("DIAGNOSTIC [GetOrCreateSender]: Creating sender for {TopicName}", topicName);
      var sender = _client.CreateSender(topicName);
      _logger.LogWarning("DIAGNOSTIC [GetOrCreateSender]: Sender created, adding to dictionary for {TopicName}", topicName);
      _senders[topicName] = sender;

      if (_logger.IsEnabled(LogLevel.Debug)) {
        var topic = topicName;
        _logger.LogDebug("Created sender for topic {TopicName}", topic);
      }

      return sender;
    } finally {
      _logger.LogWarning("DIAGNOSTIC [GetOrCreateSender]: Releasing semaphore for {TopicName}", topicName);
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
