using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.Tests.Generated;
using Whizbang.Transports.Tests.Generated;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for DispatcherTransportBridge - a component that connects IDispatcher with ITransport.
/// Following TDD: These tests are written BEFORE implementing the bridge.
/// All tests should FAIL initially (RED phase), then pass after implementation (GREEN phase).
///
/// Architecture:
/// - IDispatcher remains pure (no transport concerns)
/// - DispatcherTransportBridge handles transport integration
/// - Bridge serializes/deserializes messages
/// - Bridge routes incoming transport messages to local dispatcher
/// - Bridge publishes outgoing messages to transport destinations
/// </summary>
public class DispatcherTransportBridgeTests {
  [Test]
  public async Task PublishToTransportAsync_WithMessage_DeliversToRemoteDestinationAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var dispatcher = CreateTestDispatcher();
    var bridge = new DispatcherTransportBridge(dispatcher, transport, serializer);
    var destination = new TransportDestination("remote-service");

    var messageReceived = false;
    IMessageEnvelope? receivedEnvelope = null;

    // Subscribe to the destination to simulate remote service
    await transport.SubscribeAsync(
      handler: (envelope, ct) => {
        messageReceived = true;
        receivedEnvelope = envelope;
        return Task.CompletedTask;
      },
      destination: destination
    );

    var message = new TestCommand { Value = 42 };

    // Act - Publish message to transport via bridge
    await bridge.PublishToTransportAsync(message, destination);

    // Assert - Message was delivered to transport destination
    await Assert.That(messageReceived).IsTrue();
    await Assert.That(receivedEnvelope).IsNotNull();

