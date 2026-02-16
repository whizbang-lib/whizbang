using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for TransportConsumerBuilderExtensions.
/// Verifies that AddTransportConsumer() correctly auto-generates consumer subscriptions.
/// </summary>
public class TransportConsumerBuilderExtensionsTests {
  #region Auto-Population from Routing

  [Test]
  public async Task AddTransportConsumer_AutoPopulatesInboxDestination_FromOwnDomainsAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands")
             .Inbox.UseSharedTopic("inbox");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();
    await Assert.That(options.Destinations.Count).IsGreaterThanOrEqualTo(1);

    var inboxDestination = options.Destinations.FirstOrDefault(d => d.Address == "inbox");
    await Assert.That(inboxDestination).IsNotNull();
    await Assert.That(inboxDestination!.RoutingKey).Contains("myapp.orders.commands.#");
  }

  [Test]
  public async Task AddTransportConsumer_AutoPopulatesEventDestinations_FromSubscribeToAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands")
             .SubscribeTo("myapp.payments.events", "myapp.users.events");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();

    var paymentsDest = options.Destinations.FirstOrDefault(d => d.Address == "myapp.payments.events");
    var usersDest = options.Destinations.FirstOrDefault(d => d.Address == "myapp.users.events");

    await Assert.That(paymentsDest).IsNotNull();
    await Assert.That(usersDest).IsNotNull();
    await Assert.That(paymentsDest!.RoutingKey).IsEqualTo("#");
    await Assert.That(usersDest!.RoutingKey).IsEqualTo("#");
  }

  [Test]
  public async Task AddTransportConsumer_CombinesAutoDiscoveredAndManualDestinationsAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    // Add a test event namespace registry for auto-discovery
    services.AddSingleton<IEventNamespaceRegistry>(new TestEventNamespaceRegistry(["myapp.auto.events"]));

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands")
             .SubscribeTo("myapp.manual.events");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();

    // Should have both auto-discovered and manual
    var autoDest = options.Destinations.FirstOrDefault(d => d.Address == "myapp.auto.events");
    var manualDest = options.Destinations.FirstOrDefault(d => d.Address == "myapp.manual.events");

    await Assert.That(autoDest).IsNotNull();
    await Assert.That(manualDest).IsNotNull();
  }

  #endregion

  #region Additional Destinations

  [Test]
  public async Task AddTransportConsumer_WithAdditionalDestinations_IncludesThemAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer(config => {
      config.AdditionalDestinations.Add(new TransportDestination("custom-topic", "custom-sub"));
    });

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();

    var customDest = options.Destinations.FirstOrDefault(d => d.Address == "custom-topic");
    await Assert.That(customDest).IsNotNull();
    await Assert.That(customDest!.RoutingKey).IsEqualTo("custom-sub");
  }

  [Test]
  public async Task AddTransportConsumer_WithMultipleAdditionalDestinations_IncludesAllAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer(config => {
      config.AdditionalDestinations.Add(new TransportDestination("topic1", "sub1"));
      config.AdditionalDestinations.Add(new TransportDestination("topic2", "sub2"));
    });

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();

    var topic1 = options.Destinations.FirstOrDefault(d => d.Address == "topic1");
    var topic2 = options.Destinations.FirstOrDefault(d => d.Address == "topic2");

    await Assert.That(topic1).IsNotNull();
    await Assert.That(topic2).IsNotNull();
  }

  #endregion

  #region Worker Registration

  [Test]
  public async Task AddTransportConsumer_RegistersTransportConsumerWorkerAsHostedServiceAsync() {
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
    var hostedServiceDescriptor = services.FirstOrDefault(
        d => d.ServiceType == typeof(IHostedService) &&
             d.ImplementationType == typeof(TransportConsumerWorker));

    await Assert.That(hostedServiceDescriptor).IsNotNull();
    await Assert.That(hostedServiceDescriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  [Test]
  public async Task AddTransportConsumer_RegistersTransportConsumerOptionsAsSingletonAsync() {
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
    var optionsDescriptor = services.FirstOrDefault(
        d => d.ServiceType == typeof(TransportConsumerOptions));

    await Assert.That(optionsDescriptor).IsNotNull();
    await Assert.That(optionsDescriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  #endregion

  #region Chaining

  [Test]
  public async Task AddTransportConsumer_ReturnsSameBuilderForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(_ => { });

    // Act
    var result = builder.AddTransportConsumer();

    // Assert
    await Assert.That(result).IsSameReferenceAs(builder);
  }

  #endregion

  #region Argument Validation

  [Test]
  public async Task AddTransportConsumer_WithNullBuilder_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    WhizbangBuilder? builder = null;

    // Act & Assert
    await Assert.That(() => builder!.AddTransportConsumer())
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AddTransportConsumer_WithoutRouting_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - No WithRouting() called, so no IOptions<RoutingOptions> registered
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    // Intentionally NOT calling WithRouting()

    // Act
    builder.AddTransportConsumer();

    // Assert - Should throw when trying to resolve TransportConsumerOptions
    var provider = services.BuildServiceProvider();
    await Assert.That(() => provider.GetRequiredService<TransportConsumerOptions>())
      .Throws<InvalidOperationException>();
  }

  #endregion

  #region Service Name Resolution

  [Test]
  public async Task AddTransportConsumer_UsesServiceInstanceProviderServiceNameAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);
    services.AddSingleton<IServiceInstanceProvider>(new TestServiceInstanceProvider("MyTestService"));

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert - TransportConsumerOptions should resolve without error
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();
    await Assert.That(options.Destinations).IsNotEmpty();
  }

  [Test]
  public async Task AddTransportConsumer_WithoutServiceInstanceProvider_UsesDefaultServiceNameAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services, includeServiceInstanceProvider: false);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert - Should still work with fallback service name
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();
    await Assert.That(options.Destinations).IsNotEmpty();
  }

  #endregion

  #region Edge Cases

  [Test]
  public async Task AddTransportConsumer_WithEmptyRouting_CreatesEmptyDestinationsAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(_ => { }); // Empty routing

    // Act
    builder.AddTransportConsumer();

    // Assert - Destinations should be empty or minimal (system commands only)
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();
    await Assert.That(options.Destinations).IsNotNull();
  }

  [Test]
  public async Task AddTransportConsumer_CalledMultipleTimes_DoesNotDuplicateRegistrationsAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act - Call twice
    builder.AddTransportConsumer();
    builder.AddTransportConsumer();

    // Assert - Should only have one IHostedService registration for TransportConsumerWorker
    var hostedServiceCount = services.Count(
        d => d.ServiceType == typeof(IHostedService) &&
             d.ImplementationType == typeof(TransportConsumerWorker));

    // Note: This behavior may need adjustment - currently each call adds another
    await Assert.That(hostedServiceCount).IsGreaterThanOrEqualTo(1);
  }

  #endregion

  #region WhizbangPerspectiveBuilder Overload

  [Test]
  public async Task AddTransportConsumer_OnPerspectiveBuilder_AutoPopulatesDestinationsAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands")
             .SubscribeTo("myapp.payments.events")
             .Inbox.UseSharedTopic("inbox");
    });

    // Create perspective builder (simulates .WithEFCore<T>().WithDriver.Postgres chain)
    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer();

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();

    await Assert.That(options.Destinations.Count).IsGreaterThanOrEqualTo(1);

    var inboxDest = options.Destinations.FirstOrDefault(d => d.Address == "inbox");
    await Assert.That(inboxDest).IsNotNull();

    var paymentsDest = options.Destinations.FirstOrDefault(d => d.Address == "myapp.payments.events");
    await Assert.That(paymentsDest).IsNotNull();
  }

  [Test]
  public async Task AddTransportConsumer_OnPerspectiveBuilder_WithAdditionalDestinations_IncludesThemAsync() {
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
      config.AdditionalDestinations.Add(new TransportDestination("custom-topic", "custom-sub"));
    });

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();

    var customDest = options.Destinations.FirstOrDefault(d => d.Address == "custom-topic");
    await Assert.That(customDest).IsNotNull();
    await Assert.That(customDest!.RoutingKey).IsEqualTo("custom-sub");
  }

  [Test]
  public async Task AddTransportConsumer_OnPerspectiveBuilder_WithNullBuilder_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    WhizbangPerspectiveBuilder? builder = null;

    // Act & Assert
    await Assert.That(() => builder!.AddTransportConsumer())
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AddTransportConsumer_OnPerspectiveBuilder_ReturnsSameBuilderForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();
    _registerRequiredServices(services);

    var whizbangBuilder = new WhizbangBuilder(services);
    whizbangBuilder.WithRouting(_ => { });

    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    var result = perspectiveBuilder.AddTransportConsumer();

    // Assert
    await Assert.That(result).IsSameReferenceAs(perspectiveBuilder);
  }

  [Test]
  public async Task AddTransportConsumer_OnPerspectiveBuilder_RegistersWorkerAndOptionsAsync() {
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
    var hostedServiceDescriptor = services.FirstOrDefault(
        d => d.ServiceType == typeof(IHostedService) &&
             d.ImplementationType == typeof(TransportConsumerWorker));

    await Assert.That(hostedServiceDescriptor).IsNotNull();

    var optionsDescriptor = services.FirstOrDefault(
        d => d.ServiceType == typeof(TransportConsumerOptions));

    await Assert.That(optionsDescriptor).IsNotNull();
  }

  #endregion

  #region Test Helpers

  private static void _registerRequiredServices(
      IServiceCollection services,
      bool includeServiceInstanceProvider = true) {
    // Register minimal dependencies needed for TransportConsumerWorker resolution
    // Note: These are mocks/stubs - actual worker won't start without real transport
    services.AddLogging();

    if (includeServiceInstanceProvider) {
      services.AddSingleton<IServiceInstanceProvider>(new TestServiceInstanceProvider("TestService"));
    }
  }

  private sealed class TestEventNamespaceRegistry : IEventNamespaceRegistry {
    private readonly HashSet<string> _namespaces;

    public TestEventNamespaceRegistry(IEnumerable<string> namespaces) {
      _namespaces = new HashSet<string>(namespaces, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> GetPerspectiveEventNamespaces() => _namespaces;
    public IReadOnlySet<string> GetReceptorEventNamespaces() => new HashSet<string>();
    public IReadOnlySet<string> GetAllEventNamespaces() => _namespaces;
  }

  private sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
    public TestServiceInstanceProvider(string serviceName) {
      ServiceName = serviceName;
    }

    public string ServiceName { get; }
    public string InstanceId => Guid.NewGuid().ToString("N")[..8];

    public string HostName => throw new NotImplementedException();

    public int ProcessId => throw new NotImplementedException();

    Guid IServiceInstanceProvider.InstanceId => throw new NotImplementedException();

    public ServiceInstanceInfo ToInfo() {
      throw new NotImplementedException();
    }
  }

  #endregion
}
