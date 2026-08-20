using Azure.Messaging.ServiceBus.Administration;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Interface abstraction for ServiceBusAdministrationClient to enable testing.
/// Wraps Azure SDK's ServiceBusAdministrationClient which uses sealed classes.
/// </summary>
/// <docs>messaging/transports/azure-service-bus#admin-client</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusAdminClientWrapperTests.cs:GetNamespacePropertiesAsync_ReturnsUnwrappedValueAsync</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusAdminClientWrapperTests.cs:GetNamespacePropertiesAsync_PassesCancellationTokenAsync</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerTests.cs:EnsureTopicExistsAsync_TopicDoesNotExist_CreatesItAsync</tests>
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
    int maxDeliveryCount,
    TimeSpan lockDuration,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Creates a subscription on a topic with session support.
  /// When requiresSession is true, the subscription only accepts messages with a SessionId set.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="requiresSession">If true, the subscription requires session-enabled messages for FIFO ordering.</param>
  /// <param name="maxDeliveryCount">The maximum number of delivery attempts before dead-lettering.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task CreateSubscriptionAsync(
    string topicName,
    string subscriptionName,
    bool requiresSession,
    int maxDeliveryCount,
    TimeSpan lockDuration,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Raises an existing subscription's lock duration in place. LockDuration is updatable
  /// (unlike RequiresSession), so environments provisioned before the safe default existed
  /// self-heal on their next deploy — no delete/recreate, no message loss.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="lockDuration">The lock duration to apply.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task UpdateSubscriptionLockDurationAsync(
    string topicName,
    string subscriptionName,
    TimeSpan lockDuration,
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

  /// <summary>
  /// Gets the number of active (deliverable, not dead-lettered or scheduled) messages
  /// currently waiting on a subscription. Used by the receive-liveness watchdog to
  /// distinguish a genuinely idle subscription from one whose receiver has silently stalled.
  /// </summary>
  /// <param name="topicName">The topic name.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The subscription's active message count.</returns>
  /// <docs>messaging/transports/azure-service-bus#receive-liveness</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusAdminClientWrapperTests.cs:GetSubscriptionActiveMessageCountAsync_ReturnsActiveMessageCountAsync</tests>
  Task<long> GetSubscriptionActiveMessageCountAsync(
    string topicName,
    string subscriptionName,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Enumerates every subscription on a topic. Used by the topology ownership-drift check
  /// (topology arc phase 5): a second service's subscription on a command inbox entity this
  /// service is about to own is a modeling error the provisioning path must surface.
  /// </summary>
  /// <remarks>
  /// Default implementation yields nothing — pre-phase-5 custom implementations keep
  /// compiling, at the cost of the drift check being silently inert for them until they
  /// override. <see cref="ServiceBusAdminClientWrapper"/> overrides with the real enumeration.
  /// </remarks>
  /// <param name="topicName">The topic name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Async enumerable of every subscription's properties on the topic.</returns>
  /// <docs>fundamentals/dispatcher/routing#ownership-drift</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerManifestTests.cs:ProvisionManifest_OwnedCommandInbox_ForeignSubscription_RecordsDriftAsync</tests>
  IAsyncEnumerable<SubscriptionProperties> GetSubscriptionsAsync(
    string topicName,
    CancellationToken cancellationToken = default) => _emptySubscriptionsAsync();

  /// <summary>Empty enumeration backing the <see cref="GetSubscriptionsAsync"/> default.</summary>
  private static async IAsyncEnumerable<SubscriptionProperties> _emptySubscriptionsAsync() {
    await Task.CompletedTask;
    yield break;
  }

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
