namespace Whizbang.Core.Transports;

/// <summary>
/// Interface for provisioning transport infrastructure for owned domains.
/// Implementations create topics, exchanges, or other resources that subscribers will use.
/// </summary>
/// <remarks>
/// This interface is used by the TransportConsumerWorker to provision infrastructure
/// for domains this service owns (publishes events to). Infrastructure is provisioned
/// at worker startup, before subscriptions are created.
///
/// Examples of provisioning:
/// - Azure Service Bus: Create topics via AdminClient
/// - RabbitMQ: Declare topic exchanges
/// - Kafka: Create topics via AdminClient
/// </remarks>
/// <docs>core-concepts/routing#domain-topic-provisioning</docs>
/// <tests>Whizbang.Core.Tests/Transports/InfrastructureProvisionerTests.cs</tests>
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
  /// <tests>Whizbang.Core.Tests/Transports/InfrastructureProvisionerTests.cs:ProvisionOwnedDomains_DeclaresResourcesForEachDomainAsync</tests>
  /// <tests>Whizbang.Core.Tests/Transports/InfrastructureProvisionerTests.cs:ProvisionOwnedDomains_EmptySet_DoesNothingAsync</tests>
  /// <tests>Whizbang.Core.Tests/Transports/InfrastructureProvisionerTests.cs:ProvisionOwnedDomains_CancellationRequested_ThrowsAsync</tests>
  Task ProvisionOwnedDomainsAsync(
    IReadOnlySet<string> ownedDomains,
    CancellationToken cancellationToken = default);
}
