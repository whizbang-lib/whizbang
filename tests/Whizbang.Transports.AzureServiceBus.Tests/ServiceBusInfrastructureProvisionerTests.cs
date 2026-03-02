using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Tests for ServiceBusInfrastructureProvisioner.
/// Verifies topic provisioning for owned domains.
/// </summary>
public class ServiceBusInfrastructureProvisionerTests {
  /// <summary>
  /// When provisioning owned domains, should create a topic for each domain.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsCreatesTopicForEachDomainAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient();
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient,
      NullLogger<ServiceBusInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "myapp.users", "myapp.orders", "myapp.inventory" };

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert
    await Assert.That(adminClient.CreatedTopics.Count).IsEqualTo(3);
    await Assert.That(adminClient.CreatedTopics)
      .Contains("myapp.users")
      .And.Contains("myapp.orders")
      .And.Contains("myapp.inventory");
  }

  /// <summary>
  /// Should skip existing topics and not attempt to create them.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsSkipsExistingTopicsAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient {
      ExistingTopics = { "myapp.users" }
    };
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient,
      NullLogger<ServiceBusInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "myapp.users", "myapp.orders" };

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert - only myapp.orders should be created (myapp.users already exists)
    await Assert.That(adminClient.CreatedTopics.Count).IsEqualTo(1);
    await Assert.That(adminClient.CreatedTopics).Contains("myapp.orders");
    await Assert.That(adminClient.CreatedTopics).DoesNotContain("myapp.users");
  }

  /// <summary>
  /// Topic names should be lowercased for consistency.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsLowercasesTopicNamesAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient();
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient,
      NullLogger<ServiceBusInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "MyApp.Users", "MYAPP.ORDERS" };

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert
    await Assert.That(adminClient.CreatedTopics.Count).IsEqualTo(2);
    await Assert.That(adminClient.CreatedTopics)
      .Contains("myapp.users")
      .And.Contains("myapp.orders");
  }

  /// <summary>
  /// When owned domains set is empty, should not create any topics.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsEmptySetDoesNothingAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient();
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient,
      NullLogger<ServiceBusInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string>();

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert
    await Assert.That(adminClient.CreatedTopics).IsEmpty();
  }

  /// <summary>
  /// When cancellation is requested, should throw OperationCanceledException.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsCancellationRequestedThrowsAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient();
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient,
      NullLogger<ServiceBusInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "myapp.users" };
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(
      () => provisioner.ProvisionOwnedDomainsAsync(ownedDomains, cts.Token));
  }

  /// <summary>
  /// When a race condition occurs (topic created by another instance),
  /// should handle the conflict gracefully.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsTopicAlreadyExistsHandlesRaceAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient {
      SimulateRaceConditionForTopic = "myapp.users"
    };
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient,
      NullLogger<ServiceBusInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "myapp.users", "myapp.orders" };

    // Act - should not throw
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert - myapp.orders should still be created
    await Assert.That(adminClient.CreatedTopics).Contains("myapp.orders");
  }

  // ========================================
  // TEST DOUBLES
  // ========================================

  /// <summary>
  /// Tracking admin client that records topic operations.
  /// </summary>
  private sealed class TrackingAdminClient : IServiceBusAdminClient {
    public List<string> CreatedTopics { get; } = [];
    public HashSet<string> ExistingTopics { get; } = [];
    public string? SimulateRaceConditionForTopic { get; init; }

    public Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(ExistingTopics.Contains(topicName));
    }

    public Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) {
      cancellationToken.ThrowIfCancellationRequested();

      if (topicName == SimulateRaceConditionForTopic) {
        // Simulate race condition: another instance created the topic first
        throw new RequestFailedException(409, "Topic already exists", "Conflict", null);
      }

      CreatedTopics.Add(topicName);
      return Task.CompletedTask;
    }

    // Namespace management - not needed for provisioner tests
    public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    // Subscription management - not needed for provisioner tests
    public Task<bool> SubscriptionExistsAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    // Rule management - not needed for provisioner tests
    public IAsyncEnumerable<RuleProperties> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }
  }
}

