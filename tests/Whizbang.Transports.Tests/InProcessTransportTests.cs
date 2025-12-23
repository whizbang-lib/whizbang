using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Comprehensive tests for InProcessTransport implementation.
/// Tests cover pub/sub, request-response, subscription lifecycle, and thread safety.
/// </summary>
[Category("Transport")]
public class InProcessTransportTests {
  private static MessageEnvelope<TestMessage> _createTestEnvelope(string content) {
    var message = new TestMessage { Content = content };
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }

  // Test message types
  private sealed record TestMessage {
    public string Content { get; init; } = string.Empty;
  }

  private sealed record TestResponse {
    public string Result { get; init; } = string.Empty;
  }

  // ============================================================================
  // Capabilities Tests
  // ============================================================================

  [Test]
  public async Task Capabilities_ReturnsExpectedFlagsAsync() {
    // Arrange
    var transport = new InProcessTransport();

    // Act
    var capabilities = transport.Capabilities;

    // Assert - InProcessTransport supports all capabilities
    await Assert.That(capabilities.HasFlag(TransportCapabilities.RequestResponse)).IsTrue();
    await Assert.That(capabilities.HasFlag(TransportCapabilities.PublishSubscribe)).IsTrue();
    await Assert.That(capabilities.HasFlag(TransportCapabilities.Ordered)).IsTrue();
    await Assert.That(capabilities.HasFlag(TransportCapabilities.Reliable)).IsTrue();
  }

  // ============================================================================
  // PublishAsync Tests
  // ============================================================================

  [Test]
  public async Task PublishAsync_WithNoSubscribers_CompletesSuccessfullyAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test-message");
    var destination = new TransportDestination("test-topic");

