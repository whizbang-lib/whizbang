using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Additional coverage tests for TransportConsumerBuilderExtensions targeting uncovered branches:
/// - IReceptorInvoker fallback to NullReceptorInvoker when no registry
/// - IReceptorInvoker returns ReceptorInvoker when registry is present
/// - Health check lambda when TransportConsumerWorker is null (returns empty dictionary)
/// - ResilienceOptions registration
/// - PerspectiveBuilder overload with routing error path
/// - OrderedStreamProcessor and IEventCascader registration
/// </summary>
public class TransportConsumerBuilderExtensionsCoverageTests {
  private static void _registerRequiredServices(
    IServiceCollection services,
    bool includeServiceInstanceProvider = true) {
    services.AddLogging();
    if (includeServiceInstanceProvider) {
      services.AddSingleton<IServiceInstanceProvider>(new TestServiceInstanceProvider("TestService"));
    }
  }

  // ========================================
  // IReceptorInvoker Registration Tests
  // ========================================

  [Test]
  public async Task AddTransportConsumer_WithoutReceptorRegistry_RegistersNullReceptorInvokerAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act - no IReceptorRegistry registered
    builder.AddTransportConsumer();

    // Assert - should use NullReceptorInvoker fallback
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

    await Assert.That(invoker).IsNotNull()
      .Because("IReceptorInvoker should always be resolvable");
    await Assert.That(invoker).IsTypeOf<NullReceptorInvoker>()
      .Because("Without registry, NullReceptorInvoker should be used as fallback");
  }

  [Test]
  public async Task AddTransportConsumer_WithReceptorRegistry_RegistersReceptorInvokerAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    // Register a receptor registry so ReceptorInvoker branch is taken
    services.AddSingleton<IReceptorRegistry>(new TestReceptorRegistry());

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert - should use ReceptorInvoker (not null invoker)
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

    await Assert.That(invoker).IsNotNull();
    await Assert.That(invoker).IsTypeOf<ReceptorInvoker>()
      .Because("With registry, ReceptorInvoker should be used");
  }

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_WithoutReceptorRegistry_RegistersNullInvokerAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer();

    // Assert - fallback to NullReceptorInvoker
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

    await Assert.That(invoker).IsNotNull();
    await Assert.That(invoker).IsTypeOf<NullReceptorInvoker>();
  }

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_WithReceptorRegistry_RegistersReceptorInvokerAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    services.AddSingleton<IReceptorRegistry>(new TestReceptorRegistry());

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer();

    // Assert
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

    await Assert.That(invoker).IsNotNull();
    await Assert.That(invoker).IsTypeOf<ReceptorInvoker>();
  }

  // ========================================
  // Health Check Lambda Tests
  // ========================================

  [Test]
  public async Task AddTransportConsumer_HealthCheck_RegisteredCorrectlyAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert - the health check service should be registered
    var provider = services.BuildServiceProvider();
    var healthCheckService = provider.GetService<HealthCheckService>();
    await Assert.That(healthCheckService).IsNotNull()
      .Because("HealthCheckService should be registered");

    // TransportConsumerWorker is registered as IHostedService, not as a direct type
    // So GetService<TransportConsumerWorker>() returns null, exercising the null-coalescing path
    var worker = provider.GetService<TransportConsumerWorker>();
    await Assert.That(worker).IsNull()
      .Because("TransportConsumerWorker is registered as IHostedService, not as direct singleton");
  }

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_HealthCheck_RegisteredAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer();

    // Assert - health check is registered on perspective builder too
    var provider = services.BuildServiceProvider();
    var healthCheckService = provider.GetService<HealthCheckService>();
    await Assert.That(healthCheckService).IsNotNull();
  }

  // ========================================
  // ResilienceOptions Registration Tests
  // ========================================

  [Test]
  public async Task AddTransportConsumer_RegistersSubscriptionResilienceOptionsAsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer(config => {
      config.ResilienceOptions.InitialRetryAttempts = 5;
    });

    // Assert
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(SubscriptionResilienceOptions));
    await Assert.That(descriptor).IsNotNull()
      .Because("SubscriptionResilienceOptions should be registered as singleton");
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);

    var provider = services.BuildServiceProvider();
    var resilienceOptions = provider.GetService<SubscriptionResilienceOptions>();
    await Assert.That(resilienceOptions).IsNotNull();
    await Assert.That(resilienceOptions!.InitialRetryAttempts).IsEqualTo(5);
  }

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_RegistersResilienceOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer(config => {
      config.ResilienceOptions.InitialRetryAttempts = 3;
    });

    // Assert
    var provider = services.BuildServiceProvider();
    var resilienceOptions = provider.GetService<SubscriptionResilienceOptions>();
    await Assert.That(resilienceOptions).IsNotNull();
    await Assert.That(resilienceOptions!.InitialRetryAttempts).IsEqualTo(3);
  }

  // ========================================
  // PerspectiveBuilder Routing Error Tests
  // ========================================

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_WithoutRouting_ThrowsOnResolutionAsync() {
    // Arrange - No WithRouting() called
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    // Note: No WithRouting() call
    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer();

    // Assert - should throw when resolving TransportConsumerOptions
    var provider = services.BuildServiceProvider();
    await Assert.That(() => provider.GetRequiredService<TransportConsumerOptions>())
      .Throws<InvalidOperationException>();
  }

  // ========================================
  // OrderedStreamProcessor Registration Tests
  // ========================================

  [Test]
  public async Task AddTransportConsumer_RegistersOrderedStreamProcessorAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(OrderedStreamProcessor));
    await Assert.That(descriptor).IsNotNull()
      .Because("OrderedStreamProcessor should be registered");
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_RegistersOrderedStreamProcessorAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer();

    // Assert
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(OrderedStreamProcessor));
    await Assert.That(descriptor).IsNotNull();
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  // ========================================
  // IEventCascader Registration Tests
  // ========================================

  [Test]
  public async Task AddTransportConsumer_RegistersDispatcherEventCascaderAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert - IEventCascader should be registered
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IEventCascader));
    await Assert.That(descriptor).IsNotNull()
      .Because("IEventCascader should be registered for receptor cascade support");
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  // ========================================
  // Test Helpers
  // ========================================

  private sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
    public TestServiceInstanceProvider(string serviceName) {
      ServiceName = serviceName;
    }

    public string ServiceName { get; }
    Guid IServiceInstanceProvider.InstanceId => Guid.NewGuid();
    public string HostName => "test-host";
    public int ProcessId => Environment.ProcessId;

    public ServiceInstanceInfo ToInfo() {
      return new ServiceInstanceInfo {
        ServiceName = ServiceName,
        InstanceId = ((IServiceInstanceProvider)this).InstanceId,
        HostName = HostName,
        ProcessId = ProcessId
      };
    }
  }

  private sealed class TestReceptorRegistry : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }

    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      false;

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }

    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      false;
  }
}
