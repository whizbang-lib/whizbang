using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Exceptions;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of IInfrastructureProvisioner.
/// Creates topic exchanges for owned domains at worker startup, and — topology arc phase 5 —
/// every entity named by a <see cref="TopologyManifest"/> (manifest-driven DARK provisioning:
/// exchange per topic, queue + bindings per subscription).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#domain-topic-provisioning</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs</tests>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerManifestTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Infrastructure provisioning - startup overhead not critical")]
public sealed class RabbitMQInfrastructureProvisioner : IInfrastructureProvisioner {
  private readonly RabbitMQChannelPool _channelPool;
  private readonly ILogger<RabbitMQInfrastructureProvisioner> _logger;
  private readonly RabbitMQOptions _options;
  private readonly TopologyDriftState? _driftState;

  /// <summary>
  /// Per-process existence cache (the provisioner is a DI singleton): once an entity has been
  /// declared, re-provisioning skips it entirely — the boot declare budget is asserted bounded
  /// in tests, and a re-provision costs zero broker operations.
  /// </summary>
  private readonly ConcurrentDictionary<string, bool> _declaredExchanges = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<(string Exchange, string Queue), bool> _declaredSubscriptions = new();

  /// <summary>
  /// Initializes a new instance of RabbitMQInfrastructureProvisioner.
  /// </summary>
  /// <param name="channelPool">Channel pool for RabbitMQ operations</param>
  /// <param name="logger">Logger instance</param>
  /// <param name="options">Transport options — manifest-provisioned queues use the SAME
  /// argument set (dead-letter exchange, single-active-consumer) as subscribe-time setup;
  /// mismatched arguments would fail the later declare with PRECONDITION_FAILED. Null falls
  /// back to defaults (pre-phase-5 construction sites keep compiling).</param>
  /// <param name="driftState">Topology-drift accumulator for the ownership probe; null
  /// disables recording (the error log still fires).</param>
  public RabbitMQInfrastructureProvisioner(
      RabbitMQChannelPool channelPool,
      ILogger<RabbitMQInfrastructureProvisioner> logger,
      RabbitMQOptions? options = null,
      TopologyDriftState? driftState = null) {
    ArgumentNullException.ThrowIfNull(channelPool);
    ArgumentNullException.ThrowIfNull(logger);

    _channelPool = channelPool;
    _logger = logger;
    _options = options ?? new RabbitMQOptions();
    _driftState = driftState;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsDeclaresExchangeForEachDomainAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsUsesTopicExchangeTypeAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsIsDurableAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsLowercasesExchangeNamesAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsEmptySetDoesNothingAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsCancellationRequestedThrowsAsync</tests>
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

  /// <inheritdoc />
  /// <remarks>
  /// Manifest-driven DARK provisioning (topology arc phase 5): declares an exchange per
  /// manifest topic and — per subscription — the queue (<c>{serviceName}-{exchange}</c>, the
  /// transport's naming convention) plus its bindings, through the SAME
  /// <see cref="RabbitMQEntityProvisioning"/> code path subscribe-time setup uses (argument
  /// parity by construction; mismatched args would PRECONDITION_FAIL the later declare).
  /// Idempotent via the per-process cache. The ownership drift probe runs before this
  /// service's own declares on each owned command inbox.
  /// </remarks>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerManifestTests.cs:ProvisionManifest_SecondCall_PerformsZeroDeclaresAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQInfrastructureProvisionerManifestTests.cs:ProvisionManifest_OwnedInboxExchangeExistsWithoutOurQueue_RecordsDriftAsync</tests>
  public async Task ProvisionManifestAsync(
      TopologyManifest manifest,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(manifest);

    // Publish destinations: exchange per topic (same lowercase convention as owned-domain
    // provisioning — the outbox strategies emit lowercase already).
    using var pooledChannel = await _channelPool.RentAsync(cancellationToken);
    var channel = pooledChannel.Channel;

    foreach (var address in manifest.PublishDestinations.Select(d => d.Address).Distinct(StringComparer.OrdinalIgnoreCase)) {
      cancellationToken.ThrowIfCancellationRequested();

      // Publish-side consumer-provisioned inbox entities (inbox.*, phase 6) are SKIPPED:
      // only the handling service declares its command inbox — a publisher-declared,
      // bindingless inbox exchange would turn "no subscriber provisioned" into a silent
      // broker-side drop instead of the loud UnroutableDestinationException the flip
      // guarantees. (Subscription entities below keep declaring them — that IS the handling
      // service's dark provisioning.)
      if (CommandInboxNaming.IsConsumerProvisionedInboxEntity(address)) {
        continue;
      }

      var exchangeName = address.ToLowerInvariant();
      if (_declaredExchanges.ContainsKey(exchangeName)) {
        continue;
      }

      await channel.ExchangeDeclareAsync(
        exchange: exchangeName,
        type: "topic",
        durable: true,
        autoDelete: false,
        arguments: null,
        passive: false,
        noWait: false,
        cancellationToken: cancellationToken);
      _declaredExchanges[exchangeName] = true;
    }

    // Subscription entities: exchange + queue + bindings, exactly as the transport would
    // create them at subscribe time.
    foreach (var subscription in manifest.Subscriptions) {
      cancellationToken.ThrowIfCancellationRequested();
      var exchangeName = subscription.Topic;
      var queueName = $"{manifest.ServiceName}-{exchangeName}";
      if (_declaredSubscriptions.ContainsKey((exchangeName, queueName))) {
        continue;
      }

      // Ownership drift probe — only on entities this service exclusively OWNS. Must run
      // BEFORE our own declares (declaring the exchange would destroy the observable).
      if (_isOwnedCommandInbox(subscription)) {
        await _probeOwnershipDriftAsync(exchangeName, queueName, cancellationToken);
      }

      await RabbitMQEntityProvisioning.DeclareSubscriptionInfrastructureAsync(
        channel,
        _options,
        exchangeName,
        queueName,
        _routingPatternsOf(subscription),
        _logger,
        cancellationToken,
        controlClass: NamespaceInboxStrategy.IsControlClassSubscription(subscription));
      _declaredSubscriptions[(exchangeName, queueName)] = true;
      _declaredExchanges[exchangeName] = true;
    }
  }

  /// <summary>True when the subscription carries the owned-command-inbox marker stamped by
  /// <see cref="NamespaceInboxStrategy"/>.</summary>
  private static bool _isOwnedCommandInbox(InboxSubscription subscription) =>
    subscription.Metadata is not null
    && subscription.Metadata.TryGetValue(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey, out var marker)
    && marker is true;

  /// <summary>Extracts the routing patterns from subscription metadata (the same
  /// "RoutingPatterns" contract the transport reads off destination metadata); a
  /// pattern-less subscription binds match-all, exactly like the transport's fallback.</summary>
  private static List<string> _routingPatternsOf(InboxSubscription subscription) {
    if (subscription.Metadata is not null
        && subscription.Metadata.TryGetValue("RoutingPatterns", out var value)
        && value is IEnumerable<string> patterns) {
      var list = patterns.ToList();
      if (list.Count > 0) {
        return list;
      }
    }
    if (!string.IsNullOrEmpty(subscription.FilterExpression)) {
      return [.. subscription.FilterExpression.Split(',', StringSplitOptions.RemoveEmptyEntries)];
    }
    return ["#"];
  }

  /// <summary>
  /// The census-mandated runtime ownership check, RabbitMQ shape. AMQP cannot enumerate the
  /// queues bound to an exchange, so the observable is indirect: during the DARK phase only a
  /// subscribing owner creates <c>inbox.&lt;ns&gt;</c> entities, so a command-inbox exchange
  /// that ALREADY exists while OUR conventional queue does not means another service claimed
  /// the entity — structured error log + drift finding (health Degraded via
  /// <c>TopologyDriftHealthSource</c>). Best-effort by design: a same-name queue recreated by
  /// an operator can mask it, and it never blocks boot. Each passive probe uses a dedicated
  /// rented channel because a failed passive declare closes the channel (the pool discards
  /// closed channels on return).
  /// </summary>
  private async Task _probeOwnershipDriftAsync(
      string exchangeName, string queueName, CancellationToken cancellationToken) {
    try {
      using var probe = await _channelPool.RentAsync(cancellationToken);
      try {
        await probe.Channel.ExchangeDeclarePassiveAsync(exchangeName, cancellationToken);
      } catch (OperationInterruptedException) {
        // Exchange doesn't exist — fresh entity, the expected first-boot path.
        return;
      }

      using var queueProbe = await _channelPool.RentAsync(cancellationToken);
      try {
        await queueProbe.Channel.QueueDeclarePassiveAsync(queueName, cancellationToken);
        // Both exist — a previous boot of THIS service explains the exchange. Not drift.
      } catch (OperationInterruptedException) {
        _logger.LogError(
          "Topology ownership drift: command inbox exchange '{ExchangeName}' already exists but this service's queue '{QueueName}' does not — another service appears to have claimed the entity; one service owns a command namespace",
          exchangeName,
          queueName);
        _driftState?.Record(new TopologyDriftFinding(
          exchangeName,
          $"(unknown queue; exchange pre-existed without '{queueName}')",
          "command-inbox exchange existed before this service ever provisioned it"));
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Best-effort probe: broker/channel-pool failures must never block provisioning.
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(ex, "Ownership drift probe on '{ExchangeName}' skipped (probe failed)", exchangeName);
      }
    }
  }
}
