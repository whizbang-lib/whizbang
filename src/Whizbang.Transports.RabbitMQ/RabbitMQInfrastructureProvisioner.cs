using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of IInfrastructureProvisioner.
/// Creates topic exchanges for owned domains at worker startup.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#domain-topic-provisioning</docs>
/// <tests>Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Infrastructure provisioning - startup overhead not critical")]
public sealed class RabbitMQInfrastructureProvisioner : IInfrastructureProvisioner {
  private readonly RabbitMQChannelPool _channelPool;
  private readonly ILogger<RabbitMQInfrastructureProvisioner> _logger;

  /// <summary>
  /// Initializes a new instance of RabbitMQInfrastructureProvisioner.
  /// </summary>
  /// <param name="channelPool">Channel pool for RabbitMQ operations</param>
  /// <param name="logger">Logger instance</param>
  public RabbitMQInfrastructureProvisioner(
      RabbitMQChannelPool channelPool,
      ILogger<RabbitMQInfrastructureProvisioner> logger) {
    ArgumentNullException.ThrowIfNull(channelPool);
    ArgumentNullException.ThrowIfNull(logger);

    _channelPool = channelPool;
    _logger = logger;
  }

  /// <inheritdoc />
  /// <tests>Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsDeclaresExchangeForEachDomainAsync</tests>
  /// <tests>Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsUsesTopicExchangeTypeAsync</tests>
  /// <tests>Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsIsDurableAsync</tests>
  /// <tests>Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsLowercasesExchangeNamesAsync</tests>
  /// <tests>Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsEmptySetDoesNothingAsync</tests>
  /// <tests>Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsCancellationRequestedThrowsAsync</tests>
  public async Task ProvisionOwnedDomainsAsync(
      IReadOnlySet<string> ownedDomains,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ownedDomains);

    if (ownedDomains.Count == 0) {
      _logger.LogDebug("No owned domains to provision");
      return;
    }

    cancellationToken.ThrowIfCancellationRequested();

    if (_logger.IsEnabled(LogLevel.Debug)) {
      var count = ownedDomains.Count;
      _logger.LogDebug(
        "Provisioning {Count} RabbitMQ exchanges for owned domains",
        count);
    }

    // Rent channel from pool (RAII pattern - automatically returned on dispose)
    using var pooledChannel = await _channelPool.RentAsync(cancellationToken);
    var channel = pooledChannel.Channel;

    foreach (var domain in ownedDomains) {
      cancellationToken.ThrowIfCancellationRequested();

      var exchangeName = domain.ToLowerInvariant();

      if (_logger.IsEnabled(LogLevel.Debug)) {
        var domainName = domain;
        _logger.LogDebug(
          "Declaring exchange '{Exchange}' for owned domain '{Domain}'",
          exchangeName,
          domainName);
      }

      // Declare exchange (idempotent - safe to call multiple times)
      await channel.ExchangeDeclareAsync(
        exchange: exchangeName,
        type: "topic",
        durable: true,
        autoDelete: false,
        arguments: null,
        passive: false,
        noWait: false,
        cancellationToken: cancellationToken);

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(
          "Provisioned exchange '{Exchange}' for owned domain",
          exchangeName);
      }
    }
  }
}
