using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Hosting.Azure.ServiceBus;

namespace Whizbang.Hosting.Azure.ServiceBus.Tests;

/// <summary>
/// Tests for ServiceBusReadinessCheck implementation.
/// Validates that readiness checks work correctly with Azure Service Bus.
/// </summary>
public class ServiceBusReadinessCheckTests {
  [Test]
  public async Task IsReadyAsync_WithValidClient_ReturnsTrueAsync() {
    // Arrange
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    // Act
    var isReady = await check.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue()
      .Because("Service Bus client is healthy and should be ready");
  }

  [Test]
  public async Task IsReadyAsync_WithClosedClient_ReturnsFalseAsync() {
    // Arrange
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: false);
    var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    // Act
    var isReady = await check.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsFalse()
      .Because("Service Bus client is closed and should not be ready");
  }

  [Test]
  public async Task IsReadyAsync_RespectsCancellationTokenAsync() {
    // Arrange
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);
    var cts = new CancellationTokenSource();
    cts.Cancel(); // Cancel immediately

    // Act & Assert
    // The cancellation token is checked during lock acquisition
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await check.IsReadyAsync(cts.Token)
    );
  }

  [Test]
  public async Task IsReadyAsync_CachesResult_ForSuccessfulChecksAsync() {
    // Arrange
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(
      transport,
      client,
      NullLogger<ServiceBusReadinessCheck>.Instance,
      cacheDuration: TimeSpan.FromSeconds(1)
    );

    // Act - First call
    var firstResult = await check.IsReadyAsync();
    var firstAccessCount = client.IsClosedAccessCount;

    // Act - Second call (should use cached result)
    var secondResult = await check.IsReadyAsync();
    var secondAccessCount = client.IsClosedAccessCount;

    // Assert
    await Assert.That(firstResult).IsTrue();
    await Assert.That(secondResult).IsTrue();
    await Assert.That(firstAccessCount).IsEqualTo(1)
      .Because("First call should check IsClosed property");
    await Assert.That(secondAccessCount).IsEqualTo(1)
      .Because("Second call should use cached result without checking IsClosed");
  }

  [Test]
  public async Task IsReadyAsync_CacheExpires_AfterDurationAsync() {
    // Arrange
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(
      transport,
      client,
      NullLogger<ServiceBusReadinessCheck>.Instance,
      cacheDuration: TimeSpan.FromMilliseconds(100)
    );

    // Act - First call
    await check.IsReadyAsync();
    var accessCountAfterFirst = client.IsClosedAccessCount;

    // Wait for cache to expire
    await Task.Delay(TimeSpan.FromMilliseconds(150));

    // Act - Second call (cache should be expired)
    await check.IsReadyAsync();
    var accessCountAfterSecond = client.IsClosedAccessCount;

    // Assert
    await Assert.That(accessCountAfterFirst).IsEqualTo(1)
      .Because("First call should check IsClosed");
    await Assert.That(accessCountAfterSecond).IsEqualTo(2)
      .Because("Cache should have expired and triggered a new check of IsClosed");
  }
}

/// <summary>
/// Test implementation of ITransport for testing readiness checks.
/// </summary>
internal sealed class TestTransport : ITransport {
  private readonly bool _isInitialized;

  public TestTransport(bool isInitialized) {
    _isInitialized = isInitialized;
  }

  public bool IsInitialized => _isInitialized;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
  }

  public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
  }

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
    where TRequest : notnull where TResponse : notnull {
    throw new NotImplementedException();
  }
}

/// <summary>
/// Test implementation of ServiceBusClient for testing readiness checks.
/// </summary>
internal sealed class TestServiceBusClient : ServiceBusClient {
  private readonly bool _isHealthy;
  public int IsClosedAccessCount { get; private set; }

  public TestServiceBusClient(bool isHealthy) {
    _isHealthy = isHealthy;
  }

  public override bool IsClosed {
    get {
      IsClosedAccessCount++;
      return !_isHealthy;
    }
  }
}
