using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of ITransport.
/// Provides reliable pub/sub messaging using RabbitMQ exchanges and queues.
/// RabbitMQ channels are NOT thread-safe, so this transport uses a channel pool.
/// </summary>
/// <docs>components/transports/rabbitmq</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Transport implementation with diagnostic logging - I/O bound operations where LoggerMessage overhead isn't justified")]
public class RabbitMQTransport : ITransport, IAsyncDisposable {
  private readonly IConnection _connection;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly RabbitMQChannelPool _channelPool;
  private readonly RabbitMQOptions _options;
  private readonly ILogger<RabbitMQTransport>? _logger;
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
  }

  /// <inheritdoc />
  public bool IsInitialized => _isInitialized;

  /// <inheritdoc />
  public TransportCapabilities Capabilities =>
    TransportCapabilities.PublishSubscribe |
    TransportCapabilities.Reliable;
  // Note: NOT Ordered - RabbitMQ doesn't guarantee ordering in multi-consumer scenarios

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
  public async Task PublishAsync(
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

    var exchangeName = destination.Address;
    var routingKey = destination.RoutingKey ?? "#";

    _logger?.LogDebug(
      "Publishing message {MessageId} to exchange {ExchangeName} with routing key {RoutingKey}",
      envelope.MessageId,
      exchangeName,
      routingKey
    );

    try {
      // Rent channel from pool (RAII pattern - automatically returned on dispose)
      using var pooledChannel = await _channelPool.RentAsync(cancellationToken);
      var channel = pooledChannel.Channel;

      // Declare exchange (idempotent - safe to call multiple times)
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

      // Add correlation ID if present
      var correlationId = envelope.GetCorrelationId();
      if (correlationId != null) {
        properties.CorrelationId = correlationId.Value.Value.ToString();
      }

      // Add causation ID if present
      var causationId = envelope.GetCausationId();
      if (causationId != null) {
        properties.Headers["CausationId"] = causationId.Value.Value.ToString();
      }

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

      _logger?.LogDebug(
        "Successfully published message {MessageId} to exchange {ExchangeName}",
        envelope.MessageId,
        exchangeName
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
  public async Task<ISubscription> SubscribeAsync(
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

    var exchangeName = destination.Address;
    var queueName = destination.RoutingKey ?? _options.DefaultQueueName
      ?? throw new InvalidOperationException("Queue name must be specified in TransportDestination.RoutingKey or RabbitMQOptions.DefaultQueueName");

    // Get routing pattern from metadata if present, otherwise default to "#" (all messages)
    var routingPattern = "#";
    if (destination.Metadata?.TryGetValue("RoutingPattern", out var patternValue) == true) {
      var patternStr = patternValue.ToString();
      if (!string.IsNullOrEmpty(patternStr)) {
        routingPattern = patternStr;
      }
    }

    _logger?.LogDebug(
      "Creating subscription for exchange {ExchangeName}, queue {QueueName}, routing pattern {RoutingPattern}",
      exchangeName,
      queueName,
      routingPattern
    );

    try {
      // Create dedicated channel for this consumer (long-lived, one per subscription)
      var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

      // Set QoS prefetch count
      await channel.BasicQosAsync(
        prefetchSize: 0,
        prefetchCount: _options.PrefetchCount,
        global: false,
        cancellationToken: cancellationToken
      );

      // Declare exchange (idempotent)
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

      // Declare dead-letter exchange and queue if enabled
      if (_options.AutoDeclareDeadLetterExchange) {
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

      // Declare consumer queue with dead-letter exchange
      var queueArgs = new Dictionary<string, object?>();
      if (_options.AutoDeclareDeadLetterExchange) {
        queueArgs["x-dead-letter-exchange"] = $"{exchangeName}.dlx";
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

      // Bind queue to exchange with routing pattern
      await channel.QueueBindAsync(
        queue: queueName,
        exchange: exchangeName,
        routingKey: routingPattern,
        arguments: null,
        noWait: false,
        cancellationToken: cancellationToken
      );

      // Create async consumer
      var consumer = new AsyncEventingBasicConsumer(channel);

      // Temporary subscription reference for use in handler (will be set after BasicConsumeAsync)
      RabbitMQSubscription? subscription = null;

      // Set up message received handler
      consumer.ReceivedAsync += async (sender, args) => {
        if (subscription != null && !subscription.IsActive) {
          // If paused, nack the message with requeue
          await channel.BasicNackAsync(
            deliveryTag: args.DeliveryTag,
            multiple: false,
            requeue: true
          );
          return;
        }

        try {
          // Get envelope type from headers
          byte[]? envelopeTypeBytes = null;
          if (args.BasicProperties.Headers?.TryGetValue("EnvelopeType", out var envelopeTypeObj) == true &&
              envelopeTypeObj is byte[] bytes) {
            envelopeTypeBytes = bytes;
          }

          if (envelopeTypeBytes == null) {
            _logger?.LogError("Message {MessageId} missing EnvelopeType header", args.BasicProperties.MessageId);
            await channel.BasicNackAsync(args.DeliveryTag, false, false);
            return;
          }

          var envelopeTypeName = Encoding.UTF8.GetString(envelopeTypeBytes);

          // Deserialize envelope using AOT-compatible JsonContextRegistry
          var json = Encoding.UTF8.GetString(args.Body.Span);

          var typeInfo = Whizbang.Core.Serialization.JsonContextRegistry.GetTypeInfoByName(envelopeTypeName, _jsonOptions);
          if (typeInfo == null) {
            _logger?.LogError("No JsonTypeInfo found for envelope type {EnvelopeType}", envelopeTypeName);
            await channel.BasicNackAsync(args.DeliveryTag, false, false);
            return;
          }

          _logger?.LogDebug(
            "DIAGNOSTIC [RabbitMQ]: Deserializing envelope. EnvelopeTypeName={EnvelopeTypeName}, TypeInfo={TypeInfoType}",
            envelopeTypeName,
            typeInfo.Type.FullName
          );

          if (JsonSerializer.Deserialize(json, typeInfo) is not IMessageEnvelope envelope) {
            _logger?.LogError("Failed to deserialize message {MessageId} as {EnvelopeType}",
              args.BasicProperties.MessageId, envelopeTypeName);
            await channel.BasicNackAsync(args.DeliveryTag, false, false);
            return;
          }

          _logger?.LogDebug(
            "DIAGNOSTIC [RabbitMQ]: Deserialized envelope. EnvelopeType={EnvelopeType}, PayloadType={PayloadType}, MessageId={MessageId}",
            envelope.GetType().FullName,
            envelope.Payload?.GetType().FullName ?? "null",
            envelope.MessageId.Value
          );

          // Invoke handler with envelope type metadata
          await handler(envelope, envelopeTypeName, cancellationToken);

          // Acknowledge the message
          await channel.BasicAckAsync(
            deliveryTag: args.DeliveryTag,
            multiple: false
          );

          _logger?.LogDebug(
            "Processed message {MessageId} from queue {QueueName}",
            args.BasicProperties.MessageId,
            queueName
          );
        } catch (Exception ex) {
          _logger?.LogError(
            ex,
            "Error processing message {MessageId} from queue {QueueName}",
            args.BasicProperties.MessageId ?? "unknown",
            queueName
          );

          // Check delivery count via redelivered flag and custom header
          var deliveryCount = args.Redelivered ? 2 : 1;
          if (args.BasicProperties.Headers?.TryGetValue("x-delivery-count", out var countObj) == true) {
            deliveryCount = Convert.ToInt32(countObj, CultureInfo.InvariantCulture);
          }

          if (deliveryCount >= _options.MaxDeliveryAttempts) {
            _logger?.LogWarning(
              "Message {MessageId} exceeded max delivery attempts ({MaxAttempts}), nacking without requeue",
              args.BasicProperties.MessageId,
              _options.MaxDeliveryAttempts
            );
            await channel.BasicNackAsync(args.DeliveryTag, false, false);
          } else {
            // Nack with requeue for retry
            await channel.BasicNackAsync(args.DeliveryTag, false, true);
          }
        }
      };

      // Start consuming
      var consumerTag = await channel.BasicConsumeAsync(
        queue: queueName,
        autoAck: false, // Manual acknowledgment for reliability
        consumerTag: $"{queueName}-{Guid.NewGuid():N}",
        noLocal: false,
        exclusive: false,
        arguments: null,
        consumer: consumer,
        cancellationToken: cancellationToken
      );

      // Create subscription wrapper with consumer tag (so Dispose can cancel consumer explicitly)
      subscription = new RabbitMQSubscription(channel, queueName, consumerTag, _logger);

      _logger?.LogInformation(
        "Created subscription for exchange {ExchangeName}, queue {QueueName}, consumer tag {ConsumerTag}",
        exchangeName,
        queueName,
        consumerTag
      );

      return subscription;
    } catch (Exception ex) when (ex is not OperationCanceledException) {
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

    // Dispose channel pool
    _channelPool.Dispose();

    // DON'T dispose connection - it's injected and managed externally
    _logger?.LogInformation("RabbitMQ transport disposed (connection managed externally)");

    GC.SuppressFinalize(this);

    await Task.CompletedTask;
  }
}
