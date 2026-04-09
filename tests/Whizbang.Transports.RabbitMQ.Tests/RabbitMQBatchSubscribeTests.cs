using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Transports.RabbitMQ;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQ SubscribeBatchAsync contract.
/// These verify the method exists and has correct guard clauses.
/// Channel-level batch receive + ACK behavior is tested in integration tests
/// with a real RabbitMQ instance (Testcontainers).
/// </summary>
public class RabbitMQBatchSubscribeTests {

  [Test]
  public async Task SubscribeBatchAsync_NotInitialized_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    var transport = new RabbitMQTransport(fakeConnection, jsonOptions, pool, new RabbitMQOptions(), logger: null);

    // Act & Assert — transport NOT initialized
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask,
        new TransportDestination("test-topic"),
        new TransportBatchOptions()
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_NullHandler_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    var transport = new RabbitMQTransport(fakeConnection, jsonOptions, pool, new RabbitMQOptions(), logger: null);
    await transport.InitializeAsync();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await transport.SubscribeBatchAsync(
        null!,
        new TransportDestination("test-topic"),
        new TransportBatchOptions()
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_NullDestination_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    var transport = new RabbitMQTransport(fakeConnection, jsonOptions, pool, new RabbitMQOptions(), logger: null);
    await transport.InitializeAsync();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask,
        null!,
        new TransportBatchOptions()
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_NullBatchOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    var transport = new RabbitMQTransport(fakeConnection, jsonOptions, pool, new RabbitMQOptions(), logger: null);
    await transport.InitializeAsync();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask,
        new TransportDestination("test-topic"),
        null!
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_Disposed_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    var transport = new RabbitMQTransport(fakeConnection, jsonOptions, pool, new RabbitMQOptions(), logger: null);
    await transport.InitializeAsync();
    await transport.DisposeAsync();

    // Act & Assert
    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask,
        new TransportDestination("test-topic"),
        new TransportBatchOptions()
      )
    );
  }
}
