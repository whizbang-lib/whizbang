using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Integration.Tests.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Integration tests for NamespaceRoutingStrategy verifying end-to-end topic routing.
/// These tests verify that messages are routed to correct topics on the transport
/// based on their namespace structure.
/// </summary>
[Category("Integration")]
public class NamespaceRoutingIntegrationTests {
  // Stub work coordinator strategy for testing
  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public List<InboxMessage> QueuedInboxMessages { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) => QueuedInboxMessages.Add(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  // Stub envelope serializer for testing
  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonElement = System.Text.Json.JsonSerializer.SerializeToElement(new { });
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonElement,
        Hops = []
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!
      );
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) {
      throw new NotImplementedException("Not needed for routing tests");
    }
  }

  // ========================================
  // NAMESPACE ROUTING STRATEGY INTEGRATION TESTS
  // ========================================

  [Test]
  public async Task PublishAsync_WithNamespaceRouting_ReturnsFullNamespaceAsTopicAsync() {
    // Arrange - Event from TestNamespaces.MyApp.Orders.Events namespace
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new NamespaceRoutingStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var @event = new TestNamespaces.MyApp.Orders.Events.OrderCreated();

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Should route to full namespace in lowercase
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("testnamespaces.myapp.orders.events");
  }

  [Test]
  public async Task PublishAsync_WithNamespaceRouting_CommandNamespace_ReturnsFullNamespaceAsync() {
    // Arrange - Command from TestNamespaces.MyApp.Contracts.Commands namespace
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new NamespaceRoutingStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var command = new TestNamespaces.MyApp.Contracts.Commands.CreateOrder();

    // Act
    await dispatcher.SendAsync(command);

    // Assert - Should return full namespace
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("testnamespaces.myapp.contracts.commands");
  }

  [Test]
  public async Task PublishAsync_WithNamespaceRouting_EventNamespace_ReturnsFullNamespaceAsync() {
    // Arrange - Event from TestNamespaces.MyApp.Contracts.Events namespace
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new NamespaceRoutingStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var @event = new TestNamespaces.MyApp.Contracts.Events.OrderCreated();

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Should return full namespace
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("testnamespaces.myapp.contracts.events");
  }

  [Test]
  public async Task PublishAsync_WithNamespaceRouting_MessageNamespace_ReturnsFullNamespaceAsync() {
    // Arrange - Command from TestNamespaces.MyApp.Contracts.Messages namespace
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new NamespaceRoutingStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var command = new TestNamespaces.MyApp.Contracts.Messages.CreateOrderCommand();

    // Act
    await dispatcher.SendAsync(command);

    // Assert - Should return full namespace
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("testnamespaces.myapp.contracts.messages");
  }

  [Test]
  public async Task PublishAsync_WithNamespaceRouting_QueriesNamespace_ReturnsFullNamespaceAsync() {
    // Arrange - Query from TestNamespaces.MyApp.Contracts.Queries namespace
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new NamespaceRoutingStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var query = new TestNamespaces.MyApp.Contracts.Queries.GetOrderById();

    // Act
    await dispatcher.SendAsync(query);

    // Assert - Should return full namespace
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("testnamespaces.myapp.contracts.queries");
  }

  [Test]
  public async Task PublishAsync_WithNamespaceRouting_CompositeWithPoolSuffix_ChainsCorrectlyAsync() {
    // Arrange - Chain NamespaceRoutingStrategy with PoolSuffixRoutingStrategy
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new CompositeTopicRoutingStrategy(
        new NamespaceRoutingStrategy(),
        new PoolSuffixRoutingStrategy("-01")
    );
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var @event = new TestNamespaces.MyApp.Orders.Events.OrderCreated();

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Should be full namespace + "-01" from pool suffix
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("testnamespaces.myapp.orders.events-01");
  }

  [Test]
  public async Task PublishAsync_WithCustomNamespaceRouting_UsesCustomLogicAsync() {
    // Arrange - Custom extraction logic
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new NamespaceRoutingStrategy(type => "custom-topic-" + type.Name.ToLowerInvariant());
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var @event = new TestNamespaces.MyApp.Orders.Events.OrderCreated();

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Should use custom logic
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("custom-topic-ordercreated");
  }

  [Test]
  public async Task SendManyAsync_WithNamespaceRouting_RoutesAllMessagesToTheirNamespacesAsync() {
    // Arrange - Multiple messages from different namespaces
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new NamespaceRoutingStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var messages = new object[] {
      new TestNamespaces.MyApp.Orders.Events.OrderCreated(),
      new TestNamespaces.MyApp.Contracts.Commands.CreateOrder(),
      new TestNamespaces.MyApp.Contracts.Messages.CreateOrderCommand()
    };

    // Act
    await dispatcher.SendManyAsync(messages);

    // Assert - Each routed to its full namespace
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(3);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsEqualTo("testnamespaces.myapp.orders.events");
    await Assert.That(strategy.QueuedOutboxMessages[1].Destination).IsEqualTo("testnamespaces.myapp.contracts.commands");
    await Assert.That(strategy.QueuedOutboxMessages[2].Destination).IsEqualTo("testnamespaces.myapp.contracts.messages");
  }

  [Test]
  public async Task PublishAsync_WithNamespaceRouting_ReturnsLowercaseNamespaceAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var routingStrategy = new NamespaceRoutingStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy, routingStrategy);
    var @event = new TestNamespaces.MyApp.Orders.Events.OrderCreated();

    // Act
    await dispatcher.PublishAsync(@event);

    // Assert - Namespace should be lowercase
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    var destination = strategy.QueuedOutboxMessages[0].Destination;
    await Assert.That(destination).IsNotNull();
    await Assert.That(destination).IsEqualTo(destination.ToLowerInvariant());
    await Assert.That(destination).IsEqualTo("testnamespaces.myapp.orders.events");
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static IDispatcher _createDispatcherWithStrategy(
    IWorkCoordinatorStrategy strategy,
    ITopicRoutingStrategy routingStrategy
  ) {
    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton(routingStrategy);

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // Pool suffix strategy for composite tests
  private sealed class PoolSuffixRoutingStrategy(string suffix) : ITopicRoutingStrategy {
    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
      return baseTopic + suffix;
    }
  }
}
