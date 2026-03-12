using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for TransportConsumerBuilderExtensions targeting uncovered service name fallback
/// paths and PerspectiveBuilder overload with configure action.
/// </summary>
public class TransportConsumerBuilderExtensionsServiceNameTests {

  // ========================================
  // WhizbangBuilder - null guard
  // ========================================

  [Test]
  public async Task AddTransportConsumer_NullBuilder_ThrowsArgumentNullExceptionAsync() {
    WhizbangBuilder builder = null!;

    await Assert.That(() => builder.AddTransportConsumer())
      .Throws<ArgumentNullException>();
  }

  // ========================================
  // PerspectiveBuilder - null guard
  // ========================================

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_NullBuilder_ThrowsArgumentNullExceptionAsync() {
    WhizbangPerspectiveBuilder builder = null!;

    await Assert.That(() => builder.AddTransportConsumer())
      .Throws<ArgumentNullException>();
  }

  // ========================================
  // WhizbangBuilder - without routing throws on resolution
  // ========================================

  [Test]
  public async Task AddTransportConsumer_WithoutRouting_ThrowsOnResolutionAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(new TestProvider("TestSvc"));

    var builder = new WhizbangBuilder(services);
    // Note: No WithRouting() call

    // Act
    builder.AddTransportConsumer();

    // Assert - should throw when resolving TransportConsumerOptions
    var provider = services.BuildServiceProvider();
    await Assert.That(() => provider.GetRequiredService<TransportConsumerOptions>())
      .Throws<InvalidOperationException>();
  }

  // ========================================
  // WhizbangBuilder - with configure action adds additional destinations
  // ========================================

  [Test]
  public async Task AddTransportConsumer_WithConfigureAction_AddsAdditionalDestinationsAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(new TestProvider("MySvc"));

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer(config => {
      config.AdditionalDestinations.Add(new TransportDestination("custom-topic", "custom-key"));
    });

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();

    // Should have at least 1 auto-generated destination + 1 additional
    await Assert.That(options.Destinations.Count).IsGreaterThanOrEqualTo(1)
      .Because("Should have at least the custom destination");

    var hasCustomDestination = options.Destinations
      .Any(d => d.Address == "custom-topic" && d.RoutingKey == "custom-key");
    await Assert.That(hasCustomDestination).IsTrue()
      .Because("Additional destination should be included");
  }

  // ========================================
  // PerspectiveBuilder - with configure action adds additional destinations
  // ========================================

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_WithConfigureAction_AddsDestinationsAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(new TestProvider("MySvc"));

    var whizbangBuilder = new WhizbangBuilder(services);
    whizbangBuilder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer(config => {
      config.AdditionalDestinations.Add(new TransportDestination("extra-topic"));
      config.ResilienceOptions.InitialRetryAttempts = 7;
    });

    // Assert
    var provider = services.BuildServiceProvider();
    var resilienceOptions = provider.GetService<SubscriptionResilienceOptions>();
    await Assert.That(resilienceOptions).IsNotNull();
    await Assert.That(resilienceOptions!.InitialRetryAttempts).IsEqualTo(7);

    var options = provider.GetRequiredService<TransportConsumerOptions>();
    var hasExtraTopic = options.Destinations.Any(d => d.Address == "extra-topic");
    await Assert.That(hasExtraTopic).IsTrue();
  }

  // ========================================
  // ServiceName fallback - no IServiceInstanceProvider
  // ========================================

  [Test]
  public async Task AddTransportConsumer_WithoutServiceInstanceProvider_UsesAssemblyNameFallbackAsync() {
    // Arrange - No IServiceInstanceProvider registered
    var services = new ServiceCollection();
    services.AddLogging();

    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    // Act
    builder.AddTransportConsumer();

    // Assert - should resolve without error, using assembly name fallback
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();
    await Assert.That(options).IsNotNull();
  }

  // ========================================
  // PerspectiveBuilder - ServiceName fallback
  // ========================================

  [Test]
  public async Task AddTransportConsumer_PerspectiveBuilder_WithoutServiceInstanceProvider_ResolvesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    var whizbangBuilder = new WhizbangBuilder(services);
    whizbangBuilder.WithRouting(routing => {
      routing.OwnDomains("myapp.orders.commands");
    });

    var perspectiveBuilder = new WhizbangPerspectiveBuilder(services);

    // Act
    perspectiveBuilder.AddTransportConsumer();

    // Assert
    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<TransportConsumerOptions>();
    await Assert.That(options).IsNotNull();
  }

  // ========================================
  // TransportConsumerConfiguration defaults
  // ========================================

  [Test]
  public async Task TransportConsumerConfiguration_DefaultAdditionalDestinations_IsEmptyAsync() {
    var config = new TransportConsumerConfiguration();
    await Assert.That(config.AdditionalDestinations.Count).IsEqualTo(0);
  }

  [Test]
  public async Task TransportConsumerConfiguration_DefaultResilienceOptions_IsNotNullAsync() {
    var config = new TransportConsumerConfiguration();
    await Assert.That(config.ResilienceOptions).IsNotNull();
  }

  // ========================================
  // Test Helpers
  // ========================================

  private sealed class TestProvider : IServiceInstanceProvider {
    public TestProvider(string serviceName) { ServiceName = serviceName; }
    public string ServiceName { get; }
    public Guid InstanceId => Guid.NewGuid();
    public string HostName => "test-host";
    public int ProcessId => Environment.ProcessId;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
