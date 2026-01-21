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
/// Integration tests for IInboxRoutingStrategy and IOutboxRoutingStrategy implementations
/// with real RabbitMQ transport. Verifies that inbox/outbox strategies correctly route
/// messages through RabbitMQ exchanges and queues.
/// Uses Testcontainers for a real RabbitMQ instance.
/// </summary>
[Category("Integration")]
[NotInParallel]
public sealed class InboxOutboxRoutingIntegrationTests : IAsyncDisposable {
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
  // DOMAIN TOPIC OUTBOX STRATEGY TESTS
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task DomainTopicOutboxStrategy_PublishesToDomainExchangeAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var outboxStrategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Get destination from strategy
    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Verify destination is correct
    await Assert.That(destination.Address).IsEqualTo("orders");
    await Assert.That(destination.RoutingKey).IsEqualTo("ordercreated");

    // Set up consumer to verify message arrival at domain exchange
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(destination.Address, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      // Publish message using strategy destination
      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, destination, cancellationToken: cancellationToken);

      // Assert - Message should arrive at "orders" exchange
      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at exchange '{destination.Address}' within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task DomainTopicOutboxStrategy_WithCustomResolver_RoutesCorrectlyAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - Custom topic resolver returns "custom-orders"
    var outboxStrategy = new DomainTopicOutboxStrategy(
      new NamespaceRoutingStrategy(type => "custom-orders")
    );
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    await Assert.That(destination.Address).IsEqualTo("custom-orders");

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(destination.Address, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, destination, cancellationToken: cancellationToken);

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at custom exchange '{destination.Address}' within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // SHARED TOPIC OUTBOX STRATEGY TESTS
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task SharedTopicOutboxStrategy_PublishesToSharedExchangeAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var outboxStrategy = new SharedTopicOutboxStrategy("shared.events");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Verify destination uses shared topic
    await Assert.That(destination.Address).IsEqualTo("shared.events");
    await Assert.That(destination.RoutingKey).IsEqualTo("orders.ordercreated");
    await Assert.That(destination.Metadata is not null).IsTrue();

