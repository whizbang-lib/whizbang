using System.Runtime.CompilerServices;
using Azure.Messaging.ServiceBus.Administration;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Wrapper for ServiceBusAdministrationClient that implements IServiceBusAdminClient.
/// Provides a testable abstraction over the Azure SDK's sealed classes.
/// </summary>
/// <docs>messaging/transports/azure-service-bus#admin-client</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs</tests>
public sealed class ServiceBusAdminClientWrapper : IServiceBusAdminClient {
  private readonly ServiceBusAdministrationClient _adminClient;

  /// <summary>
  /// Initializes a new instance wrapping a ServiceBusAdministrationClient.
  /// </summary>
  /// <param name="adminClient">The underlying admin client</param>
  public ServiceBusAdminClientWrapper(ServiceBusAdministrationClient adminClient) {
    ArgumentNullException.ThrowIfNull(adminClient);
    _adminClient = adminClient;
  }

  #region Namespace Management

  /// <inheritdoc />
  public async Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default) {
    var response = await _adminClient.GetNamespacePropertiesAsync(cancellationToken);
    return response.Value;
  }

  #endregion

  #region Topic Management

  /// <inheritdoc />
  public async Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) {
    var response = await _adminClient.TopicExistsAsync(topicName, cancellationToken);
    return response.Value;
  }

  /// <inheritdoc />
  public async Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) {
    var options = new CreateTopicOptions(topicName) {
      SupportOrdering = true
    };
    await _adminClient.CreateTopicAsync(options, cancellationToken);
  }

  #endregion

  #region Subscription Management

  /// <inheritdoc />
  public async Task<bool> SubscriptionExistsAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken = default) {
    var response = await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken);
    return response.Value;
  }

  /// <inheritdoc />
  public async Task CreateSubscriptionAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken = default) {
    await _adminClient.CreateSubscriptionAsync(topicName, subscriptionName, cancellationToken);
  }

  #endregion

  #region Rule Management

  /// <inheritdoc />
  public async IAsyncEnumerable<RuleProperties> GetRulesAsync(
    string topicName,
    string subscriptionName,
    [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    await foreach (var rule in _adminClient.GetRulesAsync(topicName, subscriptionName, cancellationToken)) {
      yield return rule;
    }
  }

  /// <inheritdoc />
  public async Task DeleteRuleAsync(
    string topicName,
    string subscriptionName,
    string ruleName,
    CancellationToken cancellationToken = default) {
    await _adminClient.DeleteRuleAsync(topicName, subscriptionName, ruleName, cancellationToken);
  }

  /// <inheritdoc />
  public async Task CreateRuleAsync(
    string topicName,
    string subscriptionName,
    CreateRuleOptions options,
    CancellationToken cancellationToken = default) {
    await _adminClient.CreateRuleAsync(topicName, subscriptionName, options, cancellationToken);
  }

  #endregion
}
