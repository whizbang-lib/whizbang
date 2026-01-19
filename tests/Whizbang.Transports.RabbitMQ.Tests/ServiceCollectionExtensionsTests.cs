using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQ dependency injection extensions.
/// </summary>
public class ServiceCollectionExtensionsTests {
  [Test]
  public async Task AddRabbitMQTransport_RegistersTransport_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();
    var connectionString = "amqp://guest:guest@localhost:5672/";

    // Register a fake connection for testing
    services.AddSingleton<IConnection>(sp => {
      var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
      return fakeConnection;
    });

    // Act
    services.AddRabbitMQTransport(connectionString);
    var provider = services.BuildServiceProvider();

    // Assert
    var transport1 = provider.GetService<ITransport>();
    var transport2 = provider.GetService<ITransport>();

    await Assert.That(transport1).IsNotNull();
    await Assert.That(transport2).IsNotNull();
    await Assert.That(ReferenceEquals(transport1, transport2)).IsTrue(); // Singleton check
  }

  [Test]
  public async Task AddRabbitMQTransport_ReusesExistingConnection_IfRegisteredAsync() {
    // Arrange
    var services = new ServiceCollection();
    var connectionString = "amqp://guest:guest@localhost:5672/";

    // Pre-register a connection
    var existingConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    services.AddSingleton<IConnection>(existingConnection);

    // Act
    services.AddRabbitMQTransport(connectionString);
    var provider = services.BuildServiceProvider();

    // Assert
    var registeredConnection = provider.GetService<IConnection>();
    await Assert.That(ReferenceEquals(registeredConnection, existingConnection)).IsTrue();
  }

  [Test]
  public async Task AddRabbitMQTransport_InitializesTransport_DuringRegistrationAsync() {
    // Arrange
    var services = new ServiceCollection();
    var connectionString = "amqp://guest:guest@localhost:5672/";

    // Register a fake connection that reports as open
    services.AddSingleton<IConnection>(sp => {
      var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
      return fakeConnection;
    });

    // Act
    services.AddRabbitMQTransport(connectionString);
    var provider = services.BuildServiceProvider();
    var transport = provider.GetService<ITransport>();

    // Assert
    await Assert.That(transport).IsNotNull();
    await Assert.That(transport!.IsInitialized).IsTrue(); // Should be initialized during registration
  }

  [Test]
  public async Task AddRabbitMQTransport_WithNullConnectionString_ThrowsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act & Assert
    await Assert.That(() => services.AddRabbitMQTransport(null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task AddRabbitMQTransport_WithEmptyConnectionString_ThrowsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act & Assert
    await Assert.That(() => services.AddRabbitMQTransport(string.Empty))
      .Throws<ArgumentException>();
  }
}
