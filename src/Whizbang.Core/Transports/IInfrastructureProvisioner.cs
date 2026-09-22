namespace Whizbang.Core.Transports;

/// <summary>
/// Interface for provisioning transport infrastructure for owned domains.
/// Implementations create topics, exchanges, or other resources that subscribers will use.
/// </summary>
/// <remarks>
/// <para>This interface is used by the TransportConsumerWorker to provision infrastructure
/// for domains this service owns (publishes events to). Infrastructure is provisioned
/// at worker startup, before subscriptions are created.</para>
///
/// <para>Examples of provisioning:
/// - Azure Service Bus: Create topics via AdminClient
/// - RabbitMQ: Declare topic exchanges
/// - Kafka: Create topics via AdminClient</para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#domain-topic-provisioning</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/InfrastructureProvisionerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Transports/InfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_DefaultImplementation_CompletesWithoutThrowingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerProvisioningTests.cs:ExecuteAsync_WithProvisionerAndOwnedDomains_CallsProvisionerBeforeSubscriptionsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerProvisioningTests.cs:ExecuteAsync_WithEmptyOwnedDomains_SkipsProvisioningAsync</tests>
public interface IInfrastructureProvisioner {
  /// <summary>
  /// Provisions infrastructure for domains this service owns.
  /// Creates topics, exchanges, or other resources needed for publishing events.
  /// </summary>
  /// <param name="ownedDomains">The set of domain namespaces this service owns.</param>
  /// <param name="cancellationToken">Cancellation token to cancel the provisioning.</param>
  /// <returns>Task that completes when provisioning is finished.</returns>
  /// <remarks>
  /// This method should be idempotent - calling it multiple times with the same
  /// domains should be safe. Implementations should handle race conditions where
  /// multiple service instances attempt to provision the same resources.
  /// </remarks>
  Task ProvisionOwnedDomainsAsync(
    IReadOnlySet<string> ownedDomains,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Ensures a single topic/exchange exists, creating it if necessary.
  /// Used for on-demand provisioning during publish to avoid MessagingEntityNotFound errors.
  /// </summary>
  /// <param name="topicName">The topic name to ensure exists.</param>
  /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
  /// <returns>Task that completes when the topic is confirmed to exist.</returns>
  /// <remarks>
  /// This method should be idempotent - calling it multiple times with the same
  /// topic name should be safe. Implementations should handle race conditions where
  /// multiple service instances attempt to create the same topic.
  /// The default implementation is a no-op for transports that don't need pre-creation (e.g., RabbitMQ).
  /// </remarks>
  /// <docs>messaging/transports/azure-service-bus#publish-auto-provisioning</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_TopicDoesNotExist_CreatesItAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_TopicAlreadyExists_DoesNothingAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_RaceCondition_HandlesGracefullyAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_LowercasesTopicNameAsync</tests>
  Task EnsureTopicExistsAsync(
    string topicName,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Provisions every entity named by a <see cref="Whizbang.Core.Routing.TopologyManifest"/> —
  /// the manifest-driven DARK provisioning seam (topology arc phase 5): each publish
  /// destination and each subscription entity is created at startup, before any subscription
  /// is opened, so new entities (e.g. per-namespace command inboxes) exist and sit idle until
  /// publishers flip to them.
  /// </summary>
  /// <param name="manifest">The service's topology manifest (publish destinations +
  /// subscription set + service name for broker-name derivation).</param>
  /// <param name="cancellationToken">Cancellation token to cancel the provisioning.</param>
  /// <returns>Task that completes when provisioning is finished.</returns>
  /// <remarks>
  /// Default implementation is a NO-OP: custom provisioners written before the topology arc
  /// implement only the owned-domains surface and keep their existing behavior; they opt into
  /// manifest provisioning by overriding. Implementations must be idempotent and should cache
  /// existence so a re-provision performs no management operations.
  /// </remarks>
  /// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
  /// <tests>tests/Whizbang.Core.Tests/Transports/InfrastructureProvisionerTests.cs:ProvisionManifestAsync_DefaultImplementation_IsNoOpAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerProvisioningTests.cs:ExecuteAsync_WithManifestResolvable_CallsProvisionManifestBeforeSubscriptionsAsync</tests>
  Task ProvisionManifestAsync(
    Whizbang.Core.Routing.TopologyManifest manifest,
    CancellationToken cancellationToken = default) => Task.CompletedTask;
}
