using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable CS0067 // Event is never used (test doubles)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQ health check implementation.
/// </summary>
public class RabbitMQHealthCheckTests {
  [Test]
  public async Task CheckHealthAsync_WithOpenConnection_ReturnsHealthyAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var healthCheck = new RabbitMQHealthCheck(transport, fakeConnection);

    // Act
    var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task CheckHealthAsync_WithClosedConnection_ReturnsUnhealthyAsync() {
    // Arrange - Use a closed FakeConnection
    var closedConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()), isOpen: false);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var pool = new RabbitMQChannelPool(closedConnection, maxChannels: 5);
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      closedConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    var healthCheck = new RabbitMQHealthCheck(transport, closedConnection);

    // Act
    var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
  }

  [Test]
  public async Task CheckHealthAsync_WithNonRabbitMQTransport_ReturnsDegradedAsync() {
    // Arrange
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var fakeTransport = new FakeNonRabbitMQTransport();

    var healthCheck = new RabbitMQHealthCheck(fakeTransport, fakeConnection);

    // Act
    var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
  }

  // Test double for a non-RabbitMQ transport
  private sealed class FakeNonRabbitMQTransport : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) where TRequest : notnull where TResponse : notnull => throw new NotImplementedException();
  }
}
