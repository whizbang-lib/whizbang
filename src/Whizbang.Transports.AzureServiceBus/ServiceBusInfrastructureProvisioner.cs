using System.Collections.Concurrent;
using Azure;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Azure Service Bus implementation of IInfrastructureProvisioner.
/// Creates topics for owned domains at worker startup, and — topology arc phase 5 — every
/// entity named by a <see cref="TopologyManifest"/> (manifest-driven DARK provisioning).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#domain-topic-provisioning</docs>
/// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs</tests>
/// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerManifestTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Infrastructure provisioning - startup overhead not critical")]
public sealed class ServiceBusInfrastructureProvisioner : IInfrastructureProvisioner {
  private readonly IServiceBusAdminClient _adminClient;
  private readonly ILogger<ServiceBusInfrastructureProvisioner> _logger;
  private readonly AzureServiceBusOptions _options;
  private readonly TopologyDriftState? _driftState;

  /// <summary>
  /// Per-process existence cache (the provisioner is a DI singleton): once an entity has been
  /// confirmed or created, re-provisioning skips its existence check entirely — the boot
  /// management-op budget is asserted bounded in tests, and a re-provision costs zero ops.
  /// </summary>
  private readonly ConcurrentDictionary<string, bool> _knownTopics = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConcurrentDictionary<(string Topic, string Subscription), bool> _knownSubscriptions = new();

  /// <summary>
  /// Initializes a new instance of ServiceBusInfrastructureProvisioner.
  /// </summary>
  /// <param name="adminClient">Admin client for Service Bus operations</param>
  /// <param name="logger">Logger instance</param>
  /// <param name="options">Transport options — manifest-provisioned subscriptions use the SAME
  /// session/lock/delivery settings as subscribe-time auto-provision. Null falls back to
  /// defaults (pre-phase-5 construction sites keep compiling).</param>
  /// <param name="driftState">Topology-drift accumulator for the ownership check; null
  /// disables recording (the error log still fires).</param>
  public ServiceBusInfrastructureProvisioner(
      IServiceBusAdminClient adminClient,
      ILogger<ServiceBusInfrastructureProvisioner> logger,
      AzureServiceBusOptions? options = null,
      TopologyDriftState? driftState = null) {
    ArgumentNullException.ThrowIfNull(adminClient);
    ArgumentNullException.ThrowIfNull(logger);

    _adminClient = adminClient;
    _logger = logger;
    _options = options ?? new AzureServiceBusOptions();
    _driftState = driftState;
  }

  /// <inheritdoc />
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsCreatesTopicForEachDomainAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsSkipsExistingTopicsAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsLowercasesTopicNamesAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsEmptySetDoesNothingAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsCancellationRequestedThrowsAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsTopicAlreadyExistsHandlesRaceAsync</tests>
  public Task ProvisionOwnedDomainsAsync(
      IReadOnlySet<string> ownedDomains,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ownedDomains);

    if (ownedDomains.Count == 0) {
      _logger.LogDebug("No owned domains to provision");
      return Task.CompletedTask;
    }

