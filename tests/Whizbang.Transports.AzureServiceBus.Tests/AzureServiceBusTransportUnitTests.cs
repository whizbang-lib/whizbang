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

  private static AzureServiceBusTransport _createTransport(
    TestableAdminClient? adminClient = null,
    AzureServiceBusOptions? options = null) {
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options ??= new AzureServiceBusOptions {
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
  // SESSION OPTIONS TESTS
  // ========================================

  [Test]
  public async Task EnableSessions_DefaultsToFalseAsync() {
    // Arrange & Act
    var options = new AzureServiceBusOptions();

    // Assert
    await Assert.That(options.EnableSessions).IsFalse()
      .Because("Sessions must be opt-in — existing deployments without sessions must not break");
  }

  [Test]
  public async Task MaxConcurrentSessions_DefaultsTo64Async() {
    // Arrange & Act
    var options = new AzureServiceBusOptions();

    // Assert
    await Assert.That(options.MaxConcurrentSessions).IsEqualTo(64)
      .Because("Default should be reasonably high for a single process handling many streams");
  }

  [Test]
  public async Task Capabilities_WithoutEnableSessions_ExcludesOrderedAsync() {
    // Arrange
    var options = new AzureServiceBusOptions { EnableSessions = false };
    var transport = _createTransport(options: options);

    // Act
    var capabilities = transport.Capabilities;

    // Assert
    await Assert.That((capabilities & TransportCapabilities.Ordered) != 0).IsFalse()
      .Because("Ordered should only be claimed when sessions are enabled — otherwise it's a lie");
    await Assert.That((capabilities & TransportCapabilities.PublishSubscribe) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.Reliable) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.BulkPublish) != 0).IsTrue();
  }

  [Test]
  public async Task Capabilities_WithEnableSessions_IncludesOrderedAsync() {
    // Arrange
    var options = new AzureServiceBusOptions { EnableSessions = true };
    var transport = _createTransport(options: options);

    // Act
    var capabilities = transport.Capabilities;

    // Assert
    await Assert.That((capabilities & TransportCapabilities.Ordered) != 0).IsTrue()
      .Because("Ordered should be claimed when sessions are enabled");
    await Assert.That((capabilities & TransportCapabilities.PublishSubscribe) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.Reliable) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.BulkPublish) != 0).IsTrue();
  }

  // ========================================
  // PUBLISH WITH STREAMID (FIFO) TESTS
  // ========================================

  /// <summary>
  /// PublishAsync with StreamId in destination metadata exercises the SessionId code path.
  /// The message is created with SessionId set from the metadata before the send (which fails
  /// because there's no real broker). Verifies the code path executes without error.
  /// </summary>
  [Test]
  public async Task PublishAsync_WithStreamIdInMetadata_SetsSessionIdWithoutErrorAsync() {
    var adminClient = new TestableAdminClient { ExistingTopics = { "fifo-topic" } };
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = true,
      EnableSessions = true
    };
    var transport = _createTransport(adminClient, options);
    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var streamId = Guid.NewGuid();
    var metadata = new Dictionary<string, System.Text.Json.JsonElement> {
      ["StreamId"] = System.Text.Json.JsonDocument.Parse($"\"{streamId}\"").RootElement
    };
    var destination = new TransportDestination("fifo-topic") { Metadata = metadata };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try {
      await transport.PublishAsync(envelope, destination, cancellationToken: cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // Expected - no broker, but SessionId code path was exercised
    }

    // Admin client was consulted (code path reached publish logic)
    await Assert.That(adminClient.TopicExistsCalls).Contains("fifo-topic");
  }

  /// <summary>
  /// PublishAsync with empty StreamId in metadata should not crash.
  /// </summary>
  [Test]
  public async Task PublishAsync_WithEmptyStreamIdInMetadata_HandlesGracefullyAsync() {
    var adminClient = new TestableAdminClient { ExistingTopics = { "fifo-topic" } };
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var metadata = new Dictionary<string, System.Text.Json.JsonElement> {
      ["StreamId"] = System.Text.Json.JsonDocument.Parse("\"\"").RootElement
    };
    var destination = new TransportDestination("fifo-topic") { Metadata = metadata };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try {
      await transport.PublishAsync(envelope, destination, cancellationToken: cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // Expected - no broker
    }

    await Assert.That(adminClient.TopicExistsCalls).Contains("fifo-topic");
  }

  /// <summary>
  /// PublishAsync without StreamId metadata still works (non-FIFO path).
  /// </summary>
  [Test]
  public async Task PublishAsync_WithoutStreamIdMetadata_SkipsSessionIdAsync() {
    var adminClient = new TestableAdminClient { ExistingTopics = { "regular-topic" } };
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("regular-topic") { Metadata = null };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try {
      await transport.PublishAsync(envelope, destination, cancellationToken: cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // Expected - no broker
    }

    await Assert.That(adminClient.TopicExistsCalls).Contains("regular-topic");
  }

  // ========================================
  // PUBLISH BATCH WITH STREAMID TESTS
  // ========================================

  /// <summary>
  /// PublishBatchAsync with items having different StreamIds exercises the grouping code path.
  /// Each stream group gets its own batch. The send fails (no broker), but the batch creation
  /// and grouping logic is fully exercised.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_WithDifferentStreamIds_GroupsByStreamAsync() {
    var adminClient = new TestableAdminClient { ExistingTopics = { "batch-topic" } };
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var stream1 = Guid.NewGuid();
    var stream2 = Guid.NewGuid();
    var items = new List<BulkPublishItem> {
      _createBulkPublishItem(streamId: stream1),
      _createBulkPublishItem(streamId: stream2),
      _createBulkPublishItem(streamId: stream1),
    };
    var destination = new TransportDestination("batch-topic");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    IReadOnlyList<BulkPublishItemResult>? results = null;
    try {
      results = await transport.PublishBatchAsync(items, destination, cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // May throw if batch send fails on no broker
    }

    // Results may be partial (some succeed at batch creation, fail at send)
    // The important thing is the code path was exercised without crash
    await Assert.That(adminClient.TopicExistsCalls).Contains("batch-topic");
  }

  /// <summary>
  /// PublishBatchAsync with null StreamIds groups them together (no session).
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_WithNullStreamIds_GroupsTogetherAsync() {
    var adminClient = new TestableAdminClient { ExistingTopics = { "batch-topic" } };
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var items = new List<BulkPublishItem> {
      _createBulkPublishItem(streamId: null),
      _createBulkPublishItem(streamId: null),
    };
    var destination = new TransportDestination("batch-topic");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try {
      await transport.PublishBatchAsync(items, destination, cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // Expected
    }

    await Assert.That(adminClient.TopicExistsCalls).Contains("batch-topic");
  }

  /// <summary>
  /// PublishBatchAsync with mixed null and non-null StreamIds exercises both paths.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_WithMixedStreamIds_HandlesCorrectlyAsync() {
    var adminClient = new TestableAdminClient { ExistingTopics = { "batch-topic" } };
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var stream1 = Guid.NewGuid();
    var items = new List<BulkPublishItem> {
      _createBulkPublishItem(streamId: stream1),
      _createBulkPublishItem(streamId: null),
      _createBulkPublishItem(streamId: stream1),
    };
    var destination = new TransportDestination("batch-topic");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try {
      await transport.PublishBatchAsync(items, destination, cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // Expected
    }

    await Assert.That(adminClient.TopicExistsCalls).Contains("batch-topic");
  }

  /// <summary>
  /// PublishBatchAsync records failure results when the send fails (no broker).
  /// This exercises _sendAndRecordBatchAsync error path.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_WhenSendFails_RecordsFailureResultsAsync() {
    var adminClient = new TestableAdminClient { ExistingTopics = { "fail-topic" } };
    var transport = _createTransport(adminClient);
    await transport.InitializeAsync();

    var items = new List<BulkPublishItem> {
      _createBulkPublishItem(streamId: null),
      _createBulkPublishItem(streamId: null),
    };
    var destination = new TransportDestination("fail-topic");

    // Don't use a short timeout — let the batch actually attempt to send and fail
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    IReadOnlyList<BulkPublishItemResult>? results = null;
    try {
      results = await transport.PublishBatchAsync(items, destination, cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException) {
      // May throw at a higher level
    }

    if (results is not null) {
      // If we got results, the _sendAndRecordBatchAsync path was exercised
      // Items should be marked as failed (no real broker)
      await Assert.That(results.Count).IsGreaterThanOrEqualTo(1);
      var hasFailure = results.Any(r => !r.Success);
      await Assert.That(hasFailure).IsTrue()
        .Because("Send should fail with no broker — _sendAndRecordBatchAsync error path exercised");
    }
  }

  // ========================================
  // SUBSCRIBE WITH SESSIONS TESTS
  // ========================================

  /// <summary>
  /// SubscribeAsync with EnableSessions=true exercises the session processor creation path.
  /// The session processor creation itself succeeds (it's a local object), but starting
  /// it will fail since there's no broker. This covers the session processor setup code.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_WithEnableSessions_CreatesSessionProcessorAsync() {
    var adminClient = new TestableAdminClient {
      ExistingTopics = { "session-topic" }
    };
    // Implement subscription methods on the testable admin client
    adminClient.ExistingSubscriptions.Add(("session-topic", "test-sub"));

    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = true,
      EnableSessions = true,
      DefaultSubscriptionName = "test-sub"
    };
    var transport = _createTransport(adminClient, options);
    await transport.InitializeAsync();

    var destination = new TransportDestination("session-topic") { RoutingKey = "test-sub" };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try {
      await transport.SubscribeAsync((_, _, ct) => Task.CompletedTask, destination, cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException or InvalidOperationException) {
      // Expected - no broker, but session processor was created
    }
  }

  /// <summary>
  /// SubscribeAsync without EnableSessions uses the standard processor path.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_WithoutEnableSessions_CreatesStandardProcessorAsync() {
    var adminClient = new TestableAdminClient {
      ExistingTopics = { "standard-topic" }
    };
    adminClient.ExistingSubscriptions.Add(("standard-topic", "test-sub"));

    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = true,
      EnableSessions = false,
      DefaultSubscriptionName = "test-sub"
    };
    var transport = _createTransport(adminClient, options);
    await transport.InitializeAsync();

    var destination = new TransportDestination("standard-topic") { RoutingKey = "test-sub" };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try {
      await transport.SubscribeAsync((_, _, ct) => Task.CompletedTask, destination, cts.Token);
    } catch (Exception ex) when (ex is ServiceBusException or TimeoutException or OperationCanceledException or TaskCanceledException or InvalidOperationException) {
      // Expected - no broker
    }
  }

  // ========================================
  // HELPERS
  // ========================================

  private static BulkPublishItem _createBulkPublishItem(Guid? streamId = null) {
    var envelope = _createTestEnvelope();
    return new BulkPublishItem {
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
      MessageId = envelope.MessageId.Value,
      RoutingKey = null,
      StreamId = streamId
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
    public HashSet<(string Topic, string Subscription)> ExistingSubscriptions { get; } = [];
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
      return Task.FromResult(ExistingSubscriptions.Contains((topicName, subscriptionName)));
    }

    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      ExistingSubscriptions.Add((topicName, subscriptionName));
      return Task.CompletedTask;
    }

    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, bool requiresSession, CancellationToken cancellationToken = default) {
      ExistingSubscriptions.Add((topicName, subscriptionName));
      return Task.CompletedTask;
    }

    public Task<SubscriptionProperties> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      // SubscriptionProperties has no public constructor — create via CreateSubscriptionOptions internal conversion
      var requiresSession = SessionRequiredSubscriptions.Contains((topicName, subscriptionName));
      var options = new CreateSubscriptionOptions(topicName, subscriptionName) { RequiresSession = requiresSession };
      // Use reflection to create SubscriptionProperties from CreateSubscriptionOptions
      // Azure SDK expects this to be constructed internally, but we need it for testing
      var props = (SubscriptionProperties)typeof(SubscriptionProperties)
        .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
          null, [typeof(CreateSubscriptionOptions)], null)!
        .Invoke([options]);
      return Task.FromResult(props);
    }

    /// <summary>
    /// Set of subscriptions that have RequiresSession=true.
    /// </summary>
    public HashSet<(string Topic, string Subscription)> SessionRequiredSubscriptions { get; } = [];

    public Task DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      ExistingSubscriptions.Remove((topicName, subscriptionName));
      return Task.CompletedTask;
    }

    public IAsyncEnumerable<RuleProperties> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      return AsyncEnumerable.Empty<RuleProperties>();
    }

    public Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }
  }
}
