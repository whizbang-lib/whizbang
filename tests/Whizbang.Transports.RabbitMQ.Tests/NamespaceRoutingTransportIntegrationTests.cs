using System.Text.Json;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable TUnit0023 // Disposable field should be disposed in cleanup method

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Integration tests for NamespaceRoutingStrategy with real RabbitMQ transport.
/// Verifies that messages are routed to correct exchanges/queues based on namespace patterns.
/// Uses Testcontainers for a real RabbitMQ instance.
/// </summary>
[Category("Integration")]
[NotInParallel]
public sealed class NamespaceRoutingTransportIntegrationTests : IAsyncDisposable {
  private static RabbitMqContainer? _rabbitMqContainer;
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static bool _initialized;

  private IConnection? _connection;
  private RabbitMQChannelPool? _channelPool;
  private RabbitMQTransport? _transport;

  [Before(Test)]
  public async Task SetupAsync() {
    // Initialize container once for all tests
    await _initializeContainerAsync();

    // Create connection for this test
    var factory = new ConnectionFactory {
      Uri = new Uri(_rabbitMqContainer!.GetConnectionString())
    };
    _connection = await factory.CreateConnectionAsync();
    _channelPool = new RabbitMQChannelPool(_connection, maxChannels: 5);

    // Use JsonContextRegistry to get combined options with all registered types
    // This ensures MessageEnvelope<TestMessage> can be deserialized by the subscriber
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new RabbitMQOptions();

    _transport = new RabbitMQTransport(
      _connection,
      jsonOptions,
      _channelPool,
      options,
      logger: null
    );

    await _transport.InitializeAsync();
  }

  [After(Test)]
  public Task CleanupAsync() {
    // Fire-and-forget cleanup to avoid test hanging on connection close
    var transport = _transport;
    var channelPool = _channelPool;
    var connection = _connection;

    _transport = null;
    _channelPool = null;
    _connection = null;

    _ = Task.Run(async () => {
      try {
        if (transport != null) {
          await transport.DisposeAsync();
        }

        channelPool?.Dispose();

        if (connection != null) {
          await connection.CloseAsync();
          connection.Dispose();
        }
      } catch {
        // Ignore cleanup errors
      }
    }, CancellationToken.None);

    return Task.CompletedTask;
  }

