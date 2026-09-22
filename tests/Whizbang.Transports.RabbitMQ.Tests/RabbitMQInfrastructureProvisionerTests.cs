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

  // ============================================================
  // The provisioning trace
  // ============================================================
  //
  // Exchange declaration is idempotent and happens once at startup, so a service that provisions
  // correctly and one that provisions nothing look identical from outside. These lines are how an
  // operator tells the difference when a deploy comes up against a broker whose topology is
  // missing — and every test above passes NullLogger, whose Debug level is off, so none of them
  // ever ran.

  [Test]
  public async Task ProvisionOwnedDomains_TracesTheCountAndEachExchangeAsync() {
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var logger = new DebugCapturingLogger<RabbitMQInfrastructureProvisioner>();
    var provisioner = new RabbitMQInfrastructureProvisioner(channelPool, logger);

    await provisioner.ProvisionOwnedDomainsAsync(
      new HashSet<string> { "myapp.users", "myapp.orders" });

    await Assert.That(logger.Messages.Any(m => m.Contains("Provisioning", StringComparison.Ordinal)))
      .IsTrue();
    await Assert.That(logger.Messages.Any(m => m.Contains("myapp.users", StringComparison.Ordinal)))
      .IsTrue()
      .Because("naming each exchange is what lets an operator see which half of a topology was "
             + "missing, not just that some of it was");
    await Assert.That(logger.Messages.Any(m => m.Contains("myapp.orders", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task ProvisionOwnedDomains_WithNothingToDo_SaysSoAndTouchesNoChannelAsync() {
    // Renting a channel to declare nothing would open a broker connection on every startup of
    // every service that owns no domains.
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var logger = new DebugCapturingLogger<RabbitMQInfrastructureProvisioner>();
    var provisioner = new RabbitMQInfrastructureProvisioner(channelPool, logger);

    await provisioner.ProvisionOwnedDomainsAsync(new HashSet<string>());

    await Assert.That(channel.DeclaredExchanges).IsEmpty();
    await Assert.That(logger.Messages.Any(m => m.Contains("No owned domains", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task ProvisionOwnedDomains_TracesTheLowercasedExchangeNameAsync() {
    // The declared name is lowercased; the trace has to show what was actually declared rather
    // than what was configured, or a case-mismatch bug reads as correct in the log.
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var logger = new DebugCapturingLogger<RabbitMQInfrastructureProvisioner>();
    var provisioner = new RabbitMQInfrastructureProvisioner(channelPool, logger);

    await provisioner.ProvisionOwnedDomainsAsync(new HashSet<string> { "MyApp.Users" });

    await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange)).Contains("myapp.users");
    await Assert.That(logger.Messages.Any(m => m.Contains("myapp.users", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task ProvisionOwnedDomains_RejectsANullSetAsync() {
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool, NullLogger<RabbitMQInfrastructureProvisioner>.Instance);

    await Assert.That(async () => await provisioner.ProvisionOwnedDomainsAsync(null!))
      .Throws<ArgumentNullException>();
  }

  /// <summary>A logger enabled at every level, so the guarded trace statements actually run.</summary>
  private sealed class DebugCapturingLogger<T> : ILogger<T> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];

    public List<string> Messages {
      get { lock (_lock) { return [.. _messages]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }
}
