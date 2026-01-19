using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// Manages multiple transport instances and handles publishing/subscribing across them.
/// Enables policy-based routing where messages can be published to multiple transports
/// and subscriptions can be created from multiple sources.
/// </summary>
/// <docs>components/transports</docs>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs</tests>
public interface ITransportManager {
  /// <summary>
  /// Registers a transport for a specific transport type.
  /// If a transport already exists for this type, it will be replaced.
  /// </summary>
  /// <param name="type">The transport type</param>
  /// <param name="transport">The transport instance</param>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldStoreTransportAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_WithNullTransport_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldReplaceExistingTransportAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldStoreDifferentTypesAsync</tests>
  void AddTransport(TransportType type, ITransport transport);

  /// <summary>
  /// Gets a registered transport by type.
  /// </summary>
  /// <param name="type">The transport type</param>
  /// <returns>The transport instance</returns>
  /// <exception cref="InvalidOperationException">If no transport is registered for the type</exception>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:GetTransport_WhenExists_ShouldReturnTransportAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:GetTransport_WhenNotExists_ShouldThrowAsync</tests>
  ITransport GetTransport(TransportType type);

  /// <summary>
  /// Checks if a transport is registered for the specified type.
  /// </summary>
  /// <param name="type">The transport type</param>
  /// <returns>True if a transport is registered, false otherwise</returns>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:HasTransport_WhenExists_ShouldReturnTrueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:HasTransport_WhenNotExists_ShouldReturnFalseAsync</tests>
  bool HasTransport(TransportType type);

  /// <summary>
  /// Publishes a message to multiple transport targets (fan-out pattern).
  /// Each target specifies which transport to use and the destination.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="message">The message to publish</param>
  /// <param name="targets">The list of publish targets</param>
  /// <param name="context">Optional message context</param>
  /// <exception cref="InvalidOperationException">If a transport is not registered for any target</exception>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithEmptyTargets_ShouldNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithNullMessage_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:PublishToTargetsAsync_WithNullTargets_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WithSingleTarget_ShouldPublishAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WithMultipleTargets_ShouldPublishToAllAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WithCustomContext_ShouldUseProvidedContextAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_CreatesEnvelopeWithHopsAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerPublishingTests.cs:PublishToTargetsAsync_WhenTransportNotRegistered_ShouldThrowAsync</tests>
  Task PublishToTargetsAsync<TMessage>(
    TMessage message,
    IReadOnlyList<PublishTarget> targets,
    IMessageContext? context = null
  );

  /// <summary>
  /// Creates subscriptions from multiple transport sources.
  /// Each subscription listens to a different source and invokes the handler for incoming messages.
  /// </summary>
  /// <param name="targets">The list of subscription targets</param>
  /// <param name="handler">The handler to invoke for each incoming message</param>
  /// <returns>List of active subscriptions</returns>
  /// <exception cref="InvalidOperationException">If a transport is not registered for any target</exception>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithEmptyTargets_ShouldReturnEmptyListAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithNullTargets_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:SubscribeFromTargetsAsync_WithNullHandler_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithSingleTarget_ShouldCreateSubscriptionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithMultipleTargets_ShouldCreateMultipleSubscriptionsAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithKafkaConsumerGroup_ShouldIncludeInMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithServiceBusSubscriptionName_ShouldIncludeInMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithServiceBusSqlFilter_ShouldIncludeInMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithRabbitMQQueueName_ShouldIncludeInMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithKafkaPartition_ShouldIncludeInMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithAllMetadata_ShouldIncludeAllInDestinationAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_HandlerReceivesEnvelope_ShouldWorkAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WhenTransportNotRegistered_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithEmptyStringsInMetadata_ShouldNotIncludeThemAsync</tests>
  Task<List<ISubscription>> SubscribeFromTargetsAsync(
    IReadOnlyList<SubscriptionTarget> targets,
    Func<IMessageEnvelope, Task> handler
  );
}
