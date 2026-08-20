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
    var callOrder = new CallOrderRecorder();
    var provisioner = new TrackingProvisioner(callOrder);
    var transport = new TrackingTransport(callOrder);
    var ownedDomains = new HashSet<string> { "myapp.users", "myapp.orders" };

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
      new RoutingOptions().OwnDomains([.. ownedDomains])));
    var serviceProvider = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic", "#"));

    var worker = _createWorker(transport, options, serviceProvider);

    // Act — wait on the subscription signal (the worker's last startup step), never a fixed delay:
    // under full-suite load a sleep-then-assert races the worker's ExecuteAsync and flakes.
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await transport.FirstSubscribe.WaitAsync(TimeSpan.FromSeconds(10));
    } finally {
      cts.Cancel();
      await worker.StopAsync(CancellationToken.None);
    }

    // Assert
    await Assert.That(provisioner.ProvisionedDomains).IsNotNull();
    await Assert.That(provisioner.ProvisionedDomains!.Count).IsEqualTo(2);
    await Assert.That(provisioner.ProvisionedDomains).Contains("myapp.users");
    await Assert.That(provisioner.ProvisionedDomains).Contains("myapp.orders");

    // Verify provisioning happened before subscriptions — recorded call order, not wall-clock
    // timestamps (same-tick DateTimeOffset stamps compare equal and flake an IsLessThan).
    await Assert.That(callOrder.IndexOf("provision")).IsGreaterThanOrEqualTo(0);
    await Assert.That(callOrder.IndexOf("subscribe")).IsGreaterThanOrEqualTo(0);
    await Assert.That(callOrder.IndexOf("provision")).IsLessThan(callOrder.IndexOf("subscribe"));
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

    // Act — signal-driven (see the ordering test): a fixed delay races ExecuteAsync under load.
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await transport.FirstSubscribe.WaitAsync(TimeSpan.FromSeconds(10));
    } finally {
      cts.Cancel();
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

    // Act — subscribing is the step AFTER the provisioning decision, so once the subscribe
    // signal fires the skip-provisioning assertion below is ordering-sound (no fixed delay).
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await transport.FirstSubscribe.WaitAsync(TimeSpan.FromSeconds(10));
    } finally {
      cts.Cancel();
      await worker.StopAsync(CancellationToken.None);
    }

    // Assert - provisioner should NOT have been called
    await Assert.That(provisioner.ProvisionedDomains).IsNull();
    // But subscriptions should still be created
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);
  }

  /// <summary>
  /// When a TopologyManifest is resolvable from DI, the worker additionally runs the
  /// manifest-driven DARK provisioning (phase 5) — before subscriptions are created.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_WithManifestResolvable_CallsProvisionManifestBeforeSubscriptionsAsync() {
    // Arrange
    var callOrder = new CallOrderRecorder();
    var provisioner = new TrackingProvisioner(callOrder);
    var transport = new TrackingTransport(callOrder);
    var manifest = new Whizbang.Core.Routing.TopologyManifest("test-service", [], []);

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.AddSingleton(manifest);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new RoutingOptions()));
    var serviceProvider = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic", "#"));

    var worker = _createWorker(transport, options, serviceProvider);

    // Act — signal-driven (see the ordering test above)
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await transport.FirstSubscribe.WaitAsync(TimeSpan.FromSeconds(10));
    } finally {
      cts.Cancel();
      await worker.StopAsync(CancellationToken.None);
    }

    // Assert
    await Assert.That(provisioner.ProvisionedManifest).IsNotNull();
    await Assert.That(provisioner.ProvisionedManifest!.ServiceName).IsEqualTo("test-service");
    await Assert.That(callOrder.IndexOf("provision-manifest")).IsGreaterThanOrEqualTo(0);
    await Assert.That(callOrder.IndexOf("provision-manifest")).IsLessThan(callOrder.IndexOf("subscribe"))
      .Because("DARK provisioning must complete before the broker can deliver anything");
  }

  /// <summary>
  /// Without a manifest in DI, manifest provisioning is skipped — existing consumers see
  /// zero behavior change (owned-domain provisioning and subscriptions run as before).
  /// </summary>
  [Test]
  public async Task ExecuteAsync_NoManifestRegistered_SkipsManifestProvisioningAsync() {
    // Arrange
    var provisioner = new TrackingProvisioner();
    var transport = new TrackingTransport();

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new RoutingOptions()));
    var serviceProvider = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic", "#"));

    var worker = _createWorker(transport, options, serviceProvider);

    // Act
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await transport.FirstSubscribe.WaitAsync(TimeSpan.FromSeconds(10));
    } finally {
      cts.Cancel();
      await worker.StopAsync(CancellationToken.None);
    }

    // Assert
    await Assert.That(provisioner.ProvisionedManifest).IsNull();
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
      metrics: null,
      logger: NullLoggerFactory.Instance.CreateLogger<TransportConsumerWorker>()
    );
  }

  // ========================================
  // TEST DOUBLES
  // ========================================

  /// <summary>Records the relative order of provisioning/subscription calls — wall-clock stamps
  /// can tie on the same tick and flake a strict less-than comparison.</summary>
  private sealed class CallOrderRecorder {
    private readonly List<string> _steps = [];
    public void Record(string step) {
      lock (_steps) {
        _steps.Add(step);
      }
    }
    public int IndexOf(string step) {
      lock (_steps) {
        return _steps.IndexOf(step);
      }
    }
  }

  /// <summary>
  /// Test double for IInfrastructureProvisioner that tracks calls.
  /// </summary>
  private sealed class TrackingProvisioner(CallOrderRecorder? callOrder = null) : IInfrastructureProvisioner {
    public IReadOnlySet<string>? ProvisionedDomains { get; private set; }
    public Whizbang.Core.Routing.TopologyManifest? ProvisionedManifest { get; private set; }

    public Task ProvisionOwnedDomainsAsync(
        IReadOnlySet<string> ownedDomains,
        CancellationToken cancellationToken = default) {
      callOrder?.Record("provision");
      ProvisionedDomains = ownedDomains;
      return Task.CompletedTask;
    }

    public Task ProvisionManifestAsync(
        Whizbang.Core.Routing.TopologyManifest manifest,
        CancellationToken cancellationToken = default) {
      callOrder?.Record("provision-manifest");
      ProvisionedManifest = manifest;
      return Task.CompletedTask;
    }
  }

  /// <summary>
  /// Test double for ITransport that tracks subscription calls.
  /// </summary>
  private sealed class TrackingTransport(CallOrderRecorder? callOrder = null) : ITransport {
    private readonly TaskCompletionSource _firstSubscribe = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public int SubscribeCallCount { get; private set; }

    /// <summary>Completes on the first subscription — the deterministic "worker reached its last
    /// startup step" signal the tests wait on instead of a fixed delay.</summary>
    public Task FirstSubscribe => _firstSubscribe.Task;

    public Task InitializeAsync(CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      callOrder?.Record("subscribe");
      _firstSubscribe.TrySetResult();
      return Task.FromResult<ISubscription>(new NoOpSubscription());
    }

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      callOrder?.Record("subscribe");
      _firstSubscribe.TrySetResult();
      return Task.FromResult<ISubscription>(new NoOpSubscription());
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
