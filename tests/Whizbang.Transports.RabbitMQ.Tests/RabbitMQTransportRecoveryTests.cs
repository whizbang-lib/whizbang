using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQ transport recovery behavior.
/// Ensures resilient handling of channel disposal, connection failures, and recovery.
/// </summary>
public class RabbitMQTransportRecoveryTests {
  #region Subscription Disconnection Detection Tests

  [Test]
  public async Task Subscription_WhenChannelShutdownByPeer_FiresOnDisconnectedEventAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    var disconnectedEventFired = false;
    string? disconnectReason = null;
    subscription.OnDisconnected += (sender, args) => {
      disconnectedEventFired = true;
      disconnectReason = args.Reason;
    };

    // Act - Simulate channel shutdown (non-application initiated)
    await fakeChannel.SimulateShutdownAsync(ShutdownInitiator.Peer, "Connection reset by broker", null);

    // Assert
    await Assert.That(disconnectedEventFired).IsTrue();
    await Assert.That(disconnectReason).IsEqualTo("Connection reset by broker");
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task Subscription_WhenChannelShutdownByLibrary_FiresOnDisconnectedEventAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    var disconnectedEventFired = false;
    subscription.OnDisconnected += (sender, args) => {
      disconnectedEventFired = true;
    };

    // Act - Simulate channel shutdown initiated by library (e.g., connection loss)
    await fakeChannel.SimulateShutdownAsync(ShutdownInitiator.Library, "Connection lost", null);

    // Assert - OnDisconnected should fire for library-initiated shutdowns
    await Assert.That(disconnectedEventFired).IsTrue();
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task Subscription_WhenApplicationInitiatedShutdown_DoesNotFireOnDisconnectedAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    var disconnectedEventFired = false;
    subscription.OnDisconnected += (sender, args) => {
      disconnectedEventFired = true;
    };

    // Act - Dispose subscription (application-initiated)
    subscription.Dispose();
    await Task.Delay(150); // Wait for fire-and-forget disposal

    // Assert - OnDisconnected should NOT fire for application-initiated shutdown
    await Assert.That(disconnectedEventFired).IsFalse();
  }

  [Test]
  public async Task Subscription_OnDisconnectedEventArgs_ContainsExceptionIfPresentAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    Exception? receivedException = null;
    subscription.OnDisconnected += (sender, args) => {
      receivedException = args.Exception;
    };

    // Act - Simulate shutdown with exception
    var testException = new InvalidOperationException("Test connection failure");
    await fakeChannel.SimulateShutdownAsync(ShutdownInitiator.Peer, "Connection failed", testException);

    // Assert
    await Assert.That(receivedException).IsNotNull();
    await Assert.That(receivedException!.Message).IsEqualTo("Test connection failure");
  }

  #endregion

  #region Recovery Handler Tests

  [Test]
  public async Task Transport_OnConnectionRecovery_InvokesRecoveryHandlerAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    var recoveryHandlerCalled = false;
    transport.SetRecoveryHandler(async ct => {
      recoveryHandlerCalled = true;
      await Task.CompletedTask;
    });

    await transport.InitializeAsync();

    // Act - Simulate connection recovery
    await fakeConnection.SimulateRecoverySucceededAsync();

    // Assert
    await Assert.That(recoveryHandlerCalled).IsTrue();
  }

  [Test]
  public async Task Transport_WhenRecoveryHandlerThrows_LogsErrorButDoesNotPropagateAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    transport.SetRecoveryHandler(async ct => {
      await Task.CompletedTask;
      throw new InvalidOperationException("Recovery handler failed");
    });

    await transport.InitializeAsync();

    // Act & Assert - Should not throw
    await Assert.That(async () => await fakeConnection.SimulateRecoverySucceededAsync()).ThrowsNothing();
  }

  [Test]
  public async Task Transport_SetRecoveryHandlerToNull_ClearsHandlerAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    var callCount = 0;
    transport.SetRecoveryHandler(async ct => {
      callCount++;
      await Task.CompletedTask;
    });

    await transport.InitializeAsync();

    // Trigger recovery once
    await fakeConnection.SimulateRecoverySucceededAsync();
    await Assert.That(callCount).IsEqualTo(1);

    // Clear handler
    transport.SetRecoveryHandler(null);

    // Act - Trigger recovery again
    await fakeConnection.SimulateRecoverySucceededAsync();

    // Assert - Handler should not have been called again
    await Assert.That(callCount).IsEqualTo(1);
  }

  #endregion

  #region TaskCanceledException Handling Tests

  [Test]
  public async Task SubscribeAsync_WhenQueueBindThrowsTaskCanceled_WrapsInInvalidOperationExceptionAsync() {
    // Arrange - Create a channel that throws TaskCanceledException on QueueBindAsync
    var cancelingChannel = new FakeChannelThatThrowsOnBind();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(cancelingChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    // Act & Assert - Should wrap TaskCanceledException in InvalidOperationException
    await Assert.That(async () => await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    )).Throws<InvalidOperationException>();
  }

  #endregion
}

#region Test Doubles for Recovery Tests

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

/// <summary>
/// Fake channel that throws TaskCanceledException on QueueBindAsync.
/// </summary>
internal sealed class FakeChannelThatThrowsOnBind : FakeChannel {
  public override Task QueueBindAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? arguments, bool noWait, CancellationToken cancellationToken = default) {
    throw new TaskCanceledException("A task was canceled.");
  }
}

#pragma warning restore CS0067
#pragma warning restore CA1822

#endregion