    // Set up consumer on shared exchange with routing key
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(destination.Address, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, destination, cancellationToken: cancellationToken);

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at shared exchange '{destination.Address}' within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task SharedTopicOutboxStrategy_UsesDefaultTopicAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - Use default topic
    var outboxStrategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Default topic is "whizbang.events"
    await Assert.That(destination.Address).IsEqualTo("whizbang.events");

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var subscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(destination.Address, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, destination, cancellationToken: cancellationToken);

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail("Message should arrive at default shared exchange within timeout");
      }
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // DOMAIN TOPIC INBOX STRATEGY TESTS
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task DomainTopicInboxStrategy_SubscribesToDomainExchangeAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var inboxStrategy = new DomainTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var subscription = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    // Verify subscription is correct
    await Assert.That(subscription.Topic).IsEqualTo("orders.inbox");
    await Assert.That(subscription.FilterExpression).IsNull(); // No filter for domain topics

    // Set up consumer on domain inbox
    var receivedTcs = new TaskCompletionSource<string>();
    var transportSubscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(subscription.Topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      // Publish command to domain inbox
      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(
        envelope,
        new TransportDestination(subscription.Topic),
        cancellationToken: cancellationToken
      );

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at domain inbox '{subscription.Topic}' within timeout");
      }
    } finally {
      transportSubscription.Dispose();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task DomainTopicInboxStrategy_WithCustomSuffix_SubscribesCorrectlyAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - Custom suffix ".in"
    var inboxStrategy = new DomainTopicInboxStrategy(".in");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var subscription = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    await Assert.That(subscription.Topic).IsEqualTo("orders.in");

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var transportSubscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(subscription.Topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(
        envelope,
        new TransportDestination(subscription.Topic),
        cancellationToken: cancellationToken
      );

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at custom inbox '{subscription.Topic}' within timeout");
      }
    } finally {
      transportSubscription.Dispose();
    }
  }

  // ========================================
  // SHARED TOPIC INBOX STRATEGY TESTS
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task SharedTopicInboxStrategy_SubscribesToSharedExchangeAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange
    var inboxStrategy = new SharedTopicInboxStrategy("shared.inbox");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "inventory" };

    var subscription = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    // Verify subscription is correct
    await Assert.That(subscription.Topic).IsEqualTo("shared.inbox");
    await Assert.That(subscription.FilterExpression).IsEqualTo("orders,inventory");
    await Assert.That(subscription.Metadata is not null).IsTrue();

    // Set up consumer on shared inbox
    var receivedTcs = new TaskCompletionSource<string>();
    var transportSubscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(subscription.Topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(
        envelope,
        new TransportDestination(subscription.Topic),
        cancellationToken: cancellationToken
      );

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail($"Message should arrive at shared inbox '{subscription.Topic}' within timeout");
      }
    } finally {
      transportSubscription.Dispose();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task SharedTopicInboxStrategy_UsesDefaultTopicAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - Use default topic
    var inboxStrategy = new SharedTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var subscription = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    // Default topic is "whizbang.inbox"
    await Assert.That(subscription.Topic).IsEqualTo("whizbang.inbox");

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var transportSubscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(subscription.Topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(
        envelope,
        new TransportDestination(subscription.Topic),
        cancellationToken: cancellationToken
      );

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsNotNull();
      } catch (OperationCanceledException) {
        Assert.Fail("Message should arrive at default shared inbox within timeout");
      }
    } finally {
      transportSubscription.Dispose();
    }
  }

  // ========================================
  // END-TO-END STRATEGY COMBINATION TESTS
  // ========================================

  [Test]
  [Timeout(30000)]
  public async Task DomainOutbox_ToDomainInbox_EndToEndAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - Publisher uses DomainTopicOutboxStrategy, Subscriber uses DomainTopicInboxStrategy
    var outboxStrategy = new DomainTopicOutboxStrategy();
    var inboxStrategy = new DomainTopicInboxStrategy(".outbox"); // Subscribe to outbox topic
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Get destination from outbox strategy
    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Set up consumer using inbox strategy (subscribing to domain topic)
    var receivedTcs = new TaskCompletionSource<string>();
    var transportSubscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(destination.Address, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      // Publish using outbox strategy destination
      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, destination, cancellationToken: cancellationToken);

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsEqualTo(envelope.MessageId.ToString());
      } catch (OperationCanceledException) {
        Assert.Fail("End-to-end domain routing should work within timeout");
      }
    } finally {
      transportSubscription.Dispose();
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task SharedOutbox_ToSharedInbox_EndToEndAsync(
    CancellationToken cancellationToken
  ) {
    // Arrange - Both use shared topic strategy
    var sharedTopic = "test.shared";
    var outboxStrategy = new SharedTopicOutboxStrategy(sharedTopic);
    var inboxStrategy = new SharedTopicInboxStrategy(sharedTopic);
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    var subscription = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    // Verify both strategies use the same shared topic
    await Assert.That(destination.Address).IsEqualTo(subscription.Topic);

    // Set up consumer
    var receivedTcs = new TaskCompletionSource<string>();
    var transportSubscription = await _transport!.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        receivedTcs.TrySetResult(envelope.MessageId.ToString());
        await Task.CompletedTask;
      },
      new TransportDestination(subscription.Topic, $"test-queue-{Guid.NewGuid():N}"),
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      var envelope = _createTestEnvelope();
      await _transport.PublishAsync(envelope, destination, cancellationToken: cancellationToken);

      using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutCts.CancelAfter(5000);

      try {
        var receivedMessageId = await receivedTcs.Task.WaitAsync(timeoutCts.Token);
        await Assert.That(receivedMessageId).IsEqualTo(envelope.MessageId.ToString());
      } catch (OperationCanceledException) {
        Assert.Fail("End-to-end shared topic routing should work within timeout");
      }
    } finally {
      transportSubscription.Dispose();
    }
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

      Console.WriteLine("[InboxOutboxRouting] Starting RabbitMQ container...");

      _rabbitMqContainer = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

      await _rabbitMqContainer.StartAsync();

      Console.WriteLine($"[InboxOutboxRouting] RabbitMQ started: {_rabbitMqContainer.GetConnectionString()}");

      _initialized = true;
    } finally {
      _initLock.Release();
    }
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-inbox-outbox-content"),
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
}
