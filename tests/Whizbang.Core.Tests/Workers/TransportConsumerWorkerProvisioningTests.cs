using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for TransportConsumerWorker infrastructure provisioning.
/// Verifies that owned domains are provisioned before subscriptions are created.
/// </summary>
public class TransportConsumerWorkerProvisioningTests {
  /// <summary>
  /// When a provisioner is registered and owned domains exist,
  /// provisioning should be called before subscriptions are created.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_WithProvisionerAndOwnedDomains_CallsProvisionerBeforeSubscriptionsAsync() {
    // Arrange
    var provisioner = new TrackingProvisioner();
    var transport = new TrackingTransport();
    var ownedDomains = new HashSet<string> { "myapp.users", "myapp.orders" };

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
      new RoutingOptions().OwnDomains(ownedDomains.ToArray())));
    var serviceProvider = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic", "#"));

    var worker = _createWorker(transport, options, serviceProvider);

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try {
      await worker.StartAsync(cts.Token);
      await Task.Delay(100, cts.Token); // Let worker execute
    } catch (OperationCanceledException) {
      // Expected - worker runs forever until cancelled
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    // Assert
    await Assert.That(provisioner.ProvisionedDomains).IsNotNull();
    await Assert.That(provisioner.ProvisionedDomains!.Count).IsEqualTo(2);
    await Assert.That(provisioner.ProvisionedDomains).Contains("myapp.users");
    await Assert.That(provisioner.ProvisionedDomains).Contains("myapp.orders");

    // Verify provisioning happened before subscriptions
    await Assert.That(provisioner.ProvisionCalledAt).IsNotNull();
    await Assert.That(transport.SubscribeCalledAt).IsNotNull();
    await Assert.That(provisioner.ProvisionCalledAt!.Value).IsLessThan(transport.SubscribeCalledAt!.Value);
  }

  /// <summary>
  /// When no provisioner is registered, subscriptions should still be created.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_WithoutProvisioner_SkipsProvisioningAndSubscribesAsync() {
    // Arrange
    var transport = new TrackingTransport();
    var services = new ServiceCollection();
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new RoutingOptions()));
    var serviceProvider = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic", "#"));

    var worker = _createWorker(transport, options, serviceProvider);

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try {
      await worker.StartAsync(cts.Token);
      await Task.Delay(100, cts.Token);
    } catch (OperationCanceledException) { } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    // Assert - subscriptions should still be created
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);
  }

  /// <summary>
  /// When owned domains is empty, provisioning should be skipped.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_WithEmptyOwnedDomains_SkipsProvisioningAsync() {
    // Arrange
    var provisioner = new TrackingProvisioner();
    var transport = new TrackingTransport();

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new RoutingOptions())); // Empty owned domains
    var serviceProvider = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic", "#"));

    var worker = _createWorker(transport, options, serviceProvider);

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try {
      await worker.StartAsync(cts.Token);
      await Task.Delay(100, cts.Token);
    } catch (OperationCanceledException) { } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    // Assert - provisioner should NOT have been called
    await Assert.That(provisioner.ProvisionedDomains).IsNull();
    // But subscriptions should still be created
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static TransportConsumerWorker _createWorker(
      ITransport transport,
      TransportConsumerOptions options,
      IServiceProvider serviceProvider) {
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    return new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(
        parallelizeStreams: false,
        logger: NullLoggerFactory.Instance.CreateLogger<OrderedStreamProcessor>()),
      lifecycleMessageDeserializer: null,
      lifecycleInvoker: null,
      logger: NullLoggerFactory.Instance.CreateLogger<TransportConsumerWorker>()
    );
  }

  // ========================================
  // TEST DOUBLES
  // ========================================

  /// <summary>
  /// Test double for IInfrastructureProvisioner that tracks calls.
  /// </summary>
  private sealed class TrackingProvisioner : IInfrastructureProvisioner {
    public IReadOnlySet<string>? ProvisionedDomains { get; private set; }
    public DateTimeOffset? ProvisionCalledAt { get; private set; }

    public Task ProvisionOwnedDomainsAsync(
        IReadOnlySet<string> ownedDomains,
        CancellationToken cancellationToken = default) {
      ProvisionCalledAt = DateTimeOffset.UtcNow;
      ProvisionedDomains = ownedDomains;
      return Task.CompletedTask;
    }
  }

  /// <summary>
  /// Test double for ITransport that tracks subscription calls.
  /// </summary>
  private sealed class TrackingTransport : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public int SubscribeCallCount { get; private set; }
    public DateTimeOffset? SubscribeCalledAt { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      SubscribeCalledAt ??= DateTimeOffset.UtcNow;
      return Task.FromResult<ISubscription>(new NoOpSubscription());
    }

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope envelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull {
      throw new NotImplementedException();
    }
  }

  private sealed class NoOpSubscription : ISubscription {
    public bool IsActive => true;

#pragma warning disable CS0067 // Event is required by interface but not used in test
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }
}
