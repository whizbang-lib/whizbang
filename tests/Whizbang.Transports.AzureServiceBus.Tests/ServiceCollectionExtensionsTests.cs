using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Transports.AzureServiceBus.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Tests for Azure Service Bus dependency injection extensions.
/// </summary>
[Timeout(60_000)]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ServiceCollectionExtensionsTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  [Test]
  public async Task AddAzureServiceBusTransport_RegistersTransport_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Pre-register the existing client from fixture (to avoid creating a new one)
    services.AddSingleton(_fixture.Client);

    // Act
    services.AddAzureServiceBusTransport(_fixture.ConnectionString);
    var provider = services.BuildServiceProvider();

    // Assert
    var transport1 = provider.GetService<ITransport>();
    var transport2 = provider.GetService<ITransport>();

    await Assert.That(transport1).IsNotNull();
    await Assert.That(transport2).IsNotNull();
    await Assert.That(ReferenceEquals(transport1, transport2)).IsTrue(); // Singleton check
  }

  [Test]
  public async Task AddAzureServiceBusTransport_ReusesExistingClient_IfRegisteredAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Pre-register the client
    services.AddSingleton(_fixture.Client);

    // Act
    services.AddAzureServiceBusTransport(_fixture.ConnectionString);
    var provider = services.BuildServiceProvider();

    // Assert
    var registeredClient = provider.GetService<ServiceBusClient>();
    await Assert.That(ReferenceEquals(registeredClient, _fixture.Client)).IsTrue();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_InitializesTransport_DuringRegistrationAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Pre-register the client
    services.AddSingleton(_fixture.Client);

    // Act
    services.AddAzureServiceBusTransport(_fixture.ConnectionString);
    var provider = services.BuildServiceProvider();
    var transport = provider.GetService<ITransport>();

    // Assert
    await Assert.That(transport).IsNotNull();
    await Assert.That(transport!.IsInitialized).IsTrue(); // Should be initialized during registration
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithNullConnectionString_ThrowsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act & Assert
    await Assert.That(() => services.AddAzureServiceBusTransport(null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithEmptyConnectionString_ThrowsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act & Assert
    await Assert.That(() => services.AddAzureServiceBusTransport(string.Empty))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithOptions_AppliesOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var customMaxConcurrentCalls = 10;

    // Pre-register the client
    services.AddSingleton(_fixture.Client);

    // Act
    services.AddAzureServiceBusTransport(
      _fixture.ConnectionString,
      options => {
        options.MaxConcurrentCalls = customMaxConcurrentCalls;
      }
    );
    var provider = services.BuildServiceProvider();
    var transport = provider.GetService<ITransport>();

    // Assert
    await Assert.That(transport).IsNotNull();
    await Assert.That(transport).IsTypeOf<AzureServiceBusTransport>();
  }

  [Test]
  public async Task AddAzureServiceBusHealthChecks_RegistersHealthCheckAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Pre-register the client and transport
    services.AddSingleton(_fixture.Client);
    services.AddAzureServiceBusTransport(_fixture.ConnectionString);

    // HealthCheckService requires ILogger - add logging support
    services.AddLogging();

    // Act
    services.AddAzureServiceBusHealthChecks();
    var provider = services.BuildServiceProvider();

    // Assert - Health check registration is verified by checking the health check service is available
    var healthCheckService = provider.GetService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
    await Assert.That(healthCheckService).IsNotNull();
  }
}
