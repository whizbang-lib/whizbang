using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for AzureServiceBusTransport._ensureTopicExistsViaAdminAsync code path.
/// Uses a real ServiceBusClient with emulator connection string (no actual broker needed)
/// and a testable admin client to verify topic auto-provisioning behavior.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusTransportUnitTests {
  /// <summary>
  /// Emulator-style connection string that creates a real ServiceBusClient without connecting.
  /// The localhost endpoint triggers emulator detection, skipping admin verification in InitializeAsync.
  /// </summary>
  private const string EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=ZmFrZWtleQ==;UseDevelopmentEmulator=true";

  /// <summary>
  /// When auto-provisioning is enabled and a topic does not exist,
  /// PublishAsync should create the topic via the admin client before sending.
  /// </summary>
  [Test]
  public async Task PublishAsync_WithAdminClient_EnsuresTopicExistsAsync() {
    // Arrange
    var adminClient = new TestableAdminClient();
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-topic");

    // Act - PublishAsync will call _getOrCreateSenderAsync -> _ensureTopicExistsViaAdminAsync
    // The actual SendMessageAsync will fail because there's no real broker, but the admin path executes first.
    // Use a short cancellation to abort the send quickly (avoids 30s internal timeout).
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try {
      await transport.PublishAsync(envelope, destination, cancellationToken: cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // Expected - no real broker to send to
    }

    // Assert - topic should have been created
    await Assert.That(adminClient.TopicExistsCalls).Contains("test-topic");
    await Assert.That(adminClient.CreatedTopics).Contains("test-topic");
  }

  /// <summary>
  /// When a topic already exists, PublishAsync should skip topic creation.
  /// </summary>
  [Test]
  public async Task PublishAsync_WithAdminClient_TopicAlreadyExists_SkipsCreationAsync() {
    // Arrange
    var adminClient = new TestableAdminClient {
      ExistingTopics = { "test-topic" }
    };
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-topic");

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try {
      await transport.PublishAsync(envelope, destination, cancellationToken: cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // Expected - no real broker to send to
    }

    // Assert - TopicExistsAsync was called but CreateTopicAsync was not
    await Assert.That(adminClient.TopicExistsCalls).Contains("test-topic");
    await Assert.That(adminClient.CreatedTopics).IsEmpty();
  }

  /// <summary>
  /// When topic creation throws a 409 conflict (race condition with another instance),
  /// the transport should handle it gracefully and continue.
  /// </summary>
  [Test]
  public async Task PublishAsync_WithAdminClient_RaceCondition_HandlesGracefullyAsync() {
    // Arrange
    var adminClient = new TestableAdminClient {
      SimulateRaceConditionForTopic = "test-topic"
    };
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-topic");

    // Act - should not throw from the race condition; the 409 is caught and swallowed
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try {
      await transport.PublishAsync(envelope, destination, cancellationToken: cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // Expected - no real broker to send to, but topic provisioning succeeded (409 was handled)
    }

    // Assert - TopicExistsAsync was called, CreateTopicAsync was attempted (threw 409), no crash
    await Assert.That(adminClient.TopicExistsCalls).Contains("test-topic");
    // CreatedTopics is empty because the 409 threw before adding to the list
    await Assert.That(adminClient.CreatedTopics).IsEmpty();
  }

  // ========================================
  // HELPERS
  // ========================================

  private static AzureServiceBusTransport _createTransport(TestableAdminClient adminClient) {
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = true
    };
    var logger = LoggerFactory
      .Create(builder => builder.SetMinimumLevel(LogLevel.Debug))
      .CreateLogger<AzureServiceBusTransport>();

    return new AzureServiceBusTransport(
      client,
      jsonOptions,
      options,
      logger,
      adminClient
    );
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("unit-test-content"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }

  // ========================================
  // TEST DOUBLES
  // ========================================

  /// <summary>
  /// Testable admin client that tracks calls for assertion and can simulate race conditions.
  /// </summary>
  private sealed class TestableAdminClient : IServiceBusAdminClient {
    public List<string> CreatedTopics { get; } = [];
    public List<string> TopicExistsCalls { get; } = [];
    public HashSet<string> ExistingTopics { get; } = [];
    public string? SimulateRaceConditionForTopic { get; init; }

    public Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) {
      TopicExistsCalls.Add(topicName);
      return Task.FromResult(ExistingTopics.Contains(topicName));
    }

    public Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) {
      if (topicName == SimulateRaceConditionForTopic) {
        throw new RequestFailedException(409, "Topic already exists", "Conflict", null);
      }

      CreatedTopics.Add(topicName);
      return Task.CompletedTask;
    }

    public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task<bool> SubscriptionExistsAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      throw new NotImplementedException();
    }

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
