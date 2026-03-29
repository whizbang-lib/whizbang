using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of ITransport.
/// Provides reliable pub/sub messaging using RabbitMQ exchanges and queues.
/// RabbitMQ channels are NOT thread-safe, so this transport uses a channel pool.
/// </summary>
/// <docs>messaging/transports/rabbitmq</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Transport implementation with diagnostic logging - I/O bound operations where LoggerMessage overhead isn't justified")]
public class RabbitMQTransport : ITransport, ITransportWithRecovery, IAsyncDisposable {
  private const string UNKNOWN_MESSAGE_ID = "unknown";

  private readonly IConnection _connection;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly RabbitMQChannelPool _channelPool;
  private readonly RabbitMQOptions _options;
  private readonly ILogger<RabbitMQTransport>? _logger;
  private readonly ConcurrentDictionary<string, bool> _declaredExchanges = new();
  private Func<CancellationToken, Task>? _recoveryHandler;
  private bool _disposed;
  private bool _isInitialized;

  /// <summary>
  /// Initializes a new instance of RabbitMQTransport.
  /// </summary>
  /// <param name="connection">RabbitMQ connection (should be a singleton)</param>
  /// <param name="jsonOptions">JSON serialization options for AOT compatibility</param>
  /// <param name="channelPool">Thread-safe channel pool</param>
  /// <param name="options">Transport configuration options</param>
  /// <param name="logger">Optional logger instance</param>
  public RabbitMQTransport(
    IConnection connection,
    JsonSerializerOptions jsonOptions,
    RabbitMQChannelPool channelPool,
    RabbitMQOptions options,
    ILogger<RabbitMQTransport>? logger = null
  ) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(jsonOptions);
    ArgumentNullException.ThrowIfNull(channelPool);
    ArgumentNullException.ThrowIfNull(options);

    _connection = connection;
    _jsonOptions = jsonOptions;
    _channelPool = channelPool;
    _options = options;
    _logger = logger;