  // ========================================
  // REAL RABBITMQ TRANSPORT TESTS
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task PublishAsync_WithNamespaceRouting_HierarchicalNamespace_RoutesToCorrectExchangeAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();
    var messageType = typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated);

    // Get topic from strategy
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Verify topic is correct
    await Assert.That(topic).IsEqualTo("orders");

    // Set up consumer to verify message arrival
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      // Small delay to ensure consumer is fully registered
      await Task.Delay(500, cancellationToken);

      // Create and publish message
      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, new TransportDestination(topic), cancellationToken: cancellationToken);

      // Assert - Message should arrive at "orders" exchange
      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at exchange '{topic}' within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task PublishAsync_WithNamespaceRouting_FlatNamespace_ExtractsFromTypeNameAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();
    var messageType = typeof(TestNamespaces.MyApp.Contracts.Commands.CreateOrder);

    // Get topic from strategy
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Verify topic is correct (extracted from type name)
    await Assert.That(topic).IsEqualTo("order");

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      // Small delay to ensure consumer is fully registered
      await Task.Delay(500, cancellationToken);

      // Create and publish message
      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, new TransportDestination(topic), cancellationToken: cancellationToken);

      // Assert - Message should arrive at "order" exchange
      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at exchange '{topic}' within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task PublishAsync_WithCompositeRouting_ChainsStrategiesCorrectlyAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - Chain NamespaceRoutingStrategy with PoolSuffixRoutingStrategy
    var composite = new CompositeTopicRoutingStrategy(
      new NamespaceRoutingStrategy(),
      new TestPoolSuffixRoutingStrategy("-01")
    );
    var messageType = typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated);

    // Get topic from composite strategy
    var topic = composite.ResolveTopic(messageType, "");

    // Verify topic is correct
    await Assert.That(topic).IsEqualTo("orders-01");

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      // Small delay to ensure consumer is fully registered
      await Task.Delay(500, cancellationToken);

      // Create and publish message
      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, new TransportDestination(topic), cancellationToken: cancellationToken);

      // Assert - Message should arrive at "orders-01" exchange
      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at exchange '{topic}' within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task PublishAsync_WithCustomRoutingFunction_UsesCustomLogicAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - Custom extraction logic
    var routingStrategy = new NamespaceRoutingStrategy(
      type => "custom-topic-" + type.Name.ToLowerInvariant()
    );
    var messageType = typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated);

    // Get topic from strategy
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Verify topic is correct
    await Assert.That(topic).IsEqualTo("custom-topic-ordercreated");

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      // Small delay to ensure consumer is fully registered
      await Task.Delay(500, cancellationToken);

      // Create and publish message
      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, new TransportDestination(topic), cancellationToken: cancellationToken);

      // Assert - Message should arrive at custom exchange
      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at exchange '{topic}' within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task PublishAsync_MultipleMessages_RouteToCorrectExchangesAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();

    // Define message types and their expected topics
    var testCases = new (Type MessageType, string ExpectedTopic)[] {
      (typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated), "orders"),
      (typeof(TestNamespaces.MyApp.Contracts.Commands.CreateOrder), "order"),
      (typeof(TestNamespaces.MyApp.Contracts.Events.OrderCreated), "order")
    };

    var receivedMessages = new Dictionary<string, TaskCompletionSource<bool>>();
    var subscriptions = new List<ISubscription>();

    try {
      // Set up consumers for each unique topic
      foreach (var (messageType, expectedTopic) in testCases) {
        if (!receivedMessages.ContainsKey(expectedTopic)) {
          receivedMessages[expectedTopic] = new TaskCompletionSource<bool>();

          var topicForClosure = expectedTopic;
          var subscription = await _transport!.SubscribeAsync(
            async (envelope, envelopeType, ct) => {
              receivedMessages[topicForClosure].TrySetResult(true);
              await Task.CompletedTask;
            },
            new TransportDestination(expectedTopic, $"test-queue-{expectedTopic}-{Guid.NewGuid():N}"),
            cancellationToken
          );
          subscriptions.Add(subscription);
        }
      }

      // Small delay to ensure consumers are fully registered
      await Task.Delay(500, cancellationToken);

      // Publish messages
      foreach (var (messageType, expectedTopic) in testCases) {
        var topic = routingStrategy.ResolveTopic(messageType, "");
        await Assert.That(topic).IsEqualTo(expectedTopic);

        var envelope = _createTestEnvelope();
        await _transport!.PublishAsync(envelope, new TransportDestination(topic), cancellationToken: cancellationToken);
      }

      // Assert - All messages should arrive at their expected exchanges
      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(10000);

      try {
        await Task.WhenAll(receivedMessages.Values.Select(tcs => tcs.Task.WaitAsync(timeoutCts.Token)));
      } catch (OperationCanceledException) {
        Assert.Fail("All messages should arrive at their correct exchanges within timeout");
      }
    } finally {
      foreach (var subscription in subscriptions) {
        subscription.Dispose();
      }
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task PublishAsync_WithEventsNamespace_StripsCreatedSuffixAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();
    var messageType = typeof(TestNamespaces.MyApp.Contracts.Events.OrderCreated);

    // Get topic from strategy
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Verify topic is correct (strips "Created" suffix)
    await Assert.That(topic).IsEqualTo("order");

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      // Small delay to ensure consumer is fully registered
      await Task.Delay(500, cancellationToken);

      // Create and publish message
      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, new TransportDestination(topic), cancellationToken: cancellationToken);

      // Assert
      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at exchange '{topic}' within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task TopicIsLowercase_OnRealTransportAsync() {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();
    var messageType = typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated);

    // Act
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Assert
    await Assert.That(topic).IsEqualTo(topic.ToLowerInvariant());
    await Assert.That(topic).IsEqualTo("orders");
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static async Task _initializeContainerAsync() {
    if (_initialized) {
      return;
    }

    await _initLock.WaitAsync();
    try {
      if (_initialized) {
        return;
      }

      Console.WriteLine("[NamespaceRoutingTransport] Starting RabbitMQ container...");

      _rabbitMqContainer = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

      await _rabbitMqContainer.StartAsync();

      Console.WriteLine($"[NamespaceRoutingTransport] RabbitMQ started: {_rabbitMqContainer.GetConnectionString()}");

      _initialized = true;
    } finally {
      _initLock.Release();
    }
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-content"),
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

  public ValueTask DisposeAsync() {
    // Fire-and-forget cleanup - CleanupAsync already handles per-test cleanup
    // This is just a fallback for any remaining resources
    var transport = _transport;
    var channelPool = _channelPool;
    var connection = _connection;

    _transport = null;
    _channelPool = null;
    _connection = null;

    _ = Task.Run(async () => {
      try {
        if (transport != null) {
          await transport.DisposeAsync();
        }

        channelPool?.Dispose();

        if (connection != null) {
          await connection.CloseAsync();
          connection.Dispose();
        }
      } catch {
        // Ignore cleanup errors
      }
    }, CancellationToken.None);

    return ValueTask.CompletedTask;
  }

  /// <summary>
  /// Pool suffix routing strategy for testing composite strategies.
  /// </summary>
  private sealed class TestPoolSuffixRoutingStrategy(string suffix) : ITopicRoutingStrategy {
    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
      return baseTopic + suffix;
    }
  }
}
