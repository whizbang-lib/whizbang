using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for ServiceBusConsumerWorker polling mode configuration.
/// Note: Full end-to-end polling behavior is tested in integration tests
/// since ExecuteAsync is protected.
///
/// TODO: These tests are currently commented out because polling mode has not been implemented yet.
/// ServiceBusConsumerOptions needs Mode, PollingInterval properties and SubscriptionMode enum.
/// Uncomment and implement when polling mode feature is added.
/// </summary>
public class ServiceBusConsumerWorkerPollingTests {

  // All tests commented out until polling mode is implemented

  /*
  /// <summary>
  /// Verifies that polling mode does not create subscriptions during StartAsync.
  /// </summary>
  [Test]
  public async Task PollingMode_StartAsync_DoesNotCreateSubscriptionsAsync() {
    // Arrange
    var transport = new TestPollingTransport();
    var serviceCollection = new ServiceCollection();
    serviceCollection.AddLogging();
    serviceCollection.AddSingleton<IServiceInstanceProvider>(_ => new PollingTestServiceInstanceProvider());
    var serviceProvider = serviceCollection.BuildServiceProvider();

    var options = new ServiceBusConsumerOptions {
      Mode = SubscriptionMode.Polling,
      Subscriptions = {
        new TopicSubscription("topic-00", "sub-00-a")
      }
    };

    var worker = new ServiceBusConsumerWorker(
      serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
      transport,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      serviceProvider.GetRequiredService<ILogger<ServiceBusConsumerWorker>>(),
      new TestOrderedStreamProcessor(),
      options
    );

    // Act
    await worker.StartAsync(CancellationToken.None);

    // Assert
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(0);
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Verifies that processor mode creates subscriptions during StartAsync.
  /// </summary>
  [Test]
  public async Task ProcessorMode_StartAsync_CreatesSubscriptionsAsync() {
    // Arrange
    var transport = new TestPollingTransport();
    var serviceCollection = new ServiceCollection();
    serviceCollection.AddLogging();
    serviceCollection.AddSingleton<IServiceInstanceProvider>(_ => new PollingTestServiceInstanceProvider());
    var serviceProvider = serviceCollection.BuildServiceProvider();

    var options = new ServiceBusConsumerOptions {
      Mode = SubscriptionMode.Processor,  // Processor mode
      Subscriptions = {
        new TopicSubscription("topic-00", "sub-00-a")
      }
    };

    var worker = new ServiceBusConsumerWorker(
      serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
      transport,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      serviceProvider.GetRequiredService<ILogger<ServiceBusConsumerWorker>>(),
      new TestOrderedStreamProcessor(),
      options
    );

    // Act
    await worker.StartAsync(CancellationToken.None);

    // Assert
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Verifies that default mode is Processor.
  /// </summary>
  [Test]
  public async Task ServiceBusConsumerOptions_DefaultMode_IsProcessorAsync() {
    // Arrange & Act
    var options = new ServiceBusConsumerOptions();

    // Assert
    await Assert.That(options.Mode).IsEqualTo(SubscriptionMode.Processor);
  }

  /// <summary>
  /// Verifies that default polling interval is 500ms.
  /// </summary>
  [Test]
  public async Task ServiceBusConsumerOptions_DefaultPollingInterval_Is500MsAsync() {
    // Arrange & Act
    var options = new ServiceBusConsumerOptions();

    // Assert
    await Assert.That(options.PollingInterval).IsEqualTo(TimeSpan.FromMilliseconds(500));
  }
  */
}

/// <summary>
/// Test transport that tracks SubscribeAsync and ReceiveAsync calls.
/// </summary>
internal sealed class TestPollingTransport : ITransport {
  public int SubscribeCallCount { get; private set; }
  public int ReceiveCallCount { get; private set; }
  public bool IsInitialized => true;
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default) {
    SubscribeCallCount++;
    return Task.FromResult<ISubscription>(new PollingTestSubscription());
  }

  public Task<IMessageEnvelope?> ReceiveAsync(
    TransportDestination destination,
    TimeSpan waitTime,
    CancellationToken cancellationToken = default) {
    ReceiveCallCount++;
    // Return null (no message) to avoid message processing complexity in test
    return Task.FromResult<IMessageEnvelope?>(null);
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

/// <summary>
/// Test subscription implementation for polling tests.
/// </summary>
internal sealed class PollingTestSubscription : ISubscription {
  public bool IsActive { get; private set; } = true;

  public Task PauseAsync() {
    IsActive = false;
    return Task.CompletedTask;
  }

  public Task ResumeAsync() {
    IsActive = true;
    return Task.CompletedTask;
  }

  public void Dispose() {
    IsActive = false;
  }
}

/// <summary>
/// Test service instance provider for polling tests.
/// </summary>
internal sealed class PollingTestServiceInstanceProvider : IServiceInstanceProvider {
  private readonly Guid _instanceId = Guid.NewGuid();

  public Guid InstanceId => _instanceId;
  public string ServiceName => "test-service";
  public string HostName => "test-host";
  public int ProcessId => 12345;

  public ServiceInstanceInfo ToInfo() {
    return new ServiceInstanceInfo {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}

/// <summary>
/// Test ordered stream processor (minimal implementation).
/// </summary>
internal sealed class TestOrderedStreamProcessor : OrderedStreamProcessor {
  public TestOrderedStreamProcessor()
    : base(parallelizeStreams: false, logger: null) {
  }
}