    cancellationToken.ThrowIfCancellationRequested();
    return _provisionOwnedDomainsCoreAsync(ownedDomains, cancellationToken);
  }

  private async Task _provisionOwnedDomainsCoreAsync(
      IReadOnlySet<string> ownedDomains,
      CancellationToken cancellationToken) {
    if (_logger.IsEnabled(LogLevel.Information)) {
      var count = ownedDomains.Count;
      _logger.LogInformation(
        "Provisioning {Count} Azure Service Bus topics for owned domains",
        count);
    }

    foreach (var domain in ownedDomains) {
      cancellationToken.ThrowIfCancellationRequested();
      await _ensureTopicAsync(domain.ToLowerInvariant(), cancellationToken);
    }
  }

  /// <inheritdoc />
  /// <docs>messaging/transports/azure-service-bus#publish-auto-provisioning</docs>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_TopicDoesNotExist_CreatesItAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_TopicAlreadyExists_DoesNothingAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_RaceCondition_HandlesGracefullyAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_LowercasesTopicNameAsync</tests>
  public Task EnsureTopicExistsAsync(
      string topicName,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(topicName);
    return _ensureTopicAsync(topicName.ToLowerInvariant(), cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Manifest-driven DARK provisioning (topology arc phase 5): ensures each manifest topic
  /// (publish destinations ∪ subscription topics) exists, then each subscription — named
  /// exactly as the transport names it (<see cref="ServiceBusSubscriptionNameHelper"/>) and
  /// created through the SAME <see cref="ServiceBusEntityProvisioning"/> code path the
  /// subscribe-time auto-provision uses (session/lock/delivery parity by construction).
  /// Idempotent via the per-process existence cache; the ownership drift check runs once per
  /// owned command inbox per process.
  /// </remarks>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerManifestTests.cs:ProvisionManifest_SecondCall_PerformsZeroManagementOpsAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerManifestTests.cs:ProvisionManifest_OwnedCommandInbox_ForeignSubscription_RecordsDriftAsync</tests>
  public async Task ProvisionManifestAsync(
      TopologyManifest manifest,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(manifest);

    // Topics: the union of publish destinations and subscription entities.
    var topics = manifest.PublishDestinations.Select(d => d.Address)
      .Concat(manifest.Subscriptions.Select(s => s.Topic))
      .Select(t => t.ToLowerInvariant())
      .Distinct(StringComparer.OrdinalIgnoreCase);

    foreach (var topicName in topics) {
      cancellationToken.ThrowIfCancellationRequested();
      if (_knownTopics.ContainsKey(topicName)) {
        continue;
      }
      await _ensureTopicAsync(topicName, cancellationToken);
      _knownTopics[topicName] = true;
    }

    foreach (var subscription in manifest.Subscriptions) {
      cancellationToken.ThrowIfCancellationRequested();
      var topicName = subscription.Topic.ToLowerInvariant();
      var subscriptionName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(
        manifest.ServiceName, topicName);
      if (_knownSubscriptions.ContainsKey((topicName, subscriptionName))) {
        continue;
      }

      // Ownership drift check — only on entities this service exclusively OWNS (per-namespace
      // command inboxes); the shared/system entities legitimately carry every service.
      if (_isOwnedCommandInbox(subscription)) {
        await _checkOwnershipDriftAsync(topicName, subscriptionName, cancellationToken);
      }

      if (!await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken)) {
        try {
          await ServiceBusEntityProvisioning.CreateSubscriptionAsync(
            _adminClient, _options, topicName, subscriptionName, _logger, cancellationToken);
        } catch (RequestFailedException ex) when (ex.Status == 409) {
          // Race condition — another instance created it, safe to ignore
          if (_logger.IsEnabled(LogLevel.Debug)) {
            _logger.LogDebug(ex, "Subscription {TopicName}/{SubscriptionName} already exists (409 conflict)", topicName, subscriptionName);
          }
        }

        // Same-as-subscribe-time filter shape: a freshly created subscription with routing
        // patterns must not sit filterless until first subscribe (an unfiltered shared-inbox
        // subscription would buffer EVERY command in the window).
        var routingPatterns = _routingPatternsOf(subscription);
        if (routingPatterns.Count > 0) {
          await ServiceBusEntityProvisioning.ApplyRoutingPatternFilterAsync(
            _adminClient, topicName, subscriptionName, routingPatterns, _logger, cancellationToken);
        }
      }

      _knownSubscriptions[(topicName, subscriptionName)] = true;
    }
  }

  /// <summary>True when the subscription carries the owned-command-inbox marker stamped by
  /// <see cref="NamespaceInboxStrategy"/>.</summary>
  private static bool _isOwnedCommandInbox(InboxSubscription subscription) =>
    subscription.Metadata is not null
    && subscription.Metadata.TryGetValue(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey, out var marker)
    && marker is true;

  /// <summary>Extracts the routing patterns from subscription metadata (the same
  /// "RoutingPatterns" contract the transports read off destination metadata).</summary>
  private static IReadOnlyList<string> _routingPatternsOf(InboxSubscription subscription) {
    if (subscription.Metadata is not null
        && subscription.Metadata.TryGetValue("RoutingPatterns", out var value)
        && value is IEnumerable<string> patterns) {
      return [.. patterns];
    }
    return [];
  }

  /// <summary>
  /// The census-mandated runtime ownership check: enumerates existing subscriptions on a
  /// command inbox this service is about to own and flags every foreign one — structured error
  /// log + drift-state finding (health Degraded via <c>TopologyDriftHealthSource</c>). Never
  /// blocks boot: a duplicate subscriber duplicates commands, it does not make THIS service
  /// less able to serve.
  /// </summary>
  private async Task _checkOwnershipDriftAsync(
      string topicName, string ownSubscriptionName, CancellationToken cancellationToken) {
    try {
      await foreach (var existing in _adminClient.GetSubscriptionsAsync(topicName, cancellationToken)) {
        if (string.Equals(existing.SubscriptionName, ownSubscriptionName, StringComparison.OrdinalIgnoreCase)) {
          continue;
        }
        _logger.LogError(
          "Topology ownership drift: command inbox '{TopicName}' already carries a second service's subscription '{ForeignSubscription}' — one service owns a command namespace; a duplicate subscriber receives (and may double-handle) every command on it",
          topicName,
          existing.SubscriptionName);
        _driftState?.Record(new TopologyDriftFinding(
          topicName,
          existing.SubscriptionName,
          "second service's subscription observed on an owned command inbox during provisioning"));
      }
    } catch (RequestFailedException ex) {
      // Best-effort check: enumeration failures (topic racing into existence, transient
      // management-plane errors) must never block provisioning or startup.
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(ex, "Ownership drift check on '{TopicName}' skipped (enumeration failed)", topicName);
      }
    }
  }

  /// <summary>
  /// Shared logic for ensuring a single topic exists.
  /// Handles check-exists-then-create with 409 race condition tolerance.
  /// </summary>
  private async Task _ensureTopicAsync(string topicName, CancellationToken cancellationToken) {
    try {
      if (await _adminClient.TopicExistsAsync(topicName, cancellationToken)) {
        if (_logger.IsEnabled(LogLevel.Debug)) {
          _logger.LogDebug("Topic '{Topic}' already exists, skipping", topicName);
        }
        return;
      }

      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug("Creating topic '{Topic}'", topicName);
      }

      await _adminClient.CreateTopicAsync(topicName, cancellationToken);

      if (_logger.IsEnabled(LogLevel.Information)) {
        _logger.LogInformation("Provisioned topic '{Topic}'", topicName);
      }
    } catch (RequestFailedException ex) when (ex.Status == 409) {
      // Race condition - topic created by another instance between exists check and create
      if (_logger.IsEnabled(LogLevel.Debug)) {
        _logger.LogDebug(ex, "Topic '{Topic}' already exists (race condition), skipping", topicName);
      }
    }
  }
}
