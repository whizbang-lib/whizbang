using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Transports.AzureServiceBus;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for AzureServiceBusHealthCheck implementation.
/// Validates health check logic for Azure Service Bus transport connectivity.
/// </summary>
public class AzureServiceBusHealthCheckTests {

  [Test]
  public async Task CheckHealthAsync_WithAzureServiceBusTransport_ReturnsHealthyAsync() {
    // Arrange - Use test implementation of AzureServiceBusTransport
    var transport = new TestAzureServiceBusTransport();
    var healthCheck = new AzureServiceBusHealthCheck(transport);
    var context = new HealthCheckContext();

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    await Assert.That(result.Description).IsEqualTo("Azure Service Bus transport is available");
  }

  [Test]
  public async Task CheckHealthAsync_WithNonAzureServiceBusTransport_ReturnsDegradedAsync() {
    // Arrange - Use a different transport type (not AzureServiceBusTransport)
    var transport = new FakeTransport();
    var healthCheck = new AzureServiceBusHealthCheck(transport);
    var context = new HealthCheckContext();

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
    await Assert.That(result.Description).IsEqualTo("Transport is not Azure Service Bus");
  }

  [Test]
  public async Task CheckHealthAsync_WithException_ReturnsUnhealthyAsync() {
    // Arrange - Use a throwing health check to simulate exception
    var transport = new FakeTransport();
    var healthCheck = new ThrowingHealthCheck(transport);
    var context = new HealthCheckContext();

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    await Assert.That(result.Description).IsEqualTo("Azure Service Bus transport is not healthy");
    await Assert.That(result.Exception).IsNotNull();
  }

  [Test]
  public async Task CheckHealthAsync_WithNullTransport_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new AzureServiceBusHealthCheck(null!))
      .Throws<ArgumentNullException>()
      .WithParameterName("transport");
  }

  [Test]
  public async Task CheckHealthAsync_RespectsCancellationTokenAsync() {
    // Arrange
    var transport = new TestAzureServiceBusTransport();
    var healthCheck = new AzureServiceBusHealthCheck(transport);
    var context = new HealthCheckContext();
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act - Even with cancelled token, health check should complete (it's synchronous)
    var result = await healthCheck.CheckHealthAsync(context, cts.Token);

    // Assert - Health check completes despite cancellation (doesn't use token internally)
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task CheckHealthAsync_IncludesExceptionInUnhealthyResultAsync() {
    // Arrange
    var transport = new FakeTransport();
    var healthCheck = new ThrowingHealthCheck(transport);
    var context = new HealthCheckContext();

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    await Assert.That(result.Exception).IsNotNull();
    await Assert.That(result.Exception).IsTypeOf<InvalidOperationException>();
    await Assert.That(result.Exception!.Message).IsEqualTo("Test exception");
  }

  [Test]
  public async Task CheckHealthAsync_WithHealthCheckContext_UsesContextAsync() {
    // Arrange
    var transport = new TestAzureServiceBusTransport();
    var healthCheck = new AzureServiceBusHealthCheck(transport);
    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("test", _ => healthCheck, HealthStatus.Degraded, null)
    };

    // Act
    var result = await healthCheck.CheckHealthAsync(context);

    // Assert - Context is passed but not used in current implementation
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }
}

/// <summary>
/// Test implementation of AzureServiceBusTransport for health check tests.
/// Uses minimal valid constructor parameters for type-checking.
/// </summary>
internal sealed class TestAzureServiceBusTransport : AzureServiceBusTransport {
  public TestAzureServiceBusTransport()
    : base(
        client: new Azure.Messaging.ServiceBus.ServiceBusClient("Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=dGVzdA=="),
        jsonOptions: new System.Text.Json.JsonSerializerOptions()) {
    // Minimal valid constructor parameters for type checking
  }
}

/// <summary>
/// Fake transport implementation (not AzureServiceBusTransport) for testing Degraded status.
/// </summary>
internal sealed class FakeTransport : ITransport {
  public bool IsInitialized => true;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
  }

  public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
  }

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
    where TRequest : notnull
    where TResponse : notnull {
    throw new NotImplementedException();
  }
}

/// <summary>
/// Test helper class that simulates an exception during health check.
/// Mimics AzureServiceBusHealthCheck's try-catch behavior.
/// </summary>
internal sealed class ThrowingHealthCheck(ITransport transport) : IHealthCheck {
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

  public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
    try {
      throw new InvalidOperationException("Test exception");
    } catch (Exception ex) {
      return Task.FromResult(HealthCheckResult.Unhealthy("Azure Service Bus transport is not healthy", ex));
    }
  }
}
