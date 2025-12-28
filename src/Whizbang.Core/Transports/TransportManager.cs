using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldStoreTransportAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_WithNullTransport_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldReplaceExistingTransportAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldStoreDifferentTypesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:GetTransport_WhenExists_ShouldReturnTransportAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:GetTransport_WhenNotExists_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:HasTransport_WhenExists_ShouldReturnTrueAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:HasTransport_WhenNotExists_ShouldReturnFalseAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithEmptyTargets_ShouldNotThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithNullMessage_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithNullTargets_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithEmptyTargets_ShouldReturnEmptyListAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithNullTargets_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithNullHandler_ShouldThrowAsync</tests>
/// Default implementation of ITransportManager.
/// Manages multiple transport instances and handles publishing/subscribing across them.
/// </summary>
/// <remarks>
/// Creates a new TransportManager with a custom serializer.
/// </remarks>
/// <param name="serializer">The message serializer to use</param>
/// <param name="instanceProvider">Optional service instance provider for message tracing</param>
public class TransportManager(IMessageSerializer serializer, IServiceInstanceProvider? instanceProvider = null) : ITransportManager {
  private readonly Dictionary<TransportType, ITransport> _transports = [];
  private readonly IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
  private readonly IServiceInstanceProvider? _instanceProvider = instanceProvider;

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldStoreTransportAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_WithNullTransport_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldReplaceExistingTransportAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldStoreDifferentTypesAsync</tests>
  public void AddTransport(TransportType type, ITransport transport) {
    ArgumentNullException.ThrowIfNull(transport);
    _transports[type] = transport;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:GetTransport_WhenExists_ShouldReturnTransportAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:GetTransport_WhenNotExists_ShouldThrowAsync</tests>
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
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:HasTransport_WhenExists_ShouldReturnTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:HasTransport_WhenNotExists_ShouldReturnFalseAsync</tests>
  public bool HasTransport(TransportType type) {
    return _transports.ContainsKey(type);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithEmptyTargets_ShouldNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithNullMessage_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithNullTargets_ShouldThrowAsync</tests>
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
    var envelope = _createEnvelope(message, context);

    // Publish to all targets in parallel
    var tasks = targets.Select(target => _publishToTargetAsync(envelope, target));
    await Task.WhenAll(tasks);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithEmptyTargets_ShouldReturnEmptyListAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithNullTargets_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithNullHandler_ShouldThrowAsync</tests>
  public async Task<List<ISubscription>> SubscribeFromTargetsAsync(
    IReadOnlyList<SubscriptionTarget> targets,
    Func<IMessageEnvelope, Task> handler
  ) {
    ArgumentNullException.ThrowIfNull(targets);
    ArgumentNullException.ThrowIfNull(handler);

    // Early exit if no targets
    if (targets.Count == 0) {
      return [];
    }

    // Subscribe to all targets in parallel
    var tasks = targets.Select(target => _subscribeFromTargetAsync(target, handler));
    var subscriptions = await Task.WhenAll(tasks);

    return [.. subscriptions];
  }

  /// <summary>
  /// Publishes an envelope to a single target.
  /// </summary>
  private async Task _publishToTargetAsync(IMessageEnvelope envelope, PublishTarget target) {
    // Get transport for this target
    var transport = GetTransport(target.TransportType);

    // Create destination from target
    var destination = new TransportDestination(
      Address: target.Destination,
      RoutingKey: target.RoutingKey
    );

    // Publish to transport
    await transport.PublishAsync(envelope, destination, envelopeType: null, CancellationToken.None);
  }

  /// <summary>
  /// Creates a subscription from a single target.
  /// </summary>
  private async Task<ISubscription> _subscribeFromTargetAsync(
    SubscriptionTarget target,
    Func<IMessageEnvelope, Task> handler
  ) {
    // Get transport for this target
    var transport = GetTransport(target.TransportType);

    // Build metadata dictionary with transport-specific configuration
    var metadata = new Dictionary<string, JsonElement>();

    // Add consumer group for Kafka
    if (!string.IsNullOrEmpty(target.ConsumerGroup)) {
      metadata["ConsumerGroup"] = JsonElementHelper.FromString(target.ConsumerGroup);
    }

    // Add subscription name for Service Bus
    if (!string.IsNullOrEmpty(target.SubscriptionName)) {
      metadata["SubscriptionName"] = JsonElementHelper.FromString(target.SubscriptionName);
    }

    // Add queue name for RabbitMQ
    if (!string.IsNullOrEmpty(target.QueueName)) {
      metadata["QueueName"] = JsonElementHelper.FromString(target.QueueName);
    }

    // Add SQL filter for Service Bus
    if (!string.IsNullOrEmpty(target.SqlFilter)) {
      metadata["SqlFilter"] = JsonElementHelper.FromString(target.SqlFilter);
    }

    // Add partition for Kafka
    if (target.Partition.HasValue) {
      metadata["Partition"] = JsonElementHelper.FromInt32(target.Partition.Value);
    }

    // Create destination from target
    var destination = new TransportDestination(
      Address: target.Topic,
      RoutingKey: target.RoutingKey,
      Metadata: metadata
    );

    // Wrap handler to match ITransport signature (adds CancellationToken parameter)
    Task __transportHandler(IMessageEnvelope envelope, CancellationToken ct) {
      return handler(envelope);
    }

    // Subscribe to transport (handler is first parameter!)
    return await transport.SubscribeAsync(__transportHandler, destination, CancellationToken.None);
  }

  /// <summary>
  /// Creates a message envelope with hop for observability.
  /// </summary>
  private MessageEnvelope<TMessage> _createEnvelope<TMessage>(
    TMessage message,
    IMessageContext context
  ) {
    return new MessageEnvelope<TMessage> {
      MessageId = context.MessageId,
      Payload = message,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = _instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          Metadata = new Dictionary<string, JsonElement> {
            ["CorrelationId"] = JsonElementHelper.FromString(context.CorrelationId.ToString()),
            ["CausationId"] = JsonElementHelper.FromString(context.CausationId.ToString())
          }
        }
      ]
    };
  }
}
