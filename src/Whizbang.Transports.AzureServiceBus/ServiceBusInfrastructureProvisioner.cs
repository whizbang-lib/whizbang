using Azure;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Azure Service Bus implementation of IInfrastructureProvisioner.
/// Creates topics for owned domains at worker startup.
/// </summary>
/// <docs>core-concepts/routing#domain-topic-provisioning</docs>
/// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Infrastructure provisioning - startup overhead not critical")]
public sealed class ServiceBusInfrastructureProvisioner : IInfrastructureProvisioner {
  private readonly IServiceBusAdminClient _adminClient;
  private readonly ILogger<ServiceBusInfrastructureProvisioner> _logger;

  /// <summary>
  /// Initializes a new instance of ServiceBusInfrastructureProvisioner.
  /// </summary>
  /// <param name="adminClient">Admin client for Service Bus operations</param>
  /// <param name="logger">Logger instance</param>
  public ServiceBusInfrastructureProvisioner(
      IServiceBusAdminClient adminClient,
      ILogger<ServiceBusInfrastructureProvisioner> logger) {
    ArgumentNullException.ThrowIfNull(adminClient);
    ArgumentNullException.ThrowIfNull(logger);

    _adminClient = adminClient;
    _logger = logger;
  }

  /// <inheritdoc />
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsCreatesTopicForEachDomainAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsSkipsExistingTopicsAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsLowercasesTopicNamesAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsEmptySetDoesNothingAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsCancellationRequestedThrowsAsync</tests>
  /// <tests>Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:ProvisionOwnedDomainsTopicAlreadyExistsHandlesRaceAsync</tests>
  public async Task ProvisionOwnedDomainsAsync(
      IReadOnlySet<string> ownedDomains,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ownedDomains);

    if (ownedDomains.Count == 0) {
      _logger.LogDebug("No owned domains to provision");
      return;
    }

    cancellationToken.ThrowIfCancellationRequested();

    _logger.LogInformation(
      "Provisioning {Count} Azure Service Bus topics for owned domains",
      ownedDomains.Count);

    foreach (var domain in ownedDomains) {
      cancellationToken.ThrowIfCancellationRequested();

      var topicName = domain.ToLowerInvariant();

      try {
        // Check if topic already exists
        if (await _adminClient.TopicExistsAsync(topicName, cancellationToken)) {
          _logger.LogDebug(
            "Topic '{Topic}' already exists, skipping",
            topicName);
          continue;
        }

        _logger.LogDebug(
          "Creating topic '{Topic}' for owned domain '{Domain}'",
          topicName,
          domain);

        await _adminClient.CreateTopicAsync(topicName, cancellationToken);

        _logger.LogInformation(
          "Provisioned topic '{Topic}' for owned domain",
          topicName);
      } catch (RequestFailedException ex) when (ex.Status == 409) {
        // Race condition - topic created by another instance between exists check and create
        _logger.LogDebug(
          "Topic '{Topic}' already exists (race condition), skipping",
          topicName);
      }
    }
  }
}
