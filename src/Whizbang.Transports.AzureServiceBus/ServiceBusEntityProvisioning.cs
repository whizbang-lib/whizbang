// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
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
  /// <summary>The rule name both provisioning paths converge on (the first of the split rules, #2089).</summary>
  internal const string ROUTING_PATTERN_RULE_NAME = "RoutingPatternFilter";

  /// <summary>The broker's limit on a SQL filter expression, in characters.</summary>
  internal const int MAX_SQL_FILTER_LENGTH = 1024;

  /// <summary>Joins the LIKE clauses of one rule.</summary>
  private const string OR_SEPARATOR = " OR ";

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

    // #2089: a command inbox filters to each handled command type, which can be more exact subjects than one SQL filter
    // holds (1,024 characters). The clauses are split across as many rules as they need, each within the limit; the
    // broker delivers a message once when any rule matches. The first rule keeps the historical name.
    var desired = SplitIntoRules(likePatterns);

    try {
      // Idempotency: when the subscription's rules already converge on exactly the desired
      // SqlFilters, leave them alone. Deleting-then-recreating opens a brief no-rule window in
      // which published messages match nothing and are silently dropped — resubscribes (deploy,
      // connection recovery, receive-liveness watchdog) must not reopen that window for a no-op.
      var existingRules = new List<RuleProperties>();
      await foreach (var rule in adminClient.GetRulesAsync(topicName, subscriptionName, cancellationToken)) {
        existingRules.Add(rule);
      }
      if (_alreadyConverged(existingRules, desired)) {
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
      foreach (var ruleName in existingRules.Select(rule => rule.Name)) {
        await adminClient.DeleteRuleAsync(topicName, subscriptionName, ruleName, cancellationToken);
        deletedRules.Add(ruleName);
      }

      // Create the SqlFilter rules
      foreach (var (name, expression) in desired) {
        await adminClient.CreateRuleAsync(
          topicName, subscriptionName, new CreateRuleOptions(name, new SqlRuleFilter(expression)), cancellationToken);
      }

      if (logger.IsEnabled(LogLevel.Debug)) {
        logger.LogDebug(
          "[SqlFilter] Deleted {RuleCount} existing rules and applied {FilterCount} SqlFilter rule(s) '{SqlExpression}' to {TopicName}/{SubscriptionName}",
          deletedRules.Count,
          desired.Count,
          string.Join(OR_SEPARATOR, desired.Select(rule => rule.Expression)),
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

  /// <summary>
  /// Splits LIKE clauses into named rules whose expressions each stay within <see cref="MAX_SQL_FILTER_LENGTH"/>
  /// (#2089). Order is kept; a single clause longer than the limit still gets a rule of its own (the broker refuses it,
  /// which is logged, rather than a pattern silently disappearing).
  /// </summary>
  /// <param name="likeClauses">The clauses, in order.</param>
  /// <returns>The rules: <see cref="ROUTING_PATTERN_RULE_NAME"/>, then <c>-2</c>, <c>-3</c> and so on.</returns>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusProvisioningPathTests.cs:SubscribeAsync_RoutingPatternsBeyondOneFilterLimit_SplitAcrossRulesWithinTheLimitAsync</tests>
  internal static List<(string Name, string Expression)> SplitIntoRules(IEnumerable<string> likeClauses) {
    var expressions = new List<string>();
    var current = new System.Text.StringBuilder();
    foreach (var clause in likeClauses) {
      if (current.Length > 0 && current.Length + OR_SEPARATOR.Length + clause.Length > MAX_SQL_FILTER_LENGTH) {
        expressions.Add(current.ToString());
        current.Clear();
      }
      if (current.Length > 0) {
        current.Append(OR_SEPARATOR);
      }
      current.Append(clause);
    }
    if (current.Length > 0) {
      expressions.Add(current.ToString());
    }
    return [.. expressions.Select((expression, i) => (i == 0 ? ROUTING_PATTERN_RULE_NAME : $"{ROUTING_PATTERN_RULE_NAME}-{i + 1}", expression))];
  }

  /// <summary>The rule an event subscription excludes its opted-out event types with (#2175).</summary>
  internal const string EXCLUDED_EVENT_TYPES_RULE_NAME = "ExcludedEventTypes";

  /// <summary>
  /// The SQL filter of the excluded-event-types rule (#2175):
  /// <c>sys.Label IS NULL OR NOT (sys.Label IN ('a', 'b'))</c> over the subjects, quotes doubled. The null guard keeps a
  /// message published without a subject delivered, as the match-all rule did (in SQL, <c>NULL IN (...)</c> is unknown,
  /// and a filter that is unknown drops the message).
  /// </summary>
  /// <param name="subjects">The excluded subjects, at least one.</param>
  /// <returns>The filter expression.</returns>
  internal static string BuildExcludedEventTypesExpression(IReadOnlyList<string> subjects) =>
    throw new NotImplementedException();

  /// <summary>
  /// Converges an event subscription on its opt-out list (#2175), idempotently. With subjects, the
  /// <see cref="EXCLUDED_EVENT_TYPES_RULE_NAME"/> rule carries the filter and the match-all <c>$Default</c> rule is
  /// removed once it does. With none, a subscription that never excluded anything is not touched at all, and one that
  /// did has its rule replaced in place by a match-all filter. Rules are only ever created or replaced in place before
  /// anything is removed, so the subscription is never without a rule that delivers.
  /// </summary>
  /// <param name="adminClient">The management-plane client.</param>
  /// <param name="topicName">The topic.</param>
  /// <param name="subscriptionName">The subscription.</param>
  /// <param name="subjects">The excluded subjects; empty to deliver every event.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task completing when the subscription has converged, or the failure has been logged.</returns>
  internal static Task ApplyExcludedEventTypesAsync(
      IServiceBusAdminClient adminClient,
      string topicName,
      string subscriptionName,
      IReadOnlyList<string> subjects,
      ILogger logger,
      CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <summary>True when the subscription's rules are exactly the desired SqlFilter rules, and nothing else.</summary>
  private static bool _alreadyConverged(List<RuleProperties> existingRules, List<(string Name, string Expression)> desired) =>
    existingRules.Count == desired.Count
    && desired.All(rule => existingRules.Exists(existing =>
      existing.Name == rule.Name
      && existing.Filter is SqlRuleFilter filter
      && filter.SqlExpression == rule.Expression));
}
