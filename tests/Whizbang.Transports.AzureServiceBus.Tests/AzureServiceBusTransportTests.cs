using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Transport;
using Whizbang.Transports.AzureServiceBus.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Integration tests for AzureServiceBusTransport.
/// Azure Service Bus SDK uses sealed classes, so these tests use the real emulator.
/// Tests verify transport initialization, publish/subscribe, and lifecycle management.
/// </summary>
[Timeout(240_000)] // 240s timeout for integration tests (emulator initialization ~72s + test execution)
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class AzureServiceBusTransportTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  [Test]
  public async Task Capabilities_DefaultOptions_ReturnsPublishSubscribeReliableWithoutOrderedAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    // Act
    var capabilities = transport.Capabilities;

    // Assert - Without EnableSessions, Ordered is NOT claimed
    await Assert.That((capabilities & TransportCapabilities.PublishSubscribe) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.Reliable) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.BulkPublish) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.Ordered) != 0).IsFalse()
      .Because("Ordered requires EnableSessions = true");
  }

  [Test]
  public async Task Capabilities_WithEnableSessions_ReturnsOrderedAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    var options = new AzureServiceBusOptions { EnableSessions = true };
    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions,
      options
    );

    // Act
    var capabilities = transport.Capabilities;

    // Assert - With EnableSessions, Ordered IS claimed
    await Assert.That((capabilities & TransportCapabilities.PublishSubscribe) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.Reliable) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.BulkPublish) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.Ordered) != 0).IsTrue();
  }

  [Test]
  public async Task IsInitialized_ReturnsFalse_BeforeInitializeAsyncAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    // Act & Assert
    await Assert.That(transport.IsInitialized).IsFalse();
  }

  [Test]
  public async Task IsInitialized_ReturnsTrue_AfterInitializeAsyncAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    // Act
    await transport.InitializeAsync();

    // Assert
    await Assert.That(transport.IsInitialized).IsTrue();
  }

  [Test]
  public async Task InitializeAsync_IsIdempotentAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    // Act - Call InitializeAsync multiple times
    await transport.InitializeAsync();
    await transport.InitializeAsync();
    await transport.InitializeAsync();

    // Assert - Should still be initialized without errors
    await Assert.That(transport.IsInitialized).IsTrue();
  }

  [Test]
  public async Task PublishAsync_WithValidMessage_SendsToTopicAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = TestJsonContext.Default
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("topic-00");

    // Drain any existing messages first
    await _drainMessagesAsync("topic-00", "sub-00-a");

    // Act
    await transport.PublishAsync(envelope, destination);

    // Assert - Verify message arrived by receiving it
    var receiver = _fixture.Client.CreateReceiver("topic-00", "sub-00-a");
    try {
      var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
      await Assert.That(received).IsNotNull();
      await Assert.That(received!.MessageId).IsEqualTo(envelope.MessageId.Value.ToString());
      await receiver.CompleteMessageAsync(received);
    } finally {
      await receiver.DisposeAsync();
    }
  }

  [Test]
  public async Task SubscribeAsync_CreatesProcessor_AndInvokesHandlerAsync() {
    // Arrange - use CreateCombinedOptions which includes all registered contexts
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    await transport.InitializeAsync();

    var destination = new TransportDestination("topic-01", "sub-01-a");
    var publishDestination = new TransportDestination("topic-01");

    // Drain any existing messages first
    await _drainMessagesAsync("topic-01", "sub-01-a");

    // Create warmup and test awaiters using harnesses
    var warmupId = SubscriptionWarmup.GenerateWarmupId();
    var warmupAwaiter = new SignalAwaiter();
    var testAwaiter = new MessageAwaiter<IMessageEnvelope>(envelope => envelope);

    // Act - Create subscription with warmup detection
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        // Check if this is the warmup message or actual test message
        if (envelope is MessageEnvelope<TestMessage> testEnvelope &&
            testEnvelope.Payload.Content.Contains(warmupId)) {
          warmupAwaiter.Signal();
        } else {
          await testAwaiter.Handler(envelope, envelopeType, ct);
        }
      },
      destination
    );

    try {
      // Warmup subscription using harness
      await SubscriptionWarmup.WarmupAsync(
        transport,
        publishDestination,
        () => _createTestEnvelopeWithContent(warmupId),
        warmupAwaiter
      );

      // Act: Publish the actual test message
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(envelope, publishDestination);

      // Wait for handler to be invoked
      var receivedEnvelope = await testAwaiter.WaitAsync(TimeSpan.FromSeconds(30));

      // Assert
      await Assert.That(receivedEnvelope).IsNotNull();
      await Assert.That(receivedEnvelope!.MessageId.Value).IsEqualTo(envelope.MessageId.Value);
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task Subscription_InitialState_IsActiveAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = TestJsonContext.Default
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    await transport.InitializeAsync();

    var destination = new TransportDestination("topic-00", "sub-00-a");

    // Act
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    try {
      // Assert
      await Assert.That(subscription.IsActive).IsTrue();
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task Subscription_Pause_SetsIsActiveFalseAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = TestJsonContext.Default
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    await transport.InitializeAsync();

    var destination = new TransportDestination("topic-00", "sub-00-a");
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    try {
      // Act
      await subscription.PauseAsync();

      // Assert
      await Assert.That(subscription.IsActive).IsFalse();
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task Subscription_Resume_SetsIsActiveTrueAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = TestJsonContext.Default
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    await transport.InitializeAsync();

    var destination = new TransportDestination("topic-00", "sub-00-a");
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    try {
      await subscription.PauseAsync();

      // Act
      await subscription.ResumeAsync();

      // Assert
      await Assert.That(subscription.IsActive).IsTrue();
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task SendAsync_ThrowsNotSupportedAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = TestJsonContext.Default
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("topic-00");

    // Act & Assert
    await Assert.That(async () => await transport.SendAsync<TestMessage, TestMessage>(envelope, destination)).Throws<NotSupportedException>();
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    return _createTestEnvelopeWithContent("test-content");
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithContent(string content) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
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

  private async Task _drainMessagesAsync(string topicName, string subscriptionName) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    try {
      for (var i = 0; i < 100; i++) {
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
        if (msg == null) {
          break;
        }
        await receiver.CompleteMessageAsync(msg);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }

}
