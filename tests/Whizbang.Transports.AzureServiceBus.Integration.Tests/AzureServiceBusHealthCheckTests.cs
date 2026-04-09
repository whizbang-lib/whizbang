using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Tests for Azure Service Bus health check implementation.
/// </summary>
[Category("Integration")]
[Timeout(60_000)]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class AzureServiceBusHealthCheckTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  [Test]
  public async Task CheckHealthAsync_WithAzureServiceBusTransport_ReturnsHealthyAsync() {
    // Arrange
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    await transport.InitializeAsync();

    var healthCheck = new AzureServiceBusHealthCheck(transport);

    // Act
    var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task CheckHealthAsync_WithNonAzureServiceBusTransport_ReturnsDegradedAsync() {
    // Arrange
    var fakeTransport = new FakeNonServiceBusTransport();

    var healthCheck = new AzureServiceBusHealthCheck(fakeTransport);

    // Act
    var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
  }

  // Test double for a non-Azure Service Bus transport
  private sealed class FakeNonServiceBusTransport : ITransport {
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

    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) where TRequest : notnull where TResponse : notnull => throw new NotImplementedException();
  }
}
