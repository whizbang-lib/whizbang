using Azure.Messaging.ServiceBus.Administration;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Interface abstraction for ServiceBusAdministrationClient to enable testing.
/// Wraps Azure SDK's ServiceBusAdministrationClient which uses sealed classes.
/// </summary>
/// <docs>messaging/transports/azure-service-bus#admin-client</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs</tests>
public interface IServiceBusAdminClient {
  #region Namespace Management

  /// <summary>
  /// Gets the namespace properties for connectivity verification.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The namespace properties.</returns>
  Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default);

  #endregion

  #region Topic Management

  /// <summary>
  /// Checks if a topic exists in the Service Bus namespace.
  /// </summary>
  Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default);

  /// <summary>
  /// Creates a topic in the Service Bus namespace.
  /// </summary>
  Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default);

  #endregion

  #region Subscription Management

  /// <summary>
  /// Checks if a subscription exists on a topic.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True if the subscription exists, false otherwise.</returns>
  Task<bool> SubscriptionExistsAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Creates a subscription on a topic with default settings (receives all messages).
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task CreateSubscriptionAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Creates a subscription on a topic with session support.
  /// When requiresSession is true, the subscription only accepts messages with a SessionId set.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="requiresSession">If true, the subscription requires session-enabled messages for FIFO ordering.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task CreateSubscriptionAsync(
    string topicName,
    string subscriptionName,
    bool requiresSession,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets the properties of an existing subscription.
  /// Used to check if a subscription requires sessions for auto-migration.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The subscription properties.</returns>
  Task<SubscriptionProperties> GetSubscriptionAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Deletes a subscription from a topic.
  /// Used during auto-migration when RequiresSession needs to change (ASB does not allow toggling it).
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task DeleteSubscriptionAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken = default);

  #endregion

  #region Rule Management

  /// <summary>
  /// Gets all rules for a subscription.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Async enumerable of rule properties.</returns>
  IAsyncEnumerable<RuleProperties> GetRulesAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Deletes a rule from a subscription.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="ruleName">The rule name to delete.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task DeleteRuleAsync(
    string topicName,
    string subscriptionName,
    string ruleName,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Creates a rule on a subscription.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="options">The rule creation options including filter.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task CreateRuleAsync(
    string topicName,
    string subscriptionName,
    CreateRuleOptions options,
    CancellationToken cancellationToken = default);

  #endregion
}