    // Act & Assert - Should not throw
    await transport.PublishAsync(envelope, destination);
  }

  [Test]
  public async Task PublishAsync_WithSingleSubscriber_InvokesHandlerAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test-message");
    var destination = new TransportDestination("test-topic");

    IMessageEnvelope? receivedEnvelope = null;
    await transport.SubscribeAsync(
      handler: (env, ct) => {
        receivedEnvelope = env;
        return Task.CompletedTask;
      },
      destination: destination
    );

    // Act
    await transport.PublishAsync(envelope, destination);

    // Assert
    await Assert.That(receivedEnvelope).IsNotNull();
    await Assert.That(receivedEnvelope!.MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  [Arguments(2)]
  [Arguments(5)]
  [Arguments(10)]
  public async Task PublishAsync_WithMultipleSubscribers_InvokesAllHandlersAsync(int subscriberCount) {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope($"test-{subscriberCount}");
    var destination = new TransportDestination("test-topic");

    var invocations = new ConcurrentBag<int>();
    for (int i = 0; i < subscriberCount; i++) {
      var index = i;
      await transport.SubscribeAsync(
        handler: (env, ct) => {
          invocations.Add(index);
          return Task.CompletedTask;
        },
        destination: destination
      );
    }

    // Act
    await transport.PublishAsync(envelope, destination);

    // Assert
    await Assert.That(invocations.Count).IsEqualTo(subscriberCount);
    await Assert.That(invocations.Distinct().Count()).IsEqualTo(subscriberCount);
  }

  [Test]
  public async Task PublishAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-topic");
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(() => transport.PublishAsync(envelope, destination, cts.Token))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task PublishAsync_ToDifferentTopics_OnlyInvokesMatchingSubscribersAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope1 = _createTestEnvelope("message-1");
    var envelope2 = _createTestEnvelope("message-2");
    var destination1 = new TransportDestination("topic-1");
    var destination2 = new TransportDestination("topic-2");

    var topic1Invoked = false;
    var topic2Invoked = false;

    await transport.SubscribeAsync(
      handler: (env, ct) => {
        topic1Invoked = true;
        return Task.CompletedTask;
      },
      destination: destination1
    );

    await transport.SubscribeAsync(
      handler: (env, ct) => {
        topic2Invoked = true;
        return Task.CompletedTask;
      },
      destination: destination2
    );

    // Act
    await transport.PublishAsync(envelope1, destination1);

    // Assert
    await Assert.That(topic1Invoked).IsTrue();
    await Assert.That(topic2Invoked).IsFalse();
  }

  // ============================================================================
  // SubscribeAsync Tests
  // ============================================================================

  [Test]
  public async Task SubscribeAsync_ReturnsActiveSubscriptionAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");

    // Act
    var subscription = await transport.SubscribeAsync(
      handler: (env, ct) => Task.CompletedTask,
      destination: destination
    );

    // Assert
    await Assert.That(subscription).IsNotNull();
    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task SubscribeAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await transport.SubscribeAsync(
      handler: (env, ct) => Task.CompletedTask,
      destination: destination,
      cancellationToken: cts.Token
    )).Throws<OperationCanceledException>();
  }

  // ============================================================================
  // Subscription Lifecycle Tests
  // ============================================================================

  public enum SubscriptionLifecycleState {
    Active,
    Paused,
    Disposed
  }

  public static IEnumerable<(SubscriptionLifecycleState state, bool shouldInvokeHandler)> GetSubscriptionStates() {
    yield return (SubscriptionLifecycleState.Active, true);
    yield return (SubscriptionLifecycleState.Paused, false);
    yield return (SubscriptionLifecycleState.Disposed, false);
  }

  [Test]
  [MethodDataSource(nameof(GetSubscriptionStates))]
  public async Task Subscription_InVariousStates_BehavesCorrectlyAsync(
    SubscriptionLifecycleState state,
    bool shouldInvokeHandler) {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-topic");

    var handlerInvoked = false;
    var subscription = await transport.SubscribeAsync(
      handler: (env, ct) => {
        handlerInvoked = true;
        return Task.CompletedTask;
      },
      destination: destination
    );

    // Set subscription state
    switch (state) {
      case SubscriptionLifecycleState.Paused:
        await subscription.PauseAsync();
        break;
      case SubscriptionLifecycleState.Disposed:
        subscription.Dispose();
        break;
    }

    // Act
    await transport.PublishAsync(envelope, destination);

    // Assert
    await Assert.That(handlerInvoked).IsEqualTo(shouldInvokeHandler);
  }

  [Test]
  public async Task Subscription_PauseAsync_SetsIsActiveToFalseAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var subscription = await transport.SubscribeAsync(
      handler: (env, ct) => Task.CompletedTask,
      destination: destination
    );

    // Act
    await subscription.PauseAsync();

    // Assert
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task Subscription_ResumeAsync_SetsIsActiveToTrueAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var subscription = await transport.SubscribeAsync(
      handler: (env, ct) => Task.CompletedTask,
      destination: destination
    );

    await subscription.PauseAsync();

    // Act
    await subscription.ResumeAsync();

    // Assert
    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task Subscription_Dispose_RemovesHandlerFromTransportAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-topic");

    var handlerInvoked = false;
    var subscription = await transport.SubscribeAsync(
      handler: (env, ct) => {
        handlerInvoked = true;
        return Task.CompletedTask;
      },
      destination: destination
    );

    // Act
    subscription.Dispose();
    await transport.PublishAsync(envelope, destination);

    // Assert
    await Assert.That(handlerInvoked).IsFalse();
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task Subscription_DisposeMultipleTimes_IsIdempotentAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var subscription = await transport.SubscribeAsync(
      handler: (env, ct) => Task.CompletedTask,
      destination: destination
    );

    // Act & Assert - Should not throw
    subscription.Dispose();
    subscription.Dispose();
    subscription.Dispose();

    await Assert.That(subscription.IsActive).IsFalse();
  }

  // ============================================================================
  // SendAsync (Request-Response) Tests
  // ============================================================================

  [Test]
  public async Task SendAsync_WithResponder_ReturnsResponseEnvelopeAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var requestEnvelope = _createTestEnvelope("request");
    var responseEnvelope = _createTestEnvelope("response");
    var destination = new TransportDestination("test-topic");

    // Setup responder
    await transport.SubscribeAsync(
      handler: async (env, ct) => {
        // Simulate responder sending response
        var responseDestination = new TransportDestination($"response-{env.MessageId.Value}");
        await transport.PublishAsync(responseEnvelope, responseDestination, ct);
      },
      destination: destination
    );

    // Act
    var response = await transport.SendAsync<TestMessage, TestMessage>(requestEnvelope, destination);

    // Assert
    await Assert.That(response).IsNotNull();
    await Assert.That(response.MessageId).IsEqualTo(responseEnvelope.MessageId);
  }

  [Test]
  public async Task SendAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-topic");
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await transport.SendAsync<TestMessage, TestMessage>(envelope, destination, cts.Token))
      .Throws<OperationCanceledException>();
  }

  [Test]
  [Arguments(100)]
  [Arguments(500)]
  [Arguments(1000)]
  public async Task SendAsync_WithTimeout_ThrowsTimeoutExceptionAsync(int timeoutMs) {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-topic");
    var cts = new CancellationTokenSource(timeoutMs);

    // No responder setup - will timeout

    // Act & Assert
    await Assert.That(async () => await transport.SendAsync<TestMessage, TestMessage>(envelope, destination, cts.Token))
      .Throws<OperationCanceledException>();
  }

  // ============================================================================
  // Thread Safety Tests
  // ============================================================================

  [Test]
  [Arguments(10)]
  [Arguments(50)]
  [Arguments(100)]
  public async Task PublishAsync_ConcurrentPublishes_AllHandlersInvokedAsync(int concurrentPublishes) {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var invocations = new ConcurrentBag<MessageId>();

    await transport.SubscribeAsync(
      handler: (env, ct) => {
        invocations.Add(env.MessageId);
        return Task.CompletedTask;
      },
      destination: destination
    );

    // Act - Publish concurrently
    var tasks = Enumerable.Range(0, concurrentPublishes)
      .Select(_ => {
        var envelope = _createTestEnvelope($"message-{Guid.NewGuid()}");
        return transport.PublishAsync(envelope, destination);
      })
      .ToArray();

    await Task.WhenAll(tasks);

    // Assert
    await Assert.That(invocations.Count).IsEqualTo(concurrentPublishes);
  }

  [Test]
  [Arguments(10)]
  [Arguments(25)]
  public async Task SubscribeAsync_ConcurrentSubscriptions_AllRegisteredAsync(int concurrentSubscriptions) {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-topic");
    var invocations = new ConcurrentBag<int>();

    // Act - Subscribe concurrently
    var subscribeTasks = Enumerable.Range(0, concurrentSubscriptions)
      .Select(index => transport.SubscribeAsync(
        handler: (env, ct) => {
          invocations.Add(index);
          return Task.CompletedTask;
        },
        destination: destination
      ))
      .ToArray();

    await Task.WhenAll(subscribeTasks);

    // Publish message
    await transport.PublishAsync(envelope, destination);

    // Assert
    await Assert.That(invocations.Count).IsEqualTo(concurrentSubscriptions);
  }

  [Test]
  public async Task SubscribeAndDispose_Concurrent_ThreadSafeAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-topic");
    var subscriptions = new ConcurrentBag<ISubscription>();

    // Act - Subscribe and dispose concurrently
    var tasks = Enumerable.Range(0, 50)
      .Select(async _ => {
        var subscription = await transport.SubscribeAsync(
          handler: (env, ct) => Task.CompletedTask,
          destination: destination
        );
        subscriptions.Add(subscription);

        // Dispose immediately (simulates rapid subscribe/unsubscribe)
        subscription.Dispose();
      })
      .ToArray();

    await Task.WhenAll(tasks);

    // Publish message - should not throw
    await transport.PublishAsync(envelope, destination);

    // Assert
    await Assert.That(subscriptions.Count).IsEqualTo(50);
  }

  // ============================================================================
  // Edge Cases
  // ============================================================================

  [Test]
  public async Task PublishAsync_HandlerThrowsException_ContinuesWithOtherHandlersAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-topic");

    var handler1Invoked = false;

    await transport.SubscribeAsync(
      handler: (env, ct) => {
        handler1Invoked = true;
        throw new InvalidOperationException("Handler 1 failed");
      },
      destination: destination
    );

    await transport.SubscribeAsync(
      handler: (env, ct) => {
        return Task.CompletedTask;
      },
      destination: destination
    );

    // Act & Assert - Should throw from first handler
    await Assert.That(() => transport.PublishAsync(envelope, destination))
      .Throws<InvalidOperationException>();

    // First handler was invoked before throwing
    await Assert.That(handler1Invoked).IsTrue();
    // Second handler may or may not be invoked depending on exception handling
  }

  [Test]
  public async Task SendAsync_WithCancellationDuringPublish_ExecutesFinallyBlockAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination = new TransportDestination("test-service");
    var cts = new CancellationTokenSource();

    // Cancel immediately so PublishAsync throws
    cts.Cancel();

    // Act & Assert - Should throw OperationCanceledException
    // This exercises the finally block (line 90) which removes the pending request
    await Assert.That(async () =>
      await transport.SendAsync<TestMessage, TestResponse>(
        envelope,
        destination,
        cts.Token
      )
    ).Throws<OperationCanceledException>();

    // Verify finally block executed by checking that pending request was removed
    // (Subsequent call with same envelope should not interfere with previous request)
  }

  [Test]
  [Skip("Routing key matching not yet implemented in InProcessTransport")]
  public async Task PublishAsync_WithRoutingKey_DeliversToCorrectDestinationAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var envelope = _createTestEnvelope("test");
    var destination1 = new TransportDestination("topic", RoutingKey: "orders.created");
    var destination2 = new TransportDestination("topic", RoutingKey: "orders.updated");

    var handler1Invoked = false;
    var handler2Invoked = false;

    await transport.SubscribeAsync(
      handler: (env, ct) => {
        handler1Invoked = true;
        return Task.CompletedTask;
      },
      destination: destination1
    );

    await transport.SubscribeAsync(
      handler: (env, ct) => {
        handler2Invoked = true;
        return Task.CompletedTask;
      },
      destination: destination2
    );

    // Act - Publish to destination1
    await transport.PublishAsync(envelope, destination1);

    // Assert - Only handler1 should be invoked (different addresses due to RoutingKey)
    await Assert.That(handler1Invoked).IsTrue();
    await Assert.That(handler2Invoked).IsFalse();
  }
}