    // Hook into connection recovery event to notify subscribers
    _connection.RecoverySucceededAsync += _onConnectionRecoverySucceededAsync;
  }

  /// <inheritdoc />
  public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) {
    _recoveryHandler = onRecovered;
  }

  /// <summary>
  /// Handles RabbitMQ connection recovery event by invoking the recovery handler.
  /// </summary>
  private async Task _onConnectionRecoverySucceededAsync(object sender, AsyncEventArgs args) {
    _logger?.LogInformation("RabbitMQ connection recovered, clearing exchange cache and invoking recovery handler");
    _declaredExchanges.Clear();

    if (_recoveryHandler != null) {
      try {
        await _recoveryHandler(CancellationToken.None);
      } catch (Exception ex) {
        _logger?.LogError(ex, "Error in recovery handler after connection recovery");
      }
    }
  }

  /// <summary>
  /// Declares an exchange if it hasn't been declared yet in this transport instance.
  /// Avoids redundant broker round-trips on every publish (~5s per call).
  /// Cache is cleared on connection recovery.
  /// </summary>
  private async ValueTask _ensureExchangeDeclaredAsync(IChannel channel, string exchangeName, CancellationToken cancellationToken) {
    if (_declaredExchanges.ContainsKey(exchangeName)) {
      return;
    }

    await channel.ExchangeDeclareAsync(
      exchange: exchangeName,
      type: "topic",
      durable: true,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );
    _declaredExchanges.TryAdd(exchangeName, true);
  }

  /// <inheritdoc />
  public bool IsInitialized => _isInitialized;

  /// <inheritdoc />
  public TransportCapabilities Capabilities =>
    TransportCapabilities.PublishSubscribe |
    TransportCapabilities.Reliable |
    TransportCapabilities.BulkPublish |
    (_options.EnableSingleActiveConsumer ? TransportCapabilities.Ordered : TransportCapabilities.None);
  // Note: Ordered only when SAC is enabled - RabbitMQ doesn't guarantee ordering in multi-consumer scenarios

  /// <inheritdoc />
  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (_isInitialized) {
      _logger?.LogDebug("Transport already initialized, skipping");
      return Task.CompletedTask;
    }

    // Verify connection is open
    if (!_connection.IsOpen) {
      throw new InvalidOperationException("RabbitMQ connection is not open");
    }

    _isInitialized = true;
    _logger?.LogInformation("RabbitMQ transport initialized successfully");

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);

    if (!_isInitialized) {
      throw new InvalidOperationException("RabbitMQ transport is not initialized. Call InitializeAsync() first.");
    }

    return _publishCoreAsync(envelope, destination, envelopeType, cancellationToken);
  }

  private async Task _publishCoreAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType,
    CancellationToken cancellationToken
  ) {
    var exchangeName = destination.Address;
    var routingKey = destination.RoutingKey ?? "#";

    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var messageId = envelope.MessageId;
      _logger.LogDebug(
        "Publishing message {MessageId} to exchange {ExchangeName} with routing key {RoutingKey}",
        messageId,
        exchangeName,
        routingKey
      );
    }

    try {
      // Rent channel from pool (RAII pattern - automatically returned on dispose)
      using var pooledChannel = await _channelPool.RentAsync(cancellationToken);
      var channel = pooledChannel.Channel;

      // Declare exchange (cached — only first call per exchange hits the broker)
      await _ensureExchangeDeclaredAsync(channel, exchangeName, cancellationToken);

      // Get envelope type name - prefer provided envelopeType to preserve correct generic type
      // (envelope.GetType() may be MessageEnvelope<object> when loaded from outbox)
      var envelopeTypeName = envelopeType ?? envelope.GetType().AssemblyQualifiedName
        ?? throw new InvalidOperationException("Envelope type must have an assembly qualified name");

      var envelopeRuntimeType = envelope.GetType();

      // Serialize envelope using AOT-compatible JsonContextRegistry
      var typeInfo = _jsonOptions.GetTypeInfo(envelopeRuntimeType)
        ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeRuntimeType.Name}. Ensure the message type is registered via JsonContextRegistry.");

      var json = JsonSerializer.Serialize(envelope, typeInfo);
      var body = Encoding.UTF8.GetBytes(json);

      // Create message properties
      var properties = new BasicProperties {
        MessageId = envelope.MessageId.Value.ToString(),
        ContentType = "application/json",
        Persistent = true,
        Headers = new Dictionary<string, object?>()
      };

      // Add envelope type for deserialization
      properties.Headers["EnvelopeType"] = envelopeTypeName;

      // Add correlation and causation IDs if present
      _setCorrelationAndCausationHeaders(envelope, properties);

      // Add custom metadata (convert JsonElement to RabbitMQ-compatible types)
      if (destination.Metadata != null) {
        foreach (var (key, value) in destination.Metadata) {
          properties.Headers[key] = _convertJsonElementToRabbitMqValue(value);
        }
      }

      // Publish message to exchange
      await channel.BasicPublishAsync(
        exchange: exchangeName,
        routingKey: routingKey,
        mandatory: false,
        basicProperties: properties,
        body: body,
        cancellationToken: cancellationToken
      );

      if (_logger?.IsEnabled(LogLevel.Debug) == true) {
        var messageId = envelope.MessageId;
        _logger.LogDebug(
          "Successfully published message {MessageId} to exchange {ExchangeName}",
          messageId,
          exchangeName
        );
      }
    } catch (AlreadyClosedException ex) {
      // Channel/connection was closed - likely during shutdown or connection failure
      // The message is already persisted to the database (outbox pattern) and will be retried
      _logger?.LogWarning(
        ex,
        "RabbitMQ connection closed while publishing message {MessageId} - message will be retried from outbox",
        envelope.MessageId
      );
      throw new InvalidOperationException(
        $"RabbitMQ connection closed while publishing message {envelope.MessageId}. The message has been persisted and will be retried.",
        ex
      );
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      _logger?.LogError(
        ex,
        "Failed to publish message {MessageId} to exchange {ExchangeName} with routing key {RoutingKey}",
        envelope.MessageId,
        exchangeName,
        routingKey
      );
      throw new InvalidOperationException(
        $"Failed to publish message {envelope.MessageId} to RabbitMQ exchange '{exchangeName}'. See inner exception for details.",
        ex
      );
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

    if (!_isInitialized) {
      throw new InvalidOperationException("RabbitMQ transport is not initialized. Call InitializeAsync() first.");
    }

    if (items.Count == 0) {
      return [];
    }

    var exchangeName = destination.Address;
    var results = new List<BulkPublishItemResult>(items.Count);

    try {
      using var pooledChannel = await _channelPool.RentAsync(cancellationToken);
      var channel = pooledChannel.Channel;

      // Declare exchange (cached — only first call per exchange hits the broker)
      await _ensureExchangeDeclaredAsync(channel, exchangeName, cancellationToken);

      // Publish each item in the batch using the same channel
      foreach (var item in items) {
        try {
          await _publishSingleBatchItemAsync(channel, item, destination, exchangeName, cancellationToken);
          results.Add(new BulkPublishItemResult { MessageId = item.MessageId, Success = true });
        } catch (Exception ex) {
          results.Add(new BulkPublishItemResult {
            MessageId = item.MessageId,
            Success = false,
            Error = $"{ex.GetType().Name}: {ex.Message}"
          });
        }
      }
    } catch (AlreadyClosedException ex) {
      _failRemainingItems(items, results, $"AlreadyClosedException: {ex.Message}");
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      _failRemainingItems(items, results, $"{ex.GetType().Name}: {ex.Message}");
    }

    return results;
  }

  /// <summary>
  /// Publishes a single item within a batch, serializing and setting message properties.
  /// </summary>
  private async Task _publishSingleBatchItemAsync(
    IChannel channel,
    BulkPublishItem item,
    TransportDestination destination,
    string exchangeName,
    CancellationToken cancellationToken
  ) {
    var routingKey = item.RoutingKey ?? destination.RoutingKey ?? "#";
    var envelope = item.Envelope;
    var envelopeTypeName = item.EnvelopeType ?? envelope.GetType().AssemblyQualifiedName
      ?? throw new InvalidOperationException("Envelope type must have an assembly qualified name");
    var envelopeRuntimeType = envelope.GetType();

    var typeInfo = _jsonOptions.GetTypeInfo(envelopeRuntimeType)
      ?? throw new InvalidOperationException($"No JsonTypeInfo found for {envelopeRuntimeType.Name}.");
    var json = JsonSerializer.Serialize(envelope, typeInfo);
    var body = Encoding.UTF8.GetBytes(json);

    var properties = new BasicProperties {
      MessageId = envelope.MessageId.Value.ToString(),
      ContentType = "application/json",
      Persistent = true,
      Headers = new Dictionary<string, object?>()
    };

    properties.Headers["EnvelopeType"] = envelopeTypeName;

    _setCorrelationAndCausationHeaders(envelope, properties);

    if (destination.Metadata != null) {
      foreach (var (key, value) in destination.Metadata) {
        properties.Headers[key] = _convertJsonElementToRabbitMqValue(value);
      }
    }

    await channel.BasicPublishAsync(
      exchange: exchangeName,
      routingKey: routingKey,
      mandatory: false,
      basicProperties: properties,
      body: body,
      cancellationToken: cancellationToken
    );
  }

  /// <summary>
  /// Sets correlation and causation ID headers on message properties from the envelope.
  /// </summary>
  private static void _setCorrelationAndCausationHeaders(IMessageEnvelope envelope, BasicProperties properties) {
    var correlationId = envelope.GetCorrelationId();
    if (correlationId != null) {
      properties.CorrelationId = correlationId.Value.Value.ToString();
    }

    var causationId = envelope.GetCausationId();
    if (causationId != null) {
      properties.Headers!["CausationId"] = causationId.Value.Value.ToString();
    }
  }

  /// <summary>
  /// Adds failure results for all items not yet recorded in the results list.
  /// </summary>
  private static void _failRemainingItems(
    IReadOnlyList<BulkPublishItem> items,
    List<BulkPublishItemResult> results,
    string error
  ) {
    var failedIds = items.Select(i => i.MessageId).Except(results.Select(r => r.MessageId)).ToList();
    foreach (var id in failedIds) {
      results.Add(new BulkPublishItemResult {
        MessageId = id,
        Success = false,
        Error = error
      });
    }
  }

  /// <inheritdoc />
  public Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    ArgumentNullException.ThrowIfNull(handler);
    ArgumentNullException.ThrowIfNull(destination);

    if (!_isInitialized) {
      throw new InvalidOperationException("RabbitMQ transport is not initialized. Call InitializeAsync() first.");
    }

    return _subscribeCoreAsync(handler, destination, cancellationToken);
  }

  private async Task<ISubscription> _subscribeCoreAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken
  ) {
    var exchangeName = destination.Address;
    var queueName = _resolveQueueName(destination, exchangeName);
    var routingPatterns = _getRoutingPatterns(destination);

    _logSubscriptionCreation(exchangeName, queueName, routingPatterns);

    try {
      return await _createSubscriptionAsync(handler, exchangeName, queueName, routingPatterns, cancellationToken);
    } catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) {
      return _throwTimeoutException(exchangeName, queueName, ex);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      return _throwSubscriptionException(exchangeName, queueName, ex);
    }
  }

  /// <summary>
  /// Resolves the queue name from options or destination metadata.
  /// </summary>
  private string _resolveQueueName(TransportDestination destination, string exchangeName) {
    var subscriberName = _getSubscriberName(destination);
    if (_options.DefaultQueueName is null && subscriberName is null) {
      throw new InvalidOperationException(
        "SubscriberName is required in destination metadata for deterministic queue naming. " +
        "Configure SubscriberName when building TransportDestination, or set RabbitMQOptions.DefaultQueueName. " +
        $"Exchange: '{exchangeName}'");
    }

    return _options.DefaultQueueName ?? $"{subscriberName}-{exchangeName}";
  }

  /// <summary>
  /// Logs subscription creation details if debug logging is enabled.
  /// </summary>
  private void _logSubscriptionCreation(string exchangeName, string queueName, List<string> routingPatterns) {
    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var routingPatternsStr = string.Join(", ", routingPatterns);
      _logger.LogDebug(
        "Creating subscription for exchange {ExchangeName}, queue {QueueName}, routing patterns [{RoutingPatterns}]",
        exchangeName,
        queueName,
        routingPatternsStr
      );
    }
  }

  /// <summary>
  /// Creates the subscription by setting up infrastructure, consumer, and starting consumption.
  /// </summary>
  private async Task<ISubscription> _createSubscriptionAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    string exchangeName,
    string queueName,
    List<string> routingPatterns,
    CancellationToken cancellationToken
  ) {
    var channel = await _setupChannelAndInfrastructureAsync(
      exchangeName, queueName, routingPatterns, cancellationToken);

    var consumer = new AsyncEventingBasicConsumer(channel);
    RabbitMQSubscription? subscription = null;

    consumer.ReceivedAsync += (_, args) =>
      _onMessageReceivedAsync(channel, args, handler, subscription, queueName, cancellationToken);

    var consumerTag = await channel.BasicConsumeAsync(
      queue: queueName,
      autoAck: false,
      consumerTag: $"{queueName}-{Guid.NewGuid():N}",
      noLocal: false,
      exclusive: false,
      arguments: null,
      consumer: consumer,
      cancellationToken: cancellationToken
    );

    subscription = new RabbitMQSubscription(channel, queueName, consumerTag, _logger);

    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      _logger.LogDebug(
        "Created subscription for exchange {ExchangeName}, queue {QueueName}, consumer tag {ConsumerTag}",
        exchangeName,
        queueName,
        consumerTag
      );
    }

    return subscription;
  }

  /// <summary>
  /// Handles a received message: checks subscription state, deserializes, invokes handler, and acks/nacks.
  /// </summary>
  private async Task _onMessageReceivedAsync(
    IChannel channel,
    BasicDeliverEventArgs args,
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    RabbitMQSubscription? subscription,
    string queueName,
    CancellationToken cancellationToken
  ) {
    try {
      if (subscription is { IsActive: false }) {
        await _nackPausedMessageAsync(channel, args, queueName);
        return;
      }

      await _processMessageAsync(channel, args, handler, queueName, cancellationToken);
    } catch (Exception ex) when (ex is AlreadyClosedException or ObjectDisposedException) {
      _logger?.LogWarning(
        ex,
        "RabbitMQ channel closed/disposed while processing message {MessageId} from queue {QueueName} - message will be redelivered",
        args.BasicProperties.MessageId ?? UNKNOWN_MESSAGE_ID,
        queueName
      );
    }
  }

  /// <summary>
  /// Nacks a message when the subscription is paused, requeueing for later delivery.
  /// </summary>
  private async Task _nackPausedMessageAsync(IChannel channel, BasicDeliverEventArgs args, string queueName) {
    _logger?.LogWarning(
      "NACK reason: Subscription paused - requeueing message {MessageId} from queue {QueueName}",
      args.BasicProperties.MessageId ?? UNKNOWN_MESSAGE_ID,
      queueName
    );
    await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true);
  }

  /// <summary>
  /// Deserializes and processes a single message, acking on success and handling failures.
  /// </summary>
  private async Task _processMessageAsync(
    IChannel channel,
    BasicDeliverEventArgs args,
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    string queueName,
    CancellationToken cancellationToken
  ) {
    try {
      var envelope = _deserializeMessage(args, out var envelopeTypeName);
      if (envelope == null) {
        await _nackDeserializationFailureAsync(channel, args, queueName);
        return;
      }

      await handler(envelope, envelopeTypeName, cancellationToken);
      await channel.BasicAckAsync(args.DeliveryTag, multiple: false, CancellationToken.None);

      if (_logger?.IsEnabled(LogLevel.Debug) == true) {
        _logger.LogDebug(
          "Processed message {MessageId} from queue {QueueName}",
          args.BasicProperties.MessageId,
          queueName
        );
      }
    } catch (Exception ex) when (ex is not AlreadyClosedException) {
      await _handleMessageFailureAsync(channel, args, queueName, ex);
    }
  }

  /// <summary>
  /// Nacks a message that failed deserialization, sending it to the dead letter queue.
  /// </summary>
  private async Task _nackDeserializationFailureAsync(IChannel channel, BasicDeliverEventArgs args, string queueName) {
    _logger?.LogWarning(
      "NACK reason: Deserialization failed for message {MessageId} from queue {QueueName} - sending to dead letter queue",
      args.BasicProperties.MessageId ?? UNKNOWN_MESSAGE_ID,
      queueName
    );
    await channel.BasicNackAsync(args.DeliveryTag, false, false);
  }

  /// <summary>
  /// Throws an InvalidOperationException for RabbitMQ operation timeouts.
  /// </summary>
  private ISubscription _throwTimeoutException(string exchangeName, string queueName, OperationCanceledException ex) {
    _logger?.LogError(
      ex,
      "RabbitMQ operation timed out while creating subscription for exchange {ExchangeName}, queue {QueueName}",
      exchangeName,
      queueName
    );
    throw new InvalidOperationException(
      $"RabbitMQ operation timed out while creating subscription for exchange '{exchangeName}', queue '{queueName}'. This may indicate network issues or broker unavailability.",
      ex
    );
  }

  /// <summary>
  /// Throws an InvalidOperationException for subscription creation failures.
  /// </summary>
  private ISubscription _throwSubscriptionException(string exchangeName, string queueName, Exception ex) {
    _logger?.LogError(
      ex,
      "Failed to create subscription for exchange {ExchangeName}, queue {QueueName}",
      exchangeName,
      queueName
    );
    throw new InvalidOperationException(
      $"Failed to create RabbitMQ subscription for exchange '{exchangeName}', queue '{queueName}'. See inner exception for details.",
      ex
    );
  }

  /// <inheritdoc />
  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) where TRequest : notnull where TResponse : notnull {
    ObjectDisposedException.ThrowIf(_disposed, this);

    // Request/response not supported in initial implementation
    throw new NotSupportedException(
      "Request/response pattern is not supported by RabbitMQ transport in v0.1.0. " +
      "Use publish/subscribe pattern instead."
    );
  }

  /// <summary>
  /// Creates and configures a channel with exchange, queue declarations, and bindings.
  /// </summary>
  private async Task<IChannel> _setupChannelAndInfrastructureAsync(
    string exchangeName,
    string queueName,
    List<string> routingPatterns,
    CancellationToken cancellationToken
  ) {
    var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

    await channel.BasicQosAsync(
      prefetchSize: 0,
      prefetchCount: _options.PrefetchCount,
      global: false,
      cancellationToken: cancellationToken
    );

    await channel.ExchangeDeclareAsync(
      exchange: exchangeName,
      type: "topic",
      durable: true,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    if (_options.AutoDeclareDeadLetterExchange) {
      await _declareDeadLetterExchangeAsync(channel, exchangeName, queueName, cancellationToken);
    }

    var queueArgs = new Dictionary<string, object?>();
    if (_options.AutoDeclareDeadLetterExchange) {
      queueArgs["x-dead-letter-exchange"] = $"{exchangeName}.dlx";
    }
    if (_options.EnableSingleActiveConsumer) {
      queueArgs["x-single-active-consumer"] = true;
    }

    await channel.QueueDeclareAsync(
      queue: queueName,
      durable: true,
      exclusive: false,
      autoDelete: false,
      arguments: queueArgs,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    foreach (var pattern in routingPatterns) {
      if (_logger?.IsEnabled(LogLevel.Debug) == true) {
        _logger.LogDebug(
          "Binding queue {QueueName} to exchange {ExchangeName} with routing pattern {Pattern}",
          queueName,
          exchangeName,
          pattern
        );
      }

      await channel.QueueBindAsync(
        queue: queueName,
        exchange: exchangeName,
        routingKey: pattern,
        arguments: null,
        noWait: false,
        cancellationToken: cancellationToken
      );
    }

    return channel;
  }

  /// <summary>
  /// Declares dead letter exchange and queue for a given exchange/queue pair.
  /// </summary>
  private static async Task _declareDeadLetterExchangeAsync(
    IChannel channel,
    string exchangeName,
    string queueName,
    CancellationToken cancellationToken
  ) {
    var dlxName = $"{exchangeName}.dlx";
    var dlqName = $"{queueName}.dlq";

    await channel.ExchangeDeclareAsync(
      exchange: dlxName,
      type: "fanout",
      durable: true,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    await channel.QueueDeclareAsync(
      queue: dlqName,
      durable: true,
      exclusive: false,
      autoDelete: false,
      arguments: null,
      passive: false,
      noWait: false,
      cancellationToken: cancellationToken
    );

    await channel.QueueBindAsync(
      queue: dlqName,
      exchange: dlxName,
      routingKey: "",
      arguments: null,
      noWait: false,
      cancellationToken: cancellationToken
    );
  }

  /// <summary>
  /// Extracts the routing patterns from destination metadata.
  /// Returns multiple patterns for creating multiple bindings.
  /// </summary>
  private static List<string> _getRoutingPatterns(TransportDestination destination) {
    // Try "RoutingPatterns" (plural) first - set by SharedTopicInboxStrategy
    if (destination.Metadata?.TryGetValue("RoutingPatterns", out var patternsValue) == true
        && patternsValue.ValueKind == System.Text.Json.JsonValueKind.Array) {
      var patterns = new List<string>();
      foreach (var item in patternsValue.EnumerateArray()) {
        var pattern = item.GetString();
        if (!string.IsNullOrEmpty(pattern)) {
          patterns.Add(pattern);
        }
      }
      if (patterns.Count > 0) {
        return patterns;
      }
    }

    // Fallback: Try "RoutingPattern" (singular)
    if (destination.Metadata?.TryGetValue("RoutingPattern", out var patternValue) == true) {
      var patternStr = patternValue.ToString();
      if (!string.IsNullOrEmpty(patternStr)) {
        return [patternStr];
      }
    }

    // Fallback: Check if RoutingKey contains comma-separated patterns
    if (!string.IsNullOrEmpty(destination.RoutingKey) && destination.RoutingKey.Contains(',')) {
      return [.. destination.RoutingKey.Split(',', StringSplitOptions.RemoveEmptyEntries)];
    }

    // Default: match all
    return ["#"];
  }

  /// <summary>
  /// Extracts the SubscriberName from destination metadata for deterministic queue naming.
  /// Returns null if not found or empty/whitespace.
  /// </summary>
  /// <param name="destination">The transport destination containing metadata</param>
  /// <returns>The subscriber name, or null if not found</returns>
  private static string? _getSubscriberName(TransportDestination destination) {
    if (destination.Metadata?.TryGetValue("SubscriberName", out var subscriberNameValue) == true
        && subscriberNameValue.ValueKind == System.Text.Json.JsonValueKind.String) {
      var name = subscriberNameValue.GetString();
      if (!string.IsNullOrWhiteSpace(name)) {
        return name;
      }
    }
    return null;
  }

  /// <summary>
  /// Deserializes a message from RabbitMQ delivery args.
  /// </summary>
  private IMessageEnvelope? _deserializeMessage(BasicDeliverEventArgs args, out string? envelopeTypeName) {
    envelopeTypeName = null;

    // Get envelope type from headers
    if (args.BasicProperties.Headers?.TryGetValue("EnvelopeType", out var envelopeTypeObj) != true ||
        envelopeTypeObj is not byte[] envelopeTypeBytes) {
      _logger?.LogError("Message {MessageId} missing EnvelopeType header", args.BasicProperties.MessageId);
      return null;
    }

    envelopeTypeName = Encoding.UTF8.GetString(envelopeTypeBytes);
    var json = Encoding.UTF8.GetString(args.Body.Span);

    var typeInfo = Whizbang.Core.Serialization.JsonContextRegistry.GetTypeInfoByName(envelopeTypeName, _jsonOptions);
    if (typeInfo == null) {
      _logger?.LogError("No JsonTypeInfo found for envelope type {EnvelopeType}", envelopeTypeName);
      return null;
    }

    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var typeInfoTypeName = typeInfo.Type.FullName;
      _logger.LogDebug(
        "DIAGNOSTIC [RabbitMQ]: Deserializing envelope. EnvelopeTypeName={EnvelopeTypeName}, TypeInfo={TypeInfoType}",
        envelopeTypeName,
        typeInfoTypeName
      );
    }

    if (JsonSerializer.Deserialize(json, typeInfo) is not IMessageEnvelope envelope) {
      _logger?.LogError("Failed to deserialize message {MessageId} as {EnvelopeType}",
        args.BasicProperties.MessageId, envelopeTypeName);
      return null;
    }

    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var envelopeType = envelope.GetType().FullName;
      var payloadType = envelope.Payload?.GetType().FullName ?? "null";
      var messageId = envelope.MessageId.Value;
      _logger.LogDebug(
        "DIAGNOSTIC [RabbitMQ]: Deserialized envelope. EnvelopeType={EnvelopeType}, PayloadType={PayloadType}, MessageId={MessageId}",
        envelopeType,
        payloadType,
        messageId
      );
    }

    return envelope;
  }

  /// <summary>
  /// Handles message delivery failure with appropriate nack behavior.
  /// </summary>
  private async Task _handleMessageFailureAsync(
    IChannel channel,
    BasicDeliverEventArgs args,
    string queueName,
    Exception ex
  ) {
    _logger?.LogError(
      ex,
      "Error processing message {MessageId} from queue {QueueName}",
      args.BasicProperties.MessageId ?? UNKNOWN_MESSAGE_ID,
      queueName
    );

    // Check delivery count via redelivered flag and custom header
    var deliveryCount = args.Redelivered ? 2 : 1;
    if (args.BasicProperties.Headers?.TryGetValue("x-delivery-count", out var countObj) == true) {
      deliveryCount = Convert.ToInt32(countObj, CultureInfo.InvariantCulture);
    }

    try {
      if (deliveryCount >= _options.MaxDeliveryAttempts) {
        _logger?.LogWarning(
          ex,
          "NACK reason: Handler exception after max delivery attempts ({DeliveryCount}/{MaxAttempts}) for message {MessageId} from queue {QueueName} - sending to dead letter queue",
          deliveryCount,
          _options.MaxDeliveryAttempts,
          args.BasicProperties.MessageId ?? UNKNOWN_MESSAGE_ID,
          queueName
        );
        await channel.BasicNackAsync(args.DeliveryTag, false, false);
      } else {
        _logger?.LogWarning(
          ex,
          "NACK reason: Handler exception (attempt {DeliveryCount}/{MaxAttempts}) for message {MessageId} from queue {QueueName} - requeueing for retry",
          deliveryCount,
          _options.MaxDeliveryAttempts,
          args.BasicProperties.MessageId ?? UNKNOWN_MESSAGE_ID,
          queueName
        );
        await channel.BasicNackAsync(args.DeliveryTag, false, true);
      }
    } catch (Exception channelEx) when (channelEx is AlreadyClosedException or ObjectDisposedException) {
      // Channel/connection was closed or disposed during shutdown - this is expected
      // The message will be redelivered when the consumer reconnects or another instance picks it up
      _logger?.LogWarning(
        channelEx,
        "RabbitMQ channel closed/disposed during failure handling for message {MessageId} - message will be redelivered on reconnection",
        args.BasicProperties.MessageId ?? UNKNOWN_MESSAGE_ID
      );
    }
  }

  /// <summary>
  /// Converts a JsonElement to a RabbitMQ-compatible header value.
  /// RabbitMQ headers support: string, int, long, bool, byte[], and nested tables.
  /// </summary>
  private static object? _convertJsonElementToRabbitMqValue(JsonElement element) {
    return element.ValueKind switch {
      JsonValueKind.String => element.GetString(),
      JsonValueKind.Number when element.TryGetInt32(out var i) => i,
      JsonValueKind.Number when element.TryGetInt64(out var l) => l,
      JsonValueKind.Number => element.GetDouble(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      JsonValueKind.Null => null,
      JsonValueKind.Array => element.EnumerateArray()
        .Select(_convertJsonElementToRabbitMqValue)
        .ToList(),
      JsonValueKind.Object => element.EnumerateObject()
        .ToDictionary(p => p.Name, p => _convertJsonElementToRabbitMqValue(p.Value)),
      _ => element.GetRawText() // Fallback to string representation
    };
  }

  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    _disposed = true;

    // Unhook recovery event to prevent memory leak
    _connection.RecoverySucceededAsync -= _onConnectionRecoverySucceededAsync;
    _recoveryHandler = null;

    // Dispose channel pool
    _channelPool.Dispose();

    // DON'T dispose connection - it's injected and managed externally
    _logger?.LogInformation("RabbitMQ transport disposed (connection managed externally)");

    GC.SuppressFinalize(this);

    await Task.CompletedTask;
  }
}
