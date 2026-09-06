using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.HealthChecks;
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

  /// <summary>
  /// Registers the pieces <see cref="TransportConsumerWorker"/> needs beyond what
  /// <see cref="AddTransportConsumer"/> itself registers, so the worker singleton can actually be
  /// constructed and resolved rather than just checked at the descriptor level. ITransport,
  /// JsonSerializerOptions, ISchemaReadyGate, ILifecycleMessageDeserializer and TransportMetrics
  /// are all required-but-nullable constructor parameters — type-activation refuses to construct
  /// the worker without them, even though their declared type is nullable.
  /// </summary>
  private static void _registerWorkerResolutionDependencies(IServiceCollection services) {
    services.AddSingleton<ITransport>(new NoOpTransport());
    services.AddSingleton(new JsonSerializerOptions());
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    services.AddSingleton<ILifecycleMessageDeserializer>(new NoOpLifecycleMessageDeserializer());
    services.AddSingleton(new WhizbangMetrics());
    services.AddSingleton<TransportMetrics>();
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

    // Since the Ready composite landed, the worker IS registered as a direct singleton — the
    // hosted registration forwards to it so the SAME instance answers as a readiness
    // contributor. Assert at the descriptor level; resolving would need the transport.
    var workerDescriptor = services.FirstOrDefault(
        d => d.ServiceType == typeof(TransportConsumerWorker));
    await Assert.That(workerDescriptor).IsNotNull()
      .Because("Ready waits on the worker's SubscriptionsReady — it must be resolvable by type");
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
  // Hosted Service / Readiness Contributor factory invocation
  // (targets: TransportConsumerBuilderExtensions.cs lines 233, 387, 389)
  // ========================================

  [Test]
  public async Task AddTransportConsumer_ResolvingHostedService_ForwardsToTheSingletonWorkerAsync() {
    // If the IHostedService factory stopped forwarding to the registered TransportConsumerWorker
    // singleton, the host would start a DIFFERENT worker instance than the one the health check
    // and readiness surfaces observe — subscriptions would never actually run even though every
    // other signal reports the worker as registered.
    var services = new ServiceCollection();
    _registerRequiredServices(services);
    _registerWorkerResolutionDependencies(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    builder.AddTransportConsumer();

    var provider = services.BuildServiceProvider();
    var expectedWorker = provider.GetRequiredService<TransportConsumerWorker>();
    var hostedServices = provider.GetServices<IHostedService>().ToList();

    await Assert.That(hostedServices).Contains(expectedWorker)
      .Because("the IHostedService registration must resolve to the SAME singleton worker instance");
  }

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_ResolvingHostedService_ForwardsToTheSingletonWorkerAsync() {
    // Same invariant as the WhizbangBuilder overload, for the WhizbangPerspectiveBuilder chain
    // (e.g. after WithEFCore<T>().WithDriver.Postgres.AddTransportConsumer()) — a broken forward
    // here would mean the perspective-chain host never actually starts the worker it registered.
    var services = new ServiceCollection();
    _registerRequiredServices(services);
    _registerWorkerResolutionDependencies(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });
    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    perspectiveBuilder.AddTransportConsumer();

    var provider = services.BuildServiceProvider();
    var expectedWorker = provider.GetRequiredService<TransportConsumerWorker>();
    var hostedServices = provider.GetServices<IHostedService>().ToList();

    await Assert.That(hostedServices).Contains(expectedWorker)
      .Because("the PerspectiveBuilder overload's IHostedService registration must resolve to the SAME singleton worker instance");
  }

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_ResolvingReadinessContributor_ForwardsToTheSingletonWorkerAsync() {
    // If this factory stopped forwarding to the registered singleton, the app's readiness
    // composite would observe a DIFFERENT TransportConsumerWorker instance than the one actually
    // consuming subscriptions — reporting the app ready before subscriptions are up, or never
    // reporting ready at all.
    var services = new ServiceCollection();
    _registerRequiredServices(services);
    _registerWorkerResolutionDependencies(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });
    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    perspectiveBuilder.AddTransportConsumer();

    var provider = services.BuildServiceProvider();
    var expectedWorker = provider.GetRequiredService<TransportConsumerWorker>();
    var contributors = provider.GetServices<Whizbang.Core.Startup.IStartupReadinessContributor>().ToList();

    await Assert.That(contributors).Contains(expectedWorker)
      .Because("the readiness-contributor registration must resolve to the SAME singleton worker instance");
  }

  // ========================================
  // PerspectiveBuilder Health Check factory — worker-unavailable fallback
  // (targets: TransportConsumerBuilderExtensions.cs lines 401-406)
  // ========================================

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_HealthCheckFactory_FallsBackToEmptyStatesWhenWorkerUnavailableAsync() {
    // If this factory's `worker?.SubscriptionStates ?? new Dictionary(...)` fallback regressed
    // (e.g. swapped for GetRequiredService), invoking the health check from a provider where
    // TransportConsumerWorker isn't resolvable would throw instead of degrading to an
    // empty-subscriptions health check — turning a benign ordering/timing gap into a crash.
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });
    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    perspectiveBuilder.AddTransportConsumer();

    var provider = services.BuildServiceProvider();
    var healthCheckOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
    var registration = healthCheckOptions.Registrations.Single(r => r.Name == "subscriptions");

    // Invoke the factory against a provider that never went through AddTransportConsumer() at
    // all, so TransportConsumerWorker is simply unregistered and GetService<T>() returns null
    // cleanly instead of throwing on a missing ITransport dependency.
    var emptyProvider = new ServiceCollection().BuildServiceProvider();
    var healthCheck = registration.Factory(emptyProvider);

    await Assert.That(healthCheck).IsNotNull();
    await Assert.That(healthCheck).IsTypeOf<SubscriptionHealthCheck>();

    var context = new HealthCheckContext {
      Registration = new HealthCheckRegistration("subscriptions", healthCheck, HealthStatus.Degraded, null)
    };
    var result = await healthCheck.CheckHealthAsync(context);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy)
      .Because("an empty subscription-states dictionary must report healthy, matching "
             + "SubscriptionHealthCheck's own no-subscriptions-configured behavior");
    await Assert.That(result.Description).Contains("No subscriptions");
  }

  // ========================================
  // _getServiceName assembly-name fallback (no IServiceInstanceProvider registered)
  // (targets: TransportConsumerBuilderExtensions.cs lines 424-426, 430)
  // ========================================

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_WithoutServiceInstanceProvider_FallsBackToEntryAssemblyNameAsync() {
    // If IServiceInstanceProvider isn't registered, _getServiceName must fall back to the entry
    // assembly's name (or "UnknownService" if even that is unavailable) rather than throwing —
    // a host that hasn't wired identity yet must still be able to build its subscription set.
    var services = new ServiceCollection();
    _registerRequiredServices(services, includeServiceInstanceProvider: false);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });
    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    perspectiveBuilder.AddTransportConsumer();

    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();

    await Assert.That(options).IsNotNull()
      .Because("resolution must succeed via the assembly-name (or UnknownService) fallback, "
             + "never throw, when no IServiceInstanceProvider is registered");
  }

  // ========================================
  // Test Helpers
  // ========================================

  private sealed class NoOpTransport : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default
    ) => Task.FromResult<ISubscription>(new NoOpSubscription());

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default
    ) where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  /// <summary>Minimal deserializer satisfying TransportConsumerWorker's required-but-nullable
  /// ILifecycleMessageDeserializer constructor parameter — never actually invoked by these tests
  /// since they only resolve the worker, they don't push a message through it.</summary>
  private sealed class NoOpLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => jsonBytes;
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => jsonElement;
  }

  private sealed class NoOpSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;

#pragma warning disable CS0067
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() { }
  }

  private sealed class TestServiceInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public string ServiceName { get; } = serviceName;
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
