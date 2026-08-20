using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// The ONE code path for creating Azure Service Bus entities (topology arc phase 5): both the
/// transport's subscribe-time auto-provision and the provisioner's manifest-driven DARK
/// provisioning delegate here, so session/lock/delivery settings and filter-rule shapes can
/// never drift between the two.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceBusInfrastructureProvisionerManifestTests.cs:ProvisionManifest_CreatesSubscriptionsWithTransportSettingsAsync</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_TopicAndSubscriptionMissing_CreatesBothAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Infrastructure provisioning - startup overhead not critical")]
internal static class ServiceBusEntityProvisioning {
  /// <summary>The single rule name both provisioning paths converge on.</summary>
  internal const string ROUTING_PATTERN_RULE_NAME = "RoutingPatternFilter";

  /// <summary>
  /// Ensures a topic exists — check-then-create with 409 race tolerance (another instance
  /// creating the same topic between the check and the create is expected fleet behavior).
  /// </summary>
  internal static async Task EnsureTopicExistsAsync(
      IServiceBusAdminClient adminClient,
      string topicName,
      ILogger logger,
      CancellationToken cancellationToken) {
    try {
      if (!await adminClient.TopicExistsAsync(topicName, cancellationToken)) {
        await adminClient.CreateTopicAsync(topicName, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information)) {
          logger.LogInformation("Auto-created topic '{TopicName}'", topicName);
        }
      }
    } catch (RequestFailedException ex) when (ex.Status == 409) {
      // Race condition — another instance created it, safe to ignore
      if (logger.IsEnabled(LogLevel.Debug)) {
        logger.LogDebug(ex, "Topic '{TopicName}' already exists (race condition)", topicName);
      }
    }
  }

  /// <summary>
  /// Creates a subscription with the transport's settings — <c>EnableSessions</c> drives
  /// RequiresSession, <c>MaxDeliveryAttempts</c> the delivery cap, and
  /// <c>SubscriptionLockDuration</c> the lock — so a DARK-provisioned subscription is
  /// indistinguishable from a subscribe-time one.
  /// </summary>
  /// <param name="adminClient">The management-plane client.</param>
  /// <param name="options">The transport's options.</param>
  /// <param name="topicName">The topic to subscribe under.</param>
  /// <param name="subscriptionName">The subscription name.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <param name="controlClass">
  /// True for a CONTROL-CLASS entity (topology arc phase 9): the subscription is created WITHOUT
  /// sessions regardless of <c>EnableSessions</c>. Control consumers need no ordering, so the
  /// accept/lock machinery is pure idle cost for this class — and a sessionless entity is one whose
  /// delivery counter actually rises under lock loss, which restores the broker's own dead-letter
  /// valve (phase 8.5 established a session-enabled entity's never does). The flag can only ever
  /// REMOVE sessions: a host that disabled them globally is unaffected.
  /// </param>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbControlClassProvisioningTests.cs:ProvisionManifest_ControlClassSubscription_CreatedWithoutSessionsAsync</tests>
  internal static async Task CreateSubscriptionAsync(
      IServiceBusAdminClient adminClient,
      AzureServiceBusOptions options,
      string topicName,
      string subscriptionName,
      ILogger logger,
      CancellationToken cancellationToken,
      bool controlClass = false) {
    if (logger.IsEnabled(LogLevel.Information)) {
      logger.LogInformation("Creating subscription {TopicName}/{SubscriptionName}", topicName, subscriptionName);
    }
    if (options.EnableSessions && !controlClass) {
      await adminClient.CreateSubscriptionAsync(topicName, subscriptionName, requiresSession: true, options.MaxDeliveryAttempts, options.SubscriptionLockDuration, cancellationToken);
    } else {
      await adminClient.CreateSubscriptionAsync(topicName, subscriptionName, options.MaxDeliveryAttempts, options.SubscriptionLockDuration, cancellationToken);
    }
  }

  /// <summary>
  /// Applies the routing-pattern SqlFilter rule, converging idempotently: when the
  /// subscription's rules already equal the desired filter, nothing is touched (deleting and
  /// recreating opens a brief no-rule window in which published messages match nothing and are
  /// silently dropped). Failures are logged and swallowed — a filterless subscription is
  /// over-delivery, not data loss.
  /// </summary>
  internal static async Task ApplyRoutingPatternFilterAsync(
      IServiceBusAdminClient adminClient,
      string topicName,
      string subscriptionName,
      IEnumerable<string> routingPatterns,
      ILogger logger,
      CancellationToken cancellationToken) {
    // Build SQL filter expression
    // "ns1.#,ns2.#" → "sys.Label LIKE 'ns1.%' OR sys.Label LIKE 'ns2.%'"
    // NOTE: Azure Service Bus SqlFilter uses sys.Label for the Subject/Label property,
    // NOT [Subject]. The [Subject] syntax doesn't work for SqlRuleFilter expressions.
    // See: https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-sql-filter
    var likePatterns = routingPatterns
      .Select(p => p.Replace(".#", ".%").Replace(".*", ".%").Replace("#", "%").Replace("*", "%"))
      .Select(p => $"sys.Label LIKE '{p}'");

    var sqlExpression = string.Join(" OR ", likePatterns);

    try {
      // Idempotency: when the subscription's rules already converge on exactly the desired
      // SqlFilter, leave them alone. Deleting-then-recreating opens a brief no-rule window in
      // which published messages match nothing and are silently dropped — resubscribes (deploy,
      // connection recovery, receive-liveness watchdog) must not reopen that window for a no-op.
      var existingRules = new List<RuleProperties>();
      await foreach (var rule in adminClient.GetRulesAsync(topicName, subscriptionName, cancellationToken)) {
        existingRules.Add(rule);
      }
      if (existingRules.Count == 1
          && existingRules[0].Name == ROUTING_PATTERN_RULE_NAME
          && existingRules[0].Filter is SqlRuleFilter existingFilter
          && existingFilter.SqlExpression == sqlExpression) {
        if (logger.IsEnabled(LogLevel.Debug)) {
          logger.LogDebug(
            "[SqlFilter] Rule already correct on {TopicName}/{SubscriptionName}; skipping re-application",
            topicName,
            subscriptionName);
        }
        return;
      }

      // Delete existing rules (including $Default)
      var deletedRules = new List<string>();
      foreach (var rule in existingRules) {
        await adminClient.DeleteRuleAsync(topicName, subscriptionName, rule.Name, cancellationToken);
        deletedRules.Add(rule.Name);
      }

      // Create SqlFilter rule
      var ruleOptions = new CreateRuleOptions(ROUTING_PATTERN_RULE_NAME, new SqlRuleFilter(sqlExpression));
      await adminClient.CreateRuleAsync(topicName, subscriptionName, ruleOptions, cancellationToken);

      if (logger.IsEnabled(LogLevel.Debug)) {
        logger.LogDebug(
          "[SqlFilter] Deleted {RuleCount} existing rules and applied SqlFilter '{SqlExpression}' to {TopicName}/{SubscriptionName}",
          deletedRules.Count,
          sqlExpression,
          topicName,
          subscriptionName);
      }
    } catch (Exception ex) {
      logger.LogWarning(
        ex,
        "Failed to apply routing pattern filter to {TopicName}/{SubscriptionName}. Proceeding without filter.",
        topicName,
        subscriptionName
      );
    }
  }
}
