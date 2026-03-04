using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Core;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQInfrastructureProvisioner.
/// Verifies exchange provisioning for owned domains.
/// </summary>
public class RabbitMQInfrastructureProvisionerTests {
  /// <summary>
  /// When provisioning owned domains, should declare a topic exchange for each domain.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsDeclaresExchangeForEachDomainAsync() {
    // Arrange
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool,
      NullLogger<RabbitMQInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "myapp.users", "myapp.orders", "myapp.inventory" };

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert
    await Assert.That(channel.DeclaredExchanges.Count).IsEqualTo(3);
    await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange))
      .Contains("myapp.users")
      .And.Contains("myapp.orders")
      .And.Contains("myapp.inventory");
  }

  /// <summary>
  /// When provisioning, should use topic exchange type.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsUsesTopicExchangeTypeAsync() {
    // Arrange
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool,
      NullLogger<RabbitMQInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "myapp.users" };

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert
    await Assert.That(channel.DeclaredExchanges).HasSingleItem();
    await Assert.That(channel.DeclaredExchanges[0].Type).IsEqualTo("topic");
  }

  /// <summary>
  /// When provisioning, exchanges should be durable for persistence.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsIsDurableAsync() {
    // Arrange
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool,
      NullLogger<RabbitMQInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "myapp.users" };

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert
    await Assert.That(channel.DeclaredExchanges).HasSingleItem();
    await Assert.That(channel.DeclaredExchanges[0].Durable).IsTrue();
    await Assert.That(channel.DeclaredExchanges[0].AutoDelete).IsFalse();
  }

  /// <summary>
  /// Exchange names should be lowercased for consistency.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsLowercasesExchangeNamesAsync() {
    // Arrange
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool,
      NullLogger<RabbitMQInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "MyApp.Users", "MYAPP.ORDERS" };

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert
    await Assert.That(channel.DeclaredExchanges.Count).IsEqualTo(2);
    await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange))
      .Contains("myapp.users")
      .And.Contains("myapp.orders");
  }

  /// <summary>
  /// When owned domains set is empty, should not declare any exchanges.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsEmptySetDoesNothingAsync() {
    // Arrange
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool,
      NullLogger<RabbitMQInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string>();

    // Act
    await provisioner.ProvisionOwnedDomainsAsync(ownedDomains);

    // Assert
    await Assert.That(channel.DeclaredExchanges).IsEmpty();
  }

  /// <summary>
  /// When cancellation is requested, should throw OperationCanceledException.
  /// </summary>
  [Test]
  public async Task ProvisionOwnedDomainsCancellationRequestedThrowsAsync() {
    // Arrange
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool,
      NullLogger<RabbitMQInfrastructureProvisioner>.Instance);

    var ownedDomains = new HashSet<string> { "myapp.users" };
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(
      () => provisioner.ProvisionOwnedDomainsAsync(ownedDomains, cts.Token));
  }
}