    // Verify payload was preserved
    if (receivedEnvelope != null) {
      var typedEnvelope = (MessageEnvelope<TestCommand>)receivedEnvelope;
      await Assert.That(typedEnvelope.Payload.Value).IsEqualTo(42);
    }
  }

  [Test]
  public async Task PublishToTransportAsync_AutomaticallySerializesMessageAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var dispatcher = CreateTestDispatcher();
    var bridge = new DispatcherTransportBridge(dispatcher, transport, serializer);
    var destination = new TransportDestination("remote-service");

    byte[]? serializedBytes = null;

    await transport.SubscribeAsync(
      handler: async (envelope, ct) => {
        // Verify serialization works
        serializedBytes = await serializer.SerializeAsync(envelope);
        var deserialized = await serializer.DeserializeAsync<TestCommand>(serializedBytes);
        await Assert.That(deserialized).IsNotNull();
      },
      destination: destination
    );

    var message = new TestCommand { Value = 42, Name = "Test" };

    // Act
    await bridge.PublishToTransportAsync(message, destination);

    // Assert - Serialization occurred
    await Assert.That(serializedBytes).IsNotNull();
    await Assert.That(serializedBytes!.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task SendToTransportAsync_WithRequestResponse_ReturnsTypedResponseAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var dispatcher = CreateTestDispatcher();
    var bridge = new DispatcherTransportBridge(dispatcher, transport, serializer);
    var destination = new TransportDestination("remote-calculator");

    // Setup remote responder (simulates remote service)
    await transport.SubscribeAsync(
      handler: async (requestEnvelope, ct) => {
        var request = ((MessageEnvelope<TestQuery>)requestEnvelope).Payload;
        var response = new TestResult { Result = request.Value * 2 };

        var responseEnvelope = new MessageEnvelope<TestResult> {
          MessageId = MessageId.New(),
          Payload = response,
          Hops = new List<MessageHop> {
            new MessageHop {
              ServiceName = "RemoteCalculator",
              Timestamp = DateTimeOffset.UtcNow,
              CorrelationId = requestEnvelope.GetCorrelationId(),
              CausationId = requestEnvelope.MessageId
            }
          }
        };

        var responseDestination = new TransportDestination($"response-{requestEnvelope.MessageId.Value}");
        await transport.PublishAsync(responseEnvelope, responseDestination, ct);
      },
      destination: destination
    );

    var query = new TestQuery { Value = 21 };

    // Act - Send request and wait for response
    var response = await bridge.SendToTransportAsync<TestQuery, TestResult>(query, destination);

    // Assert - Got typed response from remote receptor
    await Assert.That(response).IsNotNull();
    await Assert.That(response.Result).IsEqualTo(42);
  }

  [Test]
  public async Task SubscribeFromTransportAsync_RoutesIncomingMessagesToDispatcherAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var dispatcher = CreateTestDispatcher();
    var bridge = new DispatcherTransportBridge(dispatcher, transport, serializer);
    var destination = new TransportDestination("local-commands");

    var dispatcherInvoked = false;

    // Configure test dispatcher to track invocations
    dispatcher.OnSendAsync = (msg) => {
      dispatcherInvoked = true;
      return Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Delivered(
        MessageId.New(),
        "test",
        CorrelationId.New(),
        MessageId.New()
      ));
    };

    // Subscribe bridge to transport - incoming messages should route to dispatcher
    await bridge.SubscribeFromTransportAsync<TestCommand>(destination);

    // Simulate remote sender publishing to transport
    var message = new TestCommand { Value = 99 };
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "RemoteSender",
          Timestamp = DateTimeOffset.UtcNow
        }
      }
    };

    // Act - Publish to transport (simulates remote send)
    await transport.PublishAsync(envelope, destination, CancellationToken.None);

    // Wait a bit for async processing
    await Task.Delay(100);

    // Assert - Dispatcher was invoked with the message
    await Assert.That(dispatcherInvoked).IsTrue();
  }

  [Test]
  public async Task SubscribeFromTransportAsync_DeserializesAndInvokesLocalReceptorAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var dispatcher = CreateTestDispatcher();
    var bridge = new DispatcherTransportBridge(dispatcher, transport, serializer);
    var destination = new TransportDestination("local-commands");

    TestCommand? receivedMessage = null;

    // Configure test dispatcher to capture the message
    dispatcher.OnSendAsync = (msg) => {
      receivedMessage = msg as TestCommand;
      return Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Delivered(
        MessageId.New(),
        "test",
        CorrelationId.New(),
        MessageId.New()
      ));
    };

    // Subscribe bridge to transport
    await bridge.SubscribeFromTransportAsync<TestCommand>(destination);

    // Create and serialize envelope
    var message = new TestCommand { Value = 123, Name = "TestMessage" };
    var envelope = new MessageEnvelope<TestCommand> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = new List<MessageHop> {
        new MessageHop {
          ServiceName = "RemoteSender",
          Timestamp = DateTimeOffset.UtcNow
        }
      }
    };

    // Act - Publish serialized envelope to transport
    await transport.PublishAsync(envelope, destination, CancellationToken.None);
    await Task.Delay(100);

    // Assert - Message was deserialized and passed to dispatcher
    await Assert.That(receivedMessage).IsNotNull();
    if (receivedMessage != null) {
      await Assert.That(receivedMessage.Value).IsEqualTo(123);
      await Assert.That(receivedMessage.Name).IsEqualTo("TestMessage");
    }
  }

  [Test]
  public async Task PublishToTransportAsync_PreservesCorrelationIdAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var dispatcher = CreateTestDispatcher();
    var bridge = new DispatcherTransportBridge(dispatcher, transport, serializer);
    var destination = new TransportDestination("remote-service");
    var correlationId = CorrelationId.New();

    IMessageEnvelope? receivedEnvelope = null;
    await transport.SubscribeAsync(
      handler: (envelope, ct) => {
        receivedEnvelope = envelope;
        return Task.CompletedTask;
      },
      destination: destination
    );

    var message = new TestCommand { Value = 42 };
    var context = new MessageContext {
      CorrelationId = correlationId,
      CausationId = MessageId.New()
    };

    // Act - Publish with explicit context
    await bridge.PublishToTransportAsync(message, destination, context);

    // Assert - CorrelationId was preserved
    await Assert.That(receivedEnvelope).IsNotNull();
    if (receivedEnvelope != null) {
      await Assert.That(receivedEnvelope.GetCorrelationId()).IsEqualTo(correlationId);
    }
  }

  [Test]
  public async Task PublishToTransportAsync_CreatesEnvelopeWithHopAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var dispatcher = CreateTestDispatcher();
    var bridge = new DispatcherTransportBridge(dispatcher, transport, serializer);
    var destination = new TransportDestination("remote-service");

    IMessageEnvelope? receivedEnvelope = null;
    await transport.SubscribeAsync(
      handler: (envelope, ct) => {
        receivedEnvelope = envelope;
        return Task.CompletedTask;
      },
      destination: destination
    );

    var message = new TestCommand { Value = 42 };

    // Act
    await bridge.PublishToTransportAsync(message, destination);

    // Assert - Envelope has at least one hop
    await Assert.That(receivedEnvelope).IsNotNull();
    if (receivedEnvelope != null) {
      await Assert.That(receivedEnvelope.Hops).HasCount().GreaterThanOrEqualTo(1);
      await Assert.That(receivedEnvelope.Hops[0].ServiceName).IsNotNull();
    }
  }

  [Test]
  public async Task SendToTransportAsync_WithExplicitContext_PreservesCorrelationIdAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var options = WhizbangJsonContext.CreateOptions();
    var serializer = new JsonMessageSerializer(options);
    var dispatcher = CreateTestDispatcher();
    var bridge = new DispatcherTransportBridge(dispatcher, transport, serializer);
    var destination = new TransportDestination("remote-calculator");
    var correlationId = CorrelationId.New();

    IMessageEnvelope? receivedRequest = null;

    // Setup remote responder
    await transport.SubscribeAsync(
      handler: async (requestEnvelope, ct) => {
        receivedRequest = requestEnvelope;
        var request = ((MessageEnvelope<TestQuery>)requestEnvelope).Payload;
        var response = new TestResult { Result = request.Value * 2 };

        var responseEnvelope = new MessageEnvelope<TestResult> {
          MessageId = MessageId.New(),
          Payload = response,
          Hops = new List<MessageHop> {
            new MessageHop {
              ServiceName = "RemoteCalculator",
              Timestamp = DateTimeOffset.UtcNow,
              CorrelationId = requestEnvelope.GetCorrelationId(),
              CausationId = requestEnvelope.MessageId
            }
          }
        };

        var responseDestination = new TransportDestination($"response-{requestEnvelope.MessageId.Value}");
        await transport.PublishAsync(responseEnvelope, responseDestination, ct);
      },
      destination: destination
    );

    var query = new TestQuery { Value = 21 };
    var context = new MessageContext {
      CorrelationId = correlationId,
      CausationId = MessageId.New()
    };

    // Act - Send request with explicit context
    var response = await bridge.SendToTransportAsync<TestQuery, TestResult>(query, destination, context);

    // Assert - Got typed response and correlationId was preserved
    await Assert.That(response).IsNotNull();
    await Assert.That(response.Result).IsEqualTo(42);
    await Assert.That(receivedRequest).IsNotNull();
    if (receivedRequest != null) {
      await Assert.That(receivedRequest.GetCorrelationId()).IsEqualTo(correlationId);
    }
  }

  // Helper methods
  private TestDispatcher CreateTestDispatcher() {
    var serviceProvider = new TestServiceProvider();
    return new TestDispatcher(serviceProvider);
  }

  // Test message types
  public record TestCommand : IEvent {
    public int Value { get; init; }
    public string Name { get; init; } = string.Empty;
  }

  public record TestQuery : IEvent {
    public int Value { get; init; }
  }

  public record TestResult : IEvent {
    public int Result { get; init; }
  }

  // Test dispatcher with hooks for verification
  private class TestDispatcher : Dispatcher {
    public Func<object, Task<IDeliveryReceipt>>? OnSendAsync { get; set; }

    public TestDispatcher(IServiceProvider serviceProvider) : base(serviceProvider) { }

    protected override ReceptorInvoker<TResult>? _getReceptorInvoker<TResult>(object message, Type messageType) {
      // For testing, we can hook into the receptor invocation
      // SendAsync calls this method to get the invoker
      if (OnSendAsync != null && typeof(TResult) == typeof(object)) {
        return async (msg) => {
          await OnSendAsync(msg);
          return (TResult)(object)Task.CompletedTask;
        };
      }
      return null;
    }

    protected override VoidReceptorInvoker? _getVoidReceptorInvoker(object message, Type messageType) {
      // For testing, track that void receptor was called
      if (OnSendAsync != null) {
        return (msg) => {
          _ = OnSendAsync(msg);
          return ValueTask.CompletedTask;
        };
      }
      return null;
    }

    protected override ReceptorPublisher<TEvent> _getReceptorPublisher<TEvent>(TEvent @event, Type eventType) {
      return async (evt) => { await Task.CompletedTask; };
    }
  }

  private class TestServiceProvider : IServiceProvider {
    public object? GetService(Type serviceType) {
      return null;
    }
  }
}
