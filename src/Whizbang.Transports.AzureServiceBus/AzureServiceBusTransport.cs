using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public class AzureServiceBusTransport : ITransport, IAsyncDisposable {
  private readonly ServiceBusClient _client;
  private readonly ServiceBusAdministrationClient? _adminClient;
  private readonly ILogger<AzureServiceBusTransport> _logger;
  private readonly Dictionary<string, ServiceBusSender> _senders = [];
  private readonly SemaphoreSlim _senderLock = new(1, 1);
  private readonly AzureServiceBusOptions _options;
  private readonly JsonSerializerContext _jsonContext;
  private readonly bool _isEmulator;
  private bool _disposed;

  public AzureServiceBusTransport(
    string connectionString,
    JsonSerializerContext jsonContext,
    AzureServiceBusOptions? options = null,
    ILogger<AzureServiceBusTransport>? logger = null
  ) {
    using var activity = WhizbangActivitySource.Transport.StartActivity("AzureServiceBusTransport.Initialize");

    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    ArgumentNullException.ThrowIfNull(jsonContext);

    // Detect if running against emulator (localhost/127.0.0.1)
    _isEmulator = connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                  connectionString.Contains("127.0.0.1");

    _client = new ServiceBusClient(connectionString);
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureServiceBusTransport>.Instance;

    // Create administration client for managing subscription rules (CorrelationFilter)
    // Skip for emulator as it doesn't support the Admin API (REST on port 443)
    if (!_isEmulator) {
      try {
        _adminClient = new ServiceBusAdministrationClient(connectionString);
      } catch (Exception ex) {
        _logger.LogWarning(ex, "Failed to create ServiceBusAdministrationClient. Filter provisioning will not be available.");
        _adminClient = null;
      }
    } else {
      _logger.LogInformation("Emulator detected. Administration client disabled. Filters must be provisioned by Aspire AppHost.");
      _adminClient = null;
    }

    _jsonContext = jsonContext;
    _options = options ?? new AzureServiceBusOptions();

    // Add OTEL tags for observability
    activity?.SetTag("transport.type", "AzureServiceBus");
    activity?.SetTag("transport.emulator", _isEmulator);
    activity?.SetTag("transport.admin_client_available", _adminClient != null);
  }

  /// <inheritdoc />
  public TransportCapabilities Capabilities =>
    TransportCapabilities.PublishSubscribe |
    TransportCapabilities.Reliable |
    TransportCapabilities.Ordered;

  /// <inheritdoc />
  public async Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);

    try {
      var sender = await GetOrCreateSenderAsync(destination.Address, cancellationToken);

      // Get the envelope type to store as metadata for deserialization
      var envelopeType = envelope.GetType();
      var envelopeTypeName = envelopeType.AssemblyQualifiedName
        ?? throw new InvalidOperationException("Envelope type must have an assembly qualified name");

      // Serialize envelope to JSON using AOT-compatible context
      var typeInfo = _jsonContext.GetTypeInfo(envelopeType)
        ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeType.Name}. Ensure the message type is registered in WhizbangJsonContext.");
      var json = JsonSerializer.Serialize(envelope, typeInfo);
      var message = new ServiceBusMessage(json) {
        MessageId = envelope.MessageId.Value.ToString(),
        Subject = destination.RoutingKey ?? "message",
        ContentType = "application/json"
      };

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

      // Add custom metadata
      if (destination.Metadata != null) {
        foreach (var (key, value) in destination.Metadata) {
          message.ApplicationProperties[key] = value;
        }
      }

      await sender.SendMessageAsync(message, cancellationToken);

      _logger.LogDebug(
        "Published message {MessageId} to topic {TopicName} with subject {Subject}",
        envelope.MessageId,
        destination.Address,
        message.Subject
      );
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
  public async Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(handler);
    ArgumentNullException.ThrowIfNull(destination);

    try {
      var topicName = destination.Address;
      var subscriptionName = destination.RoutingKey ?? _options.DefaultSubscriptionName;

      // Apply CorrelationFilter if specified in metadata (production without Aspire)
      // Skip if emulator (filters provisioned by Aspire AppHost)
      if (!_isEmulator &&
          destination.Metadata?.TryGetValue("DestinationFilter", out var destinationFilterObj) == true &&
          destinationFilterObj is string destinationFilter &&
          !string.IsNullOrWhiteSpace(destinationFilter)) {

        if (_adminClient != null) {
          try {
            await ApplyCorrelationFilterAsync(topicName, subscriptionName, destinationFilter, cancellationToken);
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
          // If paused, abandon the message so it can be reprocessed
          await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
          return;
        }

        try {
          // Get envelope type from message metadata
          if (!args.Message.ApplicationProperties.TryGetValue("EnvelopeType", out var envelopeTypeObj) ||
              envelopeTypeObj is not string envelopeTypeName) {
            _logger.LogError("Message {MessageId} missing EnvelopeType metadata", args.Message.MessageId);
            await args.DeadLetterMessageAsync(
              args.Message,
              "MissingEnvelopeType",
              "Message does not contain EnvelopeType metadata",
              cancellationToken: args.CancellationToken
            );
            return;
          }

          // Resolve the envelope type
          var envelopeType = Type.GetType(envelopeTypeName);
          if (envelopeType == null) {
            _logger.LogError("Could not resolve envelope type {EnvelopeType}", envelopeTypeName);
            await args.DeadLetterMessageAsync(
              args.Message,
              "UnresolvableEnvelopeType",
              $"Could not resolve envelope type: {envelopeTypeName}",
              cancellationToken: args.CancellationToken
            );
            return;
          }

          // Deserialize envelope using the resolved type and AOT-compatible context
          var json = args.Message.Body.ToString();
          var typeInfo = _jsonContext.GetTypeInfo(envelopeType);
          if (typeInfo == null) {
            _logger.LogError("No JsonTypeInfo found for {EnvelopeType}", envelopeTypeName);
            await args.DeadLetterMessageAsync(
              args.Message,
              "MissingJsonTypeInfo",
              $"No JsonTypeInfo found for envelope type: {envelopeTypeName}",
              cancellationToken: args.CancellationToken
            );
            return;
          }

          if (JsonSerializer.Deserialize(json, typeInfo) is not IMessageEnvelope envelope) {
            _logger.LogError("Failed to deserialize message {MessageId} as {EnvelopeType}",
              args.Message.MessageId, envelopeTypeName);
            await args.DeadLetterMessageAsync(
              args.Message,
              "DeserializationFailed",
              "Could not deserialize message envelope",
              cancellationToken: args.CancellationToken
            );
            return;
          }

          // Invoke handler
          await handler(envelope, args.CancellationToken);

          // Complete the message
          await args.CompleteMessageAsync(args.Message, cancellationToken: args.CancellationToken);

          _logger.LogDebug(
            "Processed message {MessageId} from {TopicName}/{SubscriptionName}",
            args.Message.MessageId,
            destination.Address,
            destination.RoutingKey ?? _options.DefaultSubscriptionName
          );
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
              "Message {MessageId} exceeded max delivery attempts ({MaxAttempts}), dead-lettering",
              args.Message.MessageId,
              _options.MaxDeliveryAttempts
            );
            await args.DeadLetterMessageAsync(
              args.Message,
              "MaxDeliveryAttemptsExceeded",
              ex.Message,
              cancellationToken: args.CancellationToken
            );
          } else {
            // Abandon to retry
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
          }
        }
      };

      // Configure error handler
      processor.ProcessErrorAsync += args => {
        _logger.LogError(
          args.Exception,
          "Error in Service Bus processor for {TopicName}/{SubscriptionName}: {ErrorSource}",
          destination.Address,
          destination.RoutingKey ?? _options.DefaultSubscriptionName,
          args.ErrorSource
        );
        return Task.CompletedTask;
      };

      // Start processing
      await processor.StartProcessingAsync(cancellationToken);

      _logger.LogInformation(
        "Started subscription to {TopicName}/{SubscriptionName}",
        destination.Address,
        destination.RoutingKey ?? _options.DefaultSubscriptionName
      );

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
  private async Task ApplyCorrelationFilterAsync(
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
          _logger.LogDebug(
            "Deleted rule '{RuleName}' from {TopicName}/{SubscriptionName}",
            rule.Name,
            topicName,
            subscriptionName
          );
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

      _logger.LogInformation(
        "Applied CorrelationFilter for Destination='{Destination}' to {TopicName}/{SubscriptionName}",
        destination,
        topicName,
        subscriptionName
      );
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

  private async Task<ServiceBusSender> GetOrCreateSenderAsync(string topicName, CancellationToken cancellationToken) {
    if (_senders.TryGetValue(topicName, out var existingSender)) {
      return existingSender;
    }

    await _senderLock.WaitAsync(cancellationToken);
    try {
      // Double-check after acquiring lock
      if (_senders.TryGetValue(topicName, out existingSender)) {
        return existingSender;
      }

      var sender = _client.CreateSender(topicName);
      _senders[topicName] = sender;

      _logger.LogDebug("Created sender for topic {TopicName}", topicName);

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

    // Dispose all senders
    foreach (var sender in _senders.Values) {
      await sender.DisposeAsync();
    }
    _senders.Clear();

    // Dispose client
    await _client.DisposeAsync();

    _senderLock.Dispose();

    _logger.LogInformation("Azure Service Bus transport disposed");
  }
}
