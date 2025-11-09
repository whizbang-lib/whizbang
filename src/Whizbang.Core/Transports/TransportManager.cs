using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// Default implementation of ITransportManager.
/// Manages multiple transport instances and handles publishing/subscribing across them.
/// </summary>
public class TransportManager : ITransportManager {
  private readonly Dictionary<TransportType, ITransport> _transports = new();
  private readonly IMessageSerializer _serializer;

  /// <summary>
  /// Creates a new TransportManager with the default JSON serializer.
  /// </summary>
  public TransportManager() {
    _serializer = new JsonMessageSerializer();
  }

  /// <summary>
  /// Creates a new TransportManager with a custom serializer.
  /// </summary>
  /// <param name="serializer">The message serializer to use</param>
  public TransportManager(IMessageSerializer serializer) {
    _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
  }

  /// <inheritdoc />
  public void AddTransport(TransportType type, ITransport transport) {
    ArgumentNullException.ThrowIfNull(transport);
    _transports[type] = transport;
  }

  /// <inheritdoc />
  public ITransport GetTransport(TransportType type) {
    if (!_transports.TryGetValue(type, out var transport)) {
      throw new InvalidOperationException(
        $"Transport type '{type}' is not registered. " +
        $"Please register it using AddTransport() before use."
      );
    }
    return transport;
  }

  /// <inheritdoc />
  public bool HasTransport(TransportType type) {
    return _transports.ContainsKey(type);
  }

  /// <inheritdoc />
  public async Task PublishToTargetsAsync<TMessage>(
    TMessage message,
    IReadOnlyList<PublishTarget> targets,
    IMessageContext? context = null
  ) {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(targets);

    // Early exit if no targets
    if (targets.Count == 0) {
      return;
    }

    // Create context if not provided
    context ??= MessageContext.New();

    // Create envelope once (shared across all targets)
    var envelope = CreateEnvelope(message, context);

    // Publish to all targets in parallel
    var tasks = targets.Select(target => PublishToTargetAsync(envelope, target));
    await Task.WhenAll(tasks);
  }

  /// <inheritdoc />
  public async Task<List<ISubscription>> SubscribeFromTargetsAsync(
    IReadOnlyList<SubscriptionTarget> targets,
    Func<IMessageEnvelope, Task> handler
  ) {
    ArgumentNullException.ThrowIfNull(targets);
    ArgumentNullException.ThrowIfNull(handler);

    // Early exit if no targets
    if (targets.Count == 0) {
      return new List<ISubscription>();
    }

    // Subscribe to all targets in parallel
    var tasks = targets.Select(target => SubscribeFromTargetAsync(target, handler));
    var subscriptions = await Task.WhenAll(tasks);

    return subscriptions.ToList();
  }

  /// <summary>
  /// Publishes an envelope to a single target.
  /// </summary>
  private async Task PublishToTargetAsync(IMessageEnvelope envelope, PublishTarget target) {
    // Get transport for this target
    var transport = GetTransport(target.TransportType);

    // Create destination from target
    var destination = new TransportDestination(
      Address: target.Destination,
      RoutingKey: target.RoutingKey
    );

    // Publish to transport
    await transport.PublishAsync(envelope, destination, CancellationToken.None);
  }

  /// <summary>
  /// Creates a subscription from a single target.
  /// </summary>
  private async Task<ISubscription> SubscribeFromTargetAsync(
    SubscriptionTarget target,
    Func<IMessageEnvelope, Task> handler
  ) {
    // Get transport for this target
    var transport = GetTransport(target.TransportType);

    // Build metadata dictionary with transport-specific configuration
    var metadata = new Dictionary<string, object>();

    // Add consumer group for Kafka
    if (!string.IsNullOrEmpty(target.ConsumerGroup)) {
      metadata["ConsumerGroup"] = target.ConsumerGroup;
    }

    // Add subscription name for Service Bus
    if (!string.IsNullOrEmpty(target.SubscriptionName)) {
      metadata["SubscriptionName"] = target.SubscriptionName;
    }

    // Add queue name for RabbitMQ
    if (!string.IsNullOrEmpty(target.QueueName)) {
      metadata["QueueName"] = target.QueueName;
    }

    // Add SQL filter for Service Bus
    if (!string.IsNullOrEmpty(target.SqlFilter)) {
      metadata["SqlFilter"] = target.SqlFilter;
    }

    // Add partition for Kafka
    if (target.Partition.HasValue) {
      metadata["Partition"] = target.Partition.Value;
    }

    // Create destination from target
    var destination = new TransportDestination(
      Address: target.Topic,
      RoutingKey: target.RoutingKey,
      Metadata: metadata
    );

    // Wrap handler to match ITransport signature (adds CancellationToken parameter)
    Task transportHandler(IMessageEnvelope envelope, CancellationToken ct) {
      return handler(envelope);
    }

    // Subscribe to transport (handler is first parameter!)
    return await transport.SubscribeAsync(transportHandler, destination, CancellationToken.None);
  }

  /// <summary>
  /// Creates a message envelope with hop for observability.
  /// </summary>
  private MessageEnvelope<TMessage> CreateEnvelope<TMessage>(
    TMessage message,
    IMessageContext context
  ) {
    return new MessageEnvelope<TMessage> {
      MessageId = context.MessageId,
      Payload = message,
      Hops = new List<MessageHop> {
        new MessageHop {
          Type = HopType.Current,
          ServiceName = "TransportManager", // TODO: Get from configuration
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = new Dictionary<string, object> {
            ["CorrelationId"] = context.CorrelationId.ToString(),
            ["CausationId"] = context.CausationId.ToString()
          }
        }
      }
    };
  }
}
