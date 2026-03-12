using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for TransportConsumerWorker connection recovery, ITransportWithRecovery,
/// and the StopAsync cleanup path.
/// </summary>
[Category("Workers")]
public class TransportConsumerWorkerConnectionRecoveryTests {

  // ========================================
  // ITransportWithRecovery - recovery handler registration
  // ========================================

  [Test]
  public async Task Constructor_WithTransportWithRecovery_RegistersRecoveryHandlerAsync() {
    // Arrange
    var transport = new RecoverableTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));

    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton<IDispatcher>(sp => new FakeDispatcher());
    var serviceProvider = serviceCollection.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions();
    var orderedProcessor = new OrderedStreamProcessor(parallelizeStreams: false, logger: null);

    // Act
    var worker = new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      scopeFactory,
      jsonOptions,
      orderedProcessor,
      lifecycleMessageDeserializer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    // Assert - recovery handler was registered
    await Assert.That(transport.RecoveryHandlerRegistered).IsTrue()
      .Because("Constructor should register recovery handler on ITransportWithRecovery");
  }

  // ========================================
  // Connection recovery re-subscribes
  // ========================================

  [Test]
  public async Task OnConnectionRecovered_ReSubscribesAllDestinationsAsync() {
    // Arrange
    var transport = new RecoverableTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));
    options.Destinations.Add(new TransportDestination("topic2"));

    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton<IDispatcher>(sp => new FakeDispatcher());
    var serviceProvider = serviceCollection.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions();
    var orderedProcessor = new OrderedStreamProcessor(parallelizeStreams: false, logger: null);

    var worker = new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      scopeFactory,
      jsonOptions,
      orderedProcessor,
      lifecycleMessageDeserializer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();

    // Start worker so initial subscriptions are created
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(300);

    var initialSubscribeCount = transport.SubscribeCallCount;
    await Assert.That(initialSubscribeCount).IsEqualTo(2)
      .Because("Initial startup should create 2 subscriptions");

    // Act - simulate connection recovery
    await transport.SimulateRecoveryAsync(CancellationToken.None);
    await Task.Delay(300);

    // Assert - subscriptions should be re-established
    await Assert.That(transport.SubscribeCallCount).IsGreaterThan(initialSubscribeCount)
      .Because("Recovery should re-subscribe to all destinations");

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // StopAsync disposes CTS and subscriptions
  // ========================================

  [Test]
  public async Task StopAsync_WithNoStartAsync_DoesNotThrowAsync() {
    // Arrange - worker created but never started
    var transport = new SimpleTestTransport();
    var options = new TransportConsumerOptions();

    var serviceCollection = new ServiceCollection();
    var serviceProvider = serviceCollection.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions();
    var orderedProcessor = new OrderedStreamProcessor(parallelizeStreams: false, logger: null);

    var worker = new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      scopeFactory,
      jsonOptions,
      orderedProcessor,
      lifecycleMessageDeserializer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    // Act & Assert - StopAsync should not throw even without StartAsync
    await Assert.That(async () => await worker.StopAsync(CancellationToken.None))
      .ThrowsNothing();
  }

  [Test]
  public async Task StopAsync_ClearsSubscriptionStatesAsync() {
    // Arrange
    var transport = new SimpleTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));

    var serviceCollection = new ServiceCollection();
    var serviceProvider = serviceCollection.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions();
    var orderedProcessor = new OrderedStreamProcessor(parallelizeStreams: false, logger: null);

    var worker = new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      scopeFactory,
      jsonOptions,
      orderedProcessor,
      lifecycleMessageDeserializer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(300);

    // Verify subscriptions exist
    await Assert.That(worker.SubscriptionStates.Count).IsEqualTo(1);

    // Act
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Assert - states should be cleared
    await Assert.That(worker.SubscriptionStates.Count).IsEqualTo(0)
      .Because("StopAsync should clear all subscription states");
  }

  // ========================================
  // SubscriptionStates property
  // ========================================

  [Test]
  public async Task SubscriptionStates_AfterConstruction_HasOneStatePerDestinationAsync() {
    // Arrange
    var transport = new SimpleTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));
    options.Destinations.Add(new TransportDestination("topic2"));
    options.Destinations.Add(new TransportDestination("topic3"));

    var serviceCollection = new ServiceCollection();
    var serviceProvider = serviceCollection.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions();
    var orderedProcessor = new OrderedStreamProcessor(parallelizeStreams: false, logger: null);

    // Act
    var worker = new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      scopeFactory,
      jsonOptions,
      orderedProcessor,
      lifecycleMessageDeserializer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    // Assert
    await Assert.That(worker.SubscriptionStates.Count).IsEqualTo(3)
      .Because("Each destination should have a corresponding subscription state");
  }

  // ========================================
  // ReadinessCheck returns false - stops early
  // ========================================

  [Test]
  public async Task ExecuteAsync_ReadinessCheckReturnsFalse_StopsWithoutSubscribingAsync() {
    // Arrange
    var transport = new SimpleTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));

    var serviceCollection = new ServiceCollection();
    serviceCollection.AddSingleton<ITransportReadinessCheck>(new FailingReadinessCheck());
    var serviceProvider = serviceCollection.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions();
    var orderedProcessor = new OrderedStreamProcessor(parallelizeStreams: false, logger: null);

    var worker = new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      scopeFactory,
      jsonOptions,
      orderedProcessor,
      lifecycleMessageDeserializer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();

    // Act
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(500);

    // Assert - should NOT have subscribed
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(0)
      .Because("Readiness check returned false, so no subscriptions should be created");

    cts.Cancel();
  }

  // ========================================
  // Pause/Resume with no subscriptions (null subscription objects)
  // ========================================

  [Test]
  public async Task PauseAllSubscriptionsAsync_WithNoActiveSubscriptions_DoesNotThrowAsync() {
    // Arrange - construct worker but don't start it (subscriptions are null)
    var transport = new SimpleTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));

    var serviceCollection = new ServiceCollection();
    var serviceProvider = serviceCollection.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      scopeFactory,
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    // Act & Assert - should not throw when subscription objects are null
    await Assert.That(async () => await worker.PauseAllSubscriptionsAsync())
      .ThrowsNothing();
  }

  [Test]
  public async Task ResumeAllSubscriptionsAsync_WithNoActiveSubscriptions_DoesNotThrowAsync() {
    // Arrange
    var transport = new SimpleTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));

    var serviceCollection = new ServiceCollection();
    var serviceProvider = serviceCollection.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport,
      options,
      new SubscriptionResilienceOptions(),
      scopeFactory,
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    // Act & Assert
    await Assert.That(async () => await worker.ResumeAllSubscriptionsAsync())
      .ThrowsNothing();
  }

  // ========================================
  // Test Doubles
  // ========================================

  private sealed class RecoverableTransport : ITransport, ITransportWithRecovery {
    private Func<CancellationToken, Task>? _recoveryHandler;
    private readonly List<SimpleSubscription> _subscriptions = [];

    public int SubscribeCallCount { get; private set; }
    public bool RecoveryHandlerRegistered => _recoveryHandler != null;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) {
      SubscribeCallCount++;
      var subscription = new SimpleSubscription();
      _subscriptions.Add(subscription);
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();

    public void SetRecoveryHandler(Func<CancellationToken, Task>? handler) {
      _recoveryHandler = handler;
    }

    public async Task SimulateRecoveryAsync(CancellationToken ct) {
      if (_recoveryHandler != null) {
        await _recoveryHandler(ct);
      }
    }
  }

  private sealed class SimpleTestTransport : ITransport {
    public int SubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) {
      SubscribeCallCount++;
      return Task.FromResult<ISubscription>(new SimpleSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class SimpleSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
    public bool IsDisposed { get; private set; }

#pragma warning disable CS0067
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() { IsDisposed = true; }
  }

  private sealed class FailingReadinessCheck : ITransportReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(false);
  }
}
