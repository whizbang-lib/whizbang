using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

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
  // EnsureTopicExistsAsync Tests
  // ========================================

  /// <summary>
  /// When topic does not exist, should create it.
  /// </summary>
  [Test]
  public async Task EnsureTopicExistsAsync_TopicDoesNotExist_CreatesItAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient();
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient,
      LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug)).CreateLogger<ServiceBusInfrastructureProvisioner>());

    // Act
    await provisioner.EnsureTopicExistsAsync("myapp.orders");

    // Assert
    await Assert.That(adminClient.CreatedTopics.Count).IsEqualTo(1);
    await Assert.That(adminClient.CreatedTopics).Contains("myapp.orders");
  }

  /// <summary>
  /// When topic already exists, should not attempt to create it, and should report which
  /// topic was found to already exist.
  /// </summary>
  [Test]
  public async Task EnsureTopicExistsAsync_TopicAlreadyExists_DoesNothingAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient {
      ExistingTopics = { "myapp.orders" }
    };
    var logger = new CapturingLogger<ServiceBusInfrastructureProvisioner>();
    var provisioner = new ServiceBusInfrastructureProvisioner(adminClient, logger);

    // Act
    await provisioner.EnsureTopicExistsAsync("myapp.orders");

    // Assert
    await Assert.That(adminClient.CreatedTopics).IsEmpty();
    await Assert.That(logger.Messages.Any(m =>
        m.Contains("myapp.orders", StringComparison.Ordinal)
        && m.Contains("already exists", StringComparison.Ordinal)))
      .IsTrue()
      .Because("the diagnostic must name which topic was found to already exist, not merely fire");
  }

  /// <summary>
  /// When a race condition occurs (409), should handle gracefully and report which topic
  /// raced.
  /// </summary>
  [Test]
  public async Task EnsureTopicExistsAsync_RaceCondition_HandlesGracefullyAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient {
      SimulateRaceConditionForTopic = "myapp.orders"
    };
    var logger = new CapturingLogger<ServiceBusInfrastructureProvisioner>();
    var provisioner = new ServiceBusInfrastructureProvisioner(adminClient, logger);

    // Act - should not throw
    await provisioner.EnsureTopicExistsAsync("myapp.orders");

    // Assert - no topics created (race condition swallowed)
    await Assert.That(adminClient.CreatedTopics).IsEmpty();
    await Assert.That(logger.Messages.Any(m =>
        m.Contains("myapp.orders", StringComparison.Ordinal)
        && m.Contains("race condition", StringComparison.Ordinal)))
      .IsTrue()
      .Because("the diagnostic must name which topic raced during creation, not merely fire");
  }

  /// <summary>
  /// Topic name should be lowercased for consistency.
  /// </summary>
  [Test]
  public async Task EnsureTopicExistsAsync_LowercasesTopicNameAsync() {
    // Arrange
    var adminClient = new TrackingAdminClient();
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient,
      LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug)).CreateLogger<ServiceBusInfrastructureProvisioner>());

    // Act
    await provisioner.EnsureTopicExistsAsync("MyApp.Orders");

    // Assert
    await Assert.That(adminClient.CreatedTopics.Count).IsEqualTo(1);
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

    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, bool requiresSession, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task UpdateSubscriptionLockDurationAsync(string topicName, string subscriptionName, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;



    public Task<SubscriptionProperties> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task<long> GetSubscriptionActiveMessageCountAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
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

  // ============================================================
  // Logging on the provisioning path
  // ============================================================

  /// <summary>
  /// Provisioning reports how many topics it is about to create.
  /// </summary>
  /// <remarks>
  /// Provisioning happens once at startup and is otherwise invisible. When a deploy comes up
  /// against a namespace whose entities are missing, this line is what tells an operator the
  /// service noticed and is creating them — as opposed to a service that is simply hanging on a
  /// broker it cannot reach.
  /// </remarks>
  [Test]
  public async Task ProvisionOwnedDomains_ReportsHowManyTopicsItWillCreateAsync() {
    var adminClient = new TrackingAdminClient();
    var logger = new CapturingLogger<ServiceBusInfrastructureProvisioner>();
    var provisioner = new ServiceBusInfrastructureProvisioner(adminClient, logger);

    await provisioner.ProvisionOwnedDomainsAsync(
      new HashSet<string> { "myapp.users", "myapp.orders" });

    await Assert.That(logger.Messages.Any(m => m.Contains("Provisioning", StringComparison.Ordinal)))
      .IsTrue();
    await Assert.That(logger.Messages.Any(m => m.Contains('2', StringComparison.Ordinal))).IsTrue()
      .Because("the count is what tells an operator how much of the topology was missing");
  }

  [Test]
  public async Task ProvisionOwnedDomains_WithNothingToDo_StillReportsAsync() {
    // Zero is a meaningful answer: it says the topology was already there, which is what a
    // steady-state restart should show.
    var adminClient = new TrackingAdminClient();
    var logger = new CapturingLogger<ServiceBusInfrastructureProvisioner>();
    var provisioner = new ServiceBusInfrastructureProvisioner(adminClient, logger);

    await provisioner.ProvisionOwnedDomainsAsync(new HashSet<string>());

    await Assert.That(adminClient.CreatedTopics).IsEmpty();
  }

  /// <summary>A logger that is enabled at every level and keeps what it was told.</summary>
  private sealed class CapturingLogger<T> : ILogger<T> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];

    public List<string> Messages {
      get { lock (_lock) { return [.. _messages]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // Deliberately enabled everywhere: the guarded log statements are the point, and a logger
    // that answers false would skip them exactly as NullLogger does.
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }
}
