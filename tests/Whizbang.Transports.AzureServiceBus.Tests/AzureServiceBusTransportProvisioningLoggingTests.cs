using System.Text.Json;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for AzureServiceBusTransport's provisioning and filter paths under a logger
/// with every level enabled — covering the IsEnabled-gated diagnostics that the
/// NullLogger-based provisioning tests skip: subscription-name derivation logs, the
/// RoutingPatterns / DestinationFilter debug branches, rule-deletion + correlation-filter
/// logs, 409-race debug swallows, session auto-migration info, and publish-side topic
/// auto-creation logs.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusTransportProvisioningLoggingTests {
  private const string TOPIC = "log-topic";
  private const string SUB = "log-sub";

  // ========================================
  // SUBSCRIPTION NAME DERIVATION DIAGNOSTICS
  // ========================================

  /// <summary>
  /// Subscribing without RoutingPatterns metadata logs the routing-pattern skip and the
  /// plain-RoutingKey subscription-name choice at debug.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_NoRoutingPatternsMetadata_LogsDebugSkipAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { TOPIC },
      ExistingSubscriptions = { (TOPIC, SUB) }
    };
    var (transport, _, logger) = _createTransport(adminClient);

    var subscription = await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC, SUB));

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "RoutingPatterns not found in metadata")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "as subscription name")).IsTrue()
      .Because("a plain RoutingKey is used directly and logged at debug");
  }

  /// <summary>SubscriberName metadata derivation is logged at debug with the derived name.</summary>
  [Test]
  public async Task SubscribeAsync_SubscriberNameMetadata_LogsDerivedNameAsync() {
    var adminClient = new RecordingProvisioningAdminClient { ExistingTopics = { TOPIC } };
    var (transport, _, logger) = _createTransport(adminClient);
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = AsbTransportTestData.Json("\"orders-service\"")
    };

    await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC, "#", metadata));

    var expectedName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName("orders-service", TOPIC);
    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedSubscriptions[0].Subscription).IsEqualTo(expectedName);
    await Assert.That(logger.Contains(LogLevel.Debug, "Derived subscription name")).IsTrue();
  }

  /// <summary>
  /// A wildcard RoutingKey with no SubscriberName falls back to the default subscription
  /// name, logged at debug.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_WildcardRoutingKey_LogsDefaultFallbackAsync() {
    var adminClient = new RecordingProvisioningAdminClient { ExistingTopics = { TOPIC } };
    var (transport, _, logger) = _createTransport(adminClient);

    await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC, "#"));

    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedSubscriptions[0].Subscription).IsEqualTo("default");
    await Assert.That(logger.Contains(LogLevel.Debug, "Using default subscription name")).IsTrue();
  }

  // ========================================
  // DestinationFilter / CorrelationFilter DIAGNOSTICS
  // ========================================

  /// <summary>
  /// DestinationFilter metadata without an admin client on a non-emulator endpoint logs the
  /// unavailable-admin-client debug branch and proceeds without the filter.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterWithoutAdminClient_LogsDebugAsync() {
    var (transport, _, logger) = _createTransport(adminClient: null);
    var metadata = new Dictionary<string, JsonElement> {
      ["DestinationFilter"] = AsbTransportTestData.Json("\"svc-a\"")
    };

    var subscription = await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC, SUB, metadata));

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "administration client is not available")).IsTrue();
  }

  /// <summary>
  /// The correlation-filter path deletes existing $Default / DestinationFilter rules (debug
  /// log per rule), creates the replacement CorrelationFilter, and logs at information.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterWithRules_DeletesRulesCreatesFilterAndLogsAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { TOPIC },
      ExistingSubscriptions = { (TOPIC, SUB) },
      ExistingRules = {
        ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter()),
        ServiceBusModelFactory.RuleProperties("DestinationFilter", new TrueRuleFilter())
      }
    };
    var (transport, _, logger) = _createTransport(adminClient);
    var metadata = new Dictionary<string, JsonElement> {
      ["DestinationFilter"] = AsbTransportTestData.Json("\"svc-a\"")
    };

    await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC, SUB, metadata));

    await Assert.That(adminClient.DeletedRules.Contains((TOPIC, SUB, "$Default"))).IsTrue();
    await Assert.That(adminClient.DeletedRules.Contains((TOPIC, SUB, "DestinationFilter"))).IsTrue();
    await Assert.That(adminClient.CreatedRules).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedRules[0].Options.Name).IsEqualTo("DestinationFilter");
    await Assert.That(logger.Contains(LogLevel.Debug, "Deleted rule")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Information, "Applied CorrelationFilter")).IsTrue();
  }

  // ========================================
  // PROVISIONING RACE / MIGRATION DIAGNOSTICS
  // ========================================

  /// <summary>
  /// A 409 Conflict during subscription creation (another instance won the race) is swallowed
  /// with a debug log and the subscribe proceeds.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_SubscriptionCreate409Race_LogsDebugConflictAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { TOPIC },
      CreateSubscriptionException = new RequestFailedException(409, "Subscription already exists", "Conflict", null)
    };
    var (transport, _, logger) = _createTransport(adminClient);

    var subscription = await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC, SUB));

    await Assert.That(subscription.IsActive).IsTrue()
      .Because("a 409 race on subscription creation must not fail the subscribe");
    await Assert.That(logger.Contains(LogLevel.Debug, "already exists (409 conflict)")).IsTrue();
  }

  /// <summary>
  /// Sessions enabled + existing non-session subscription triggers the delete/recreate
  /// auto-migration with its information log.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_SessionMigration_LogsAutoMigrationAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { TOPIC },
      ExistingSubscriptions = { (TOPIC, SUB) }
    };
    var (transport, _, logger) = _createTransport(adminClient, enableSessions: true);

    await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC, SUB));

    await Assert.That(adminClient.DeletedSubscriptions.Contains((TOPIC, SUB))).IsTrue();
    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedSubscriptions[0].RequiresSession == true).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Information, "Auto-migrating subscription")).IsTrue();
    // No "Creating subscription" log here: the migration path calls the admin client
    // directly rather than routing through _createSubscriptionAsync.
  }

  /// <summary>
  /// Applying a RoutingPatterns SqlFilter logs the deleted-rule count and the SQL expression
  /// at debug once the rule is written.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_RoutingPatterns_LogsSqlFilterApplicationAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { TOPIC },
      ExistingSubscriptions = { (TOPIC, SUB) },
      ExistingRules = { ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter()) }
    };
    var (transport, _, logger) = _createTransport(adminClient);
    var metadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = AsbTransportTestData.Json("""["orders.#"]""")
    };

    await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC, SUB, metadata));

    await Assert.That(adminClient.CreatedRules).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedRules[0].Options.Name).IsEqualTo("RoutingPatternFilter");
    await Assert.That(logger.Contains(LogLevel.Debug, "[SqlFilter]")).IsTrue();
  }

  // ========================================
  // PUBLISH-SIDE TOPIC AUTO-PROVISIONING DIAGNOSTICS
  // ========================================

  /// <summary>
  /// Publishing to a missing topic auto-creates it (information log) before the sender is
  /// created (debug log).
  /// </summary>
  [Test]
  public async Task PublishAsync_TopicMissing_AutoCreatesTopicWithLogsAsync() {
    var adminClient = new RecordingProvisioningAdminClient();
    var (transport, client, logger) = _createTransport(adminClient);

    await transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), new TransportDestination(TOPIC, "orders.created"));

    await Assert.That(adminClient.CreatedTopics).Contains(TOPIC);
    await Assert.That(client.LastSender!.Sent).Count().IsEqualTo(1);
    await Assert.That(logger.Contains(LogLevel.Information, "Auto-created topic")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "Created sender for topic")).IsTrue();
  }

  /// <summary>
  /// A 409 Conflict during publish-side topic creation is swallowed with a debug log and the
  /// publish continues.
  /// </summary>
  [Test]
  public async Task PublishAsync_TopicCreate409Race_LogsDebugAndPublishesAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      CreateTopicException = new RequestFailedException(409, "Topic already exists", "Conflict", null)
    };
    var (transport, client, logger) = _createTransport(adminClient);

    await transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), new TransportDestination(TOPIC, "orders.created"));

    await Assert.That(client.LastSender!.Sent).Count().IsEqualTo(1)
      .Because("the 409 race must not abort the publish");
    await Assert.That(logger.Contains(LogLevel.Debug, "already exists (race condition)")).IsTrue();
  }

  // ========================================
  // HELPERS
  // ========================================

  private static Task _noopHandler(IMessageEnvelope envelope, string? envelopeType, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client, RecordingTransportLogger Logger) _createTransport(
    RecordingProvisioningAdminClient? adminClient,
    bool enableSessions = false) {
    var client = new RaisableServiceBusClient();
    var logger = new RecordingTransportLogger();
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = true,
      EnableSessions = enableSessions
    };
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      options,
      logger,
      adminClient);
    return (transport, client, logger);
  }
}
