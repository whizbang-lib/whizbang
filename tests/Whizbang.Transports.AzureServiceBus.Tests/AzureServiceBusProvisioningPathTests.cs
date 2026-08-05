using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for AzureServiceBusTransport's provisioning and options-validation paths:
/// constructor guards, InitializeAsync branches, _ensureInfrastructureExistsAsync,
/// _createSubscriptionAsync, _ensureTopicExistsViaAdminAsync,
/// _migrateSubscriptionToSessionsIfNeededAsync, subscription-name derivation, and the
/// SqlFilter / CorrelationFilter rule application paths.
///
/// The ASB emulator has no management plane, so every admin-plane path is driven through a
/// recording fake IServiceBusAdminClient. The broker itself is replaced with mockable
/// ServiceBusClient/ServiceBusProcessor subclasses (the Azure SDK's documented mocking
/// surface), so SubscribeAsync/SubscribeBatchAsync complete end-to-end without any network.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusProvisioningPathTests {
  private const string CLOUD_NAMESPACE = "unit-test.servicebus.windows.net";
  private const string EMULATOR_NAMESPACE = "localhost";

  // ========================================
  // CONSTRUCTOR / OPTIONS VALIDATION
  // ========================================

  /// <summary>Constructor must reject a null ServiceBusClient.</summary>
  [Test]
  public async Task Constructor_NullClient_ThrowsArgumentNullExceptionAsync() {
    var jsonOptions = _createJsonOptions();

    Assert.Throws<ArgumentNullException>(() => _ = new AzureServiceBusTransport(null!, jsonOptions));

    await Task.CompletedTask;
  }

  /// <summary>Constructor must reject null JsonSerializerOptions.</summary>
  [Test]
  public async Task Constructor_NullJsonOptions_ThrowsArgumentNullExceptionAsync() {
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE);

    Assert.Throws<ArgumentNullException>(() => _ = new AzureServiceBusTransport(client, null!));

    await Task.CompletedTask;
  }

  /// <summary>Transport starts uninitialized until InitializeAsync succeeds.</summary>
  [Test]
  public async Task IsInitialized_BeforeInitialize_ReturnsFalseAsync() {
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient: null);

    await Assert.That(transport.IsInitialized).IsFalse();
  }

  // ========================================
  // InitializeAsync BRANCHES
  // ========================================

  /// <summary>
  /// Emulator endpoints skip admin verification entirely — even a broken admin client must
  /// not be consulted, and the transport still reports initialized.
  /// </summary>
  [Test]
  public async Task InitializeAsync_EmulatorEndpoint_SkipsAdminVerificationAsync() {
    var adminClient = new RecordingAdminClient {
      GetNamespaceException = new InvalidOperationException("admin plane must not be called for emulator")
    };
    var transport = _createTransport(new FakeServiceBusClient(EMULATOR_NAMESPACE), adminClient);

    await transport.InitializeAsync();

    await Assert.That(transport.IsInitialized).IsTrue();
    await Assert.That(adminClient.GetNamespaceCallCount).IsEqualTo(0)
      .Because("emulator has no management plane; verification must be skipped");
  }

  /// <summary>
  /// Non-emulator endpoint with an admin client verifies connectivity via
  /// GetNamespacePropertiesAsync exactly once.
  /// </summary>
  [Test]
  public async Task InitializeAsync_NonEmulatorWithAdminClient_VerifiesConnectivityAsync() {
    var adminClient = new RecordingAdminClient();
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient);

    await transport.InitializeAsync();

    await Assert.That(transport.IsInitialized).IsTrue();
    await Assert.That(adminClient.GetNamespaceCallCount).IsEqualTo(1);
  }

  /// <summary>
  /// Second InitializeAsync call is an idempotent no-op — no second connectivity check.
  /// </summary>
  [Test]
  public async Task InitializeAsync_CalledTwice_SecondCallSkipsVerificationAsync() {
    var adminClient = new RecordingAdminClient();
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient);

    await transport.InitializeAsync();
    await transport.InitializeAsync();

    await Assert.That(transport.IsInitialized).IsTrue();
    await Assert.That(adminClient.GetNamespaceCallCount).IsEqualTo(1)
      .Because("initialization is idempotent — the admin API must only be hit once");
  }

  /// <summary>
  /// Admin-plane failure during verification is wrapped in InvalidOperationException and
  /// the transport stays uninitialized.
  /// </summary>
  [Test]
  public async Task InitializeAsync_AdminVerificationFails_ThrowsInvalidOperationExceptionAsync() {
    var adminClient = new RecordingAdminClient {
      GetNamespaceException = new ServiceBusException("unreachable", ServiceBusFailureReason.ServiceCommunicationProblem)
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.InitializeAsync());

    await Assert.That(ex!.InnerException).IsTypeOf<ServiceBusException>();
    await Assert.That(transport.IsInitialized).IsFalse();
  }

  /// <summary>
  /// Non-emulator endpoint without an admin client initializes with a warning
  /// (client-open check only).
  /// </summary>
  [Test]
  public async Task InitializeAsync_NonEmulatorWithoutAdminClient_MarksInitializedAsync() {
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient: null);

    await transport.InitializeAsync();

    await Assert.That(transport.IsInitialized).IsTrue();
  }

  /// <summary>A closed ServiceBusClient cannot be initialized.</summary>
  [Test]
  public async Task InitializeAsync_ClientClosed_ThrowsInvalidOperationExceptionAsync() {
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE) { IsClosedValue = true };
    var transport = _createTransport(client, adminClient: null);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.InitializeAsync());

    await Assert.That(ex!.InnerException).IsTypeOf<InvalidOperationException>()
      .Because("the closed-client InvalidOperationException is wrapped by the initialize catch block");
    await Assert.That(transport.IsInitialized).IsFalse();
  }

  /// <summary>Pre-cancelled token short-circuits before any work.</summary>
  [Test]
  public async Task InitializeAsync_CancelledToken_ThrowsOperationCanceledExceptionAsync() {
    var adminClient = new RecordingAdminClient();
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient);
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<OperationCanceledException>(async () => await transport.InitializeAsync(cts.Token));

    await Assert.That(transport.IsInitialized).IsFalse();
    await Assert.That(adminClient.GetNamespaceCallCount).IsEqualTo(0);
  }

  // ========================================
  // _ensureInfrastructureExistsAsync / _createSubscriptionAsync /
  // _ensureTopicExistsViaAdminAsync (via SubscribeAsync)
  // ========================================

  /// <summary>
  /// Auto-provisioning creates a missing topic AND a missing subscription (non-session
  /// mode uses the 3-argument CreateSubscriptionAsync overload).
  /// </summary>
  [Test]
  public async Task SubscribeAsync_TopicAndSubscriptionMissing_CreatesBothAsync() {
    var adminClient = new RecordingAdminClient();
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE);
    var transport = _createTransport(client, adminClient, _nonSessionOptions());

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(adminClient.CreatedTopics).Contains("orders-topic");
    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    var created = adminClient.CreatedSubscriptions[0];
    await Assert.That(created.Topic).IsEqualTo("orders-topic");
    await Assert.That(created.Subscription).IsEqualTo("unit-sub");
    await Assert.That(created.RequiresSession is null).IsTrue()
      .Because("non-session mode must use the overload without RequiresSession");
    await Assert.That(created.MaxDeliveryCount).IsEqualTo(10);
    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(client.LastProcessor!.StartCalled).IsTrue();
  }

  /// <summary>
  /// With sessions enabled, a fresh subscription is created with RequiresSession=true and
  /// the configured MaxDeliveryAttempts, and a session processor is started.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_SessionsEnabled_CreatesSessionRequiredSubscriptionAsync() {
    var adminClient = new RecordingAdminClient { ExistingTopics = { "orders-topic" } };
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE);
    var options = _sessionOptions();
    options.MaxDeliveryAttempts = 7;
    var transport = _createTransport(client, adminClient, options);

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    var created = adminClient.CreatedSubscriptions[0];
    await Assert.That(created.RequiresSession == true).IsTrue();
    await Assert.That(created.MaxDeliveryCount).IsEqualTo(7);
    await Assert.That(adminClient.CreatedTopics).IsEmpty()
      .Because("the topic already exists; only the subscription is created");
    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(client.LastSessionProcessor!.StartCalled).IsTrue();
  }

  /// <summary>
  /// An existing subscription with sessions disabled is left untouched — no create, no
  /// migration probe.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_SubscriptionExistsSessionsDisabled_SkipsCreationAndMigrationAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(adminClient.CreatedSubscriptions).IsEmpty();
    await Assert.That(adminClient.DeletedSubscriptions).IsEmpty();
    await Assert.That(adminClient.GetSubscriptionCallCount).IsEqualTo(0)
      .Because("migration probing only happens when EnableSessions is true");
  }

  /// <summary>
  /// Sessions enabled + existing non-session subscription triggers the delete/recreate
  /// auto-migration (ASB cannot toggle RequiresSession in place).
  /// </summary>
  [Test]
  public async Task SubscribeAsync_SessionsEnabledExistingNonSessionSubscription_MigratesToSessionsAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _sessionOptions());

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(adminClient.GetSubscriptionCallCount).IsEqualTo(1);
    await Assert.That(adminClient.DeletedSubscriptions.Contains(("orders-topic", "unit-sub"))).IsTrue();
    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedSubscriptions[0].RequiresSession == true).IsTrue()
      .Because("migration recreates the subscription with RequiresSession=true");
  }

  /// <summary>
  /// Sessions enabled + existing subscription that already requires sessions is a no-op.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_SessionsEnabledSubscriptionAlreadySessionful_DoesNotMigrateAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") },
      SessionRequiredSubscriptions = { ("orders-topic", "unit-sub") }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _sessionOptions());

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(adminClient.GetSubscriptionCallCount).IsEqualTo(1);
    await Assert.That(adminClient.DeletedSubscriptions).IsEmpty();
    await Assert.That(adminClient.CreatedSubscriptions).IsEmpty();
  }

  /// <summary>Without an admin client, provisioning is skipped but subscribing still works.</summary>
  [Test]
  public async Task SubscribeAsync_NoAdminClient_SkipsProvisioningAndSubscribesAsync() {
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE);
    var transport = _createTransport(client, adminClient: null, _nonSessionOptions());

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(client.LastProcessor!.StartCalled).IsTrue();
  }

  /// <summary>AutoProvisionInfrastructure=false leaves the admin plane untouched.</summary>
  [Test]
  public async Task SubscribeAsync_AutoProvisionDisabled_DoesNotTouchAdminPlaneAsync() {
    var adminClient = new RecordingAdminClient();
    var options = _nonSessionOptions();
    options.AutoProvisionInfrastructure = false;
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, options);

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(adminClient.TopicExistsCalls).IsEmpty();
    await Assert.That(adminClient.SubscriptionExistsCallCount).IsEqualTo(0);
    await Assert.That(adminClient.CreatedSubscriptions).IsEmpty();
  }

  /// <summary>
  /// 409 Conflict during subscription creation (another instance won the race) is swallowed
  /// and the subscribe proceeds.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_SubscriptionCreate409Race_HandledGracefullyAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      CreateSubscriptionException = new RequestFailedException(409, "Subscription already exists", "Conflict", null)
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(subscription.IsActive).IsTrue()
      .Because("a 409 race on subscription creation must not fail the subscribe");
    await Assert.That(adminClient.CreatedSubscriptions).IsEmpty();
  }

  /// <summary>
  /// 409 Conflict during topic creation is swallowed and provisioning continues to the
  /// subscription step.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_TopicCreate409Race_ContinuesToSubscriptionCreationAsync() {
    var adminClient = new RecordingAdminClient {
      CreateTopicException = new RequestFailedException(409, "Topic already exists", "Conflict", null)
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic"));

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1)
      .Because("the topic 409 must not abort subscription provisioning");
  }

  /// <summary>Non-409 topic creation failures propagate to the caller.</summary>
  [Test]
  public async Task SubscribeAsync_TopicCreateFailsNonConflict_PropagatesExceptionAsync() {
    var adminClient = new RecordingAdminClient {
      CreateTopicException = new RequestFailedException(500, "Internal error")
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());

    await Assert.ThrowsAsync<RequestFailedException>(async () =>
      await transport.SubscribeAsync(_noopHandler, _destination("orders-topic")));
  }

  /// <summary>
  /// Unexpected admin-plane failures (not 409) during the subscription-exists probe
  /// propagate through SubscribeAsync's log-and-rethrow.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_SubscriptionProbeFails_PropagatesExceptionAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      SubscriptionExistsException = new InvalidOperationException("admin plane down")
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await transport.SubscribeAsync(_noopHandler, _destination("orders-topic")));
  }

  // ========================================
  // SUBSCRIPTION NAME DERIVATION
  // ========================================

  /// <summary>SubscriberName metadata wins over RoutingKey when deriving the subscription name.</summary>
  [Test]
  public async Task SubscribeAsync_SubscriberNameMetadata_DerivesSubscriptionNameAsync() {
    var adminClient = new RecordingAdminClient { ExistingTopics = { "orders-topic" } };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = _json("\"orders-service\"")
    };
    var destination = new TransportDestination("orders-topic", "#", metadata);

    await transport.SubscribeAsync(_noopHandler, destination);

    var expected = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName("orders-service", "orders-topic");
    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedSubscriptions[0].Subscription).IsEqualTo(expected);
  }

  /// <summary>
  /// A wildcard RoutingKey ("#") is not a valid ASB subscription name — the default
  /// subscription name is used instead.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_WildcardRoutingKey_FallsBackToDefaultSubscriptionNameAsync() {
    var adminClient = new RecordingAdminClient { ExistingTopics = { "orders-topic" } };
    var options = _nonSessionOptions();
    options.DefaultSubscriptionName = "fallback-sub";
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, options);

    await transport.SubscribeAsync(_noopHandler, new TransportDestination("orders-topic", "#"));

    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedSubscriptions[0].Subscription).IsEqualTo("fallback-sub");
  }

  /// <summary>A plain RoutingKey (no wildcards) is used directly as the subscription name.</summary>
  [Test]
  public async Task SubscribeAsync_PlainRoutingKey_UsedAsSubscriptionNameAsync() {
    var adminClient = new RecordingAdminClient { ExistingTopics = { "orders-topic" } };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());

    await transport.SubscribeAsync(_noopHandler, new TransportDestination("orders-topic", "my-plain-sub"));

    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    await Assert.That(adminClient.CreatedSubscriptions[0].Subscription).IsEqualTo("my-plain-sub");
  }

  // ========================================
  // _applyRoutingPatternFilterAsync (SqlFilter)
  // ========================================

  /// <summary>
  /// RoutingPatterns metadata replaces existing rules with a single SqlFilter rule whose
  /// expression translates RabbitMQ-style patterns to sys.Label LIKE patterns.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_RoutingPatterns_AppliesSqlFilterRuleAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") },
      ExistingRules = { ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter()) }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = _json("""["orders.#","audit.*"]""")
    };

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(adminClient.DeletedRules.Contains(("orders-topic", "unit-sub", "$Default"))).IsTrue()
      .Because("the default match-all rule must be removed before the SqlFilter is applied");
    await Assert.That(adminClient.CreatedRules).Count().IsEqualTo(1);
    var rule = adminClient.CreatedRules[0];
    await Assert.That(rule.Options.Name).IsEqualTo("RoutingPatternFilter");
    var sqlFilter = rule.Options.Filter as SqlRuleFilter;
    await Assert.That(sqlFilter).IsNotNull();
    await Assert.That(sqlFilter!.SqlExpression)
      .IsEqualTo("sys.Label LIKE 'orders.%' OR sys.Label LIKE 'audit.%'");
  }

  /// <summary>Bare '#' and '*' wildcards (no dot prefix) also translate to '%'.</summary>
  [Test]
  public async Task SubscribeAsync_RoutingPatternWithBareWildcard_TranslatesToPercentAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = _json("""["events#"]""")
    };

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(adminClient.CreatedRules).Count().IsEqualTo(1);
    var sqlFilter = adminClient.CreatedRules[0].Options.Filter as SqlRuleFilter;
    await Assert.That(sqlFilter!.SqlExpression).IsEqualTo("sys.Label LIKE 'events%'");
  }

  /// <summary>RoutingPatterns metadata that is not a JSON array is ignored.</summary>
  [Test]
  public async Task SubscribeAsync_RoutingPatternsNotAnArray_SkipsFilterAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = _json("\"orders.#\"")
    };

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(adminClient.CreatedRules).IsEmpty();
    await Assert.That(adminClient.DeletedRules).IsEmpty();
  }

  /// <summary>Empty / whitespace-only patterns are filtered out and no rule is written.</summary>
  [Test]
  public async Task SubscribeAsync_RoutingPatternsOnlyWhitespace_SkipsFilterAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = _json("""["", "  "]""")
    };

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(adminClient.CreatedRules).IsEmpty();
  }

  /// <summary>
  /// With AutoProvisionInfrastructure=false the routing-pattern filter path returns early
  /// even when patterns are present.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_RoutingPatternsWithAutoProvisionDisabled_SkipsFilterAsync() {
    var adminClient = new RecordingAdminClient();
    var options = _nonSessionOptions();
    options.AutoProvisionInfrastructure = false;
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, options);
    var metadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = _json("""["orders.#"]""")
    };

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(adminClient.CreatedRules).IsEmpty();
    await Assert.That(adminClient.DeletedRules).IsEmpty();
  }

  /// <summary>
  /// Failures while applying the SqlFilter rule are logged and swallowed — subscribing
  /// proceeds without the filter.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_RoutingPatternRuleCreationFails_ProceedsWithoutFilterAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") },
      CreateRuleException = new ServiceBusException("rules broke", ServiceBusFailureReason.GeneralError)
    };
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE);
    var transport = _createTransport(client, adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = _json("""["orders.#"]""")
    };

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(subscription.IsActive).IsTrue()
      .Because("a filter-application failure must not fail the subscribe");
    await Assert.That(adminClient.CreatedRules).IsEmpty();
  }

  // ========================================
  // _applyCorrelationFilterAsync (DestinationFilter)
  // ========================================

  /// <summary>
  /// DestinationFilter metadata on a non-emulator endpoint replaces the default rule with a
  /// CorrelationFilter on the Destination application property.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterNonEmulator_AppliesCorrelationFilterAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") },
      ExistingRules = {
        ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter()),
        ServiceBusModelFactory.RuleProperties("DestinationFilter", new TrueRuleFilter())
      }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["DestinationFilter"] = _json("\"svc-a\"")
    };

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(adminClient.DeletedRules.Contains(("orders-topic", "unit-sub", "$Default"))).IsTrue();
    await Assert.That(adminClient.DeletedRules.Contains(("orders-topic", "unit-sub", "DestinationFilter"))).IsTrue()
      .Because("a stale DestinationFilter rule is replaced, not duplicated");
    await Assert.That(adminClient.CreatedRules).Count().IsEqualTo(1);
    var rule = adminClient.CreatedRules[0];
    await Assert.That(rule.Options.Name).IsEqualTo("DestinationFilter");
    var correlationFilter = rule.Options.Filter as CorrelationRuleFilter;
    await Assert.That(correlationFilter).IsNotNull();
    await Assert.That(correlationFilter!.ApplicationProperties["Destination"]).IsEqualTo("svc-a");
  }

  /// <summary>On the emulator, correlation filters are provisioned externally — skipped here.</summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterOnEmulator_SkipsCorrelationFilterAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") }
    };
    var transport = _createTransport(new FakeServiceBusClient(EMULATOR_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["DestinationFilter"] = _json("\"svc-a\"")
    };

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(adminClient.CreatedRules).IsEmpty()
      .Because("the emulator path must not touch subscription rules");
  }

  /// <summary>
  /// When the subscription does not exist (auto-provision off), the correlation filter is
  /// skipped with a warning rather than failing the subscribe.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterSubscriptionMissing_SkipsRuleCreationAsync() {
    var adminClient = new RecordingAdminClient();
    var options = _nonSessionOptions();
    options.AutoProvisionInfrastructure = false;
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, options);
    var metadata = new Dictionary<string, JsonElement> {
      ["DestinationFilter"] = _json("\"svc-a\"")
    };

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(adminClient.CreatedRules).IsEmpty();
    await Assert.That(adminClient.SubscriptionExistsCallCount).IsEqualTo(1)
      .Because("the correlation path probes for the subscription before writing rules");
  }

  /// <summary>
  /// A rule-write failure inside the correlation filter path is logged and swallowed;
  /// subscribing succeeds without the filter.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterRuleCreationFails_ProceedsWithoutFilterAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") },
      CreateRuleException = new ServiceBusException("rules broke", ServiceBusFailureReason.GeneralError)
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["DestinationFilter"] = _json("\"svc-a\"")
    };

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(subscription.IsActive).IsTrue()
      .Because("correlation filter failures degrade to no-filter instead of failing the subscribe");
    await Assert.That(adminClient.CreatedRules).IsEmpty();
  }

  /// <summary>
  /// DestinationFilter present but no admin client: logged at debug, subscribe proceeds.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterWithoutAdminClient_ProceedsWithoutFilterAsync() {
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE);
    var transport = _createTransport(client, adminClient: null, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["DestinationFilter"] = _json("\"svc-a\"")
    };

    var subscription = await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(subscription.IsActive).IsTrue();
  }

  /// <summary>Empty DestinationFilter strings are ignored.</summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterEmptyString_SkipsFilterAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      ExistingSubscriptions = { ("orders-topic", "unit-sub") }
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());
    var metadata = new Dictionary<string, JsonElement> {
      ["DestinationFilter"] = _json("\"\"")
    };

    await transport.SubscribeAsync(_noopHandler, _destination("orders-topic", metadata));

    await Assert.That(adminClient.CreatedRules).IsEmpty();
  }

  // ========================================
  // SubscribeBatchAsync PROVISIONING
  // ========================================

  /// <summary>
  /// SubscribeBatchAsync drives the same provisioning path as SubscribeAsync (non-session
  /// batch processor).
  /// </summary>
  [Test]
  public async Task SubscribeBatchAsync_AutoProvision_CreatesInfrastructureAndStartsProcessorAsync() {
    var adminClient = new RecordingAdminClient();
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE);
    var transport = _createTransport(client, adminClient, _nonSessionOptions());

    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask,
      _destination("orders-topic"),
      new TransportBatchOptions());

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(adminClient.CreatedTopics).Contains("orders-topic");
    await Assert.That(adminClient.CreatedSubscriptions).Count().IsEqualTo(1);
    await Assert.That(client.LastProcessor!.StartCalled).IsTrue();
  }

  /// <summary>With sessions enabled, SubscribeBatchAsync starts a session processor.</summary>
  [Test]
  public async Task SubscribeBatchAsync_SessionsEnabled_StartsSessionProcessorAsync() {
    var adminClient = new RecordingAdminClient { ExistingTopics = { "orders-topic" } };
    var client = new FakeServiceBusClient(CLOUD_NAMESPACE);
    var transport = _createTransport(client, adminClient, _sessionOptions());

    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask,
      _destination("orders-topic"),
      new TransportBatchOptions());

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(client.LastSessionProcessor!.StartCalled).IsTrue();
    await Assert.That(adminClient.CreatedSubscriptions[0].RequiresSession == true).IsTrue();
  }

  /// <summary>Admin-plane failures propagate through SubscribeBatchAsync's log-and-rethrow.</summary>
  [Test]
  public async Task SubscribeBatchAsync_ProvisioningFails_PropagatesExceptionAsync() {
    var adminClient = new RecordingAdminClient {
      ExistingTopics = { "orders-topic" },
      SubscriptionExistsException = new InvalidOperationException("admin plane down")
    };
    var transport = _createTransport(new FakeServiceBusClient(CLOUD_NAMESPACE), adminClient, _nonSessionOptions());

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await transport.SubscribeBatchAsync(
        (_, _) => Task.CompletedTask,
        _destination("orders-topic"),
        new TransportBatchOptions()));
  }

  // ========================================
  // HELPERS
  // ========================================

  private static Task _noopHandler(Whizbang.Core.Observability.IMessageEnvelope envelope, string? envelopeType, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  private static JsonSerializerOptions _createJsonOptions() =>
    new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

  private static AzureServiceBusOptions _nonSessionOptions() => new() {
    AutoProvisionInfrastructure = true,
    EnableSessions = false
  };

  private static AzureServiceBusOptions _sessionOptions() => new() {
    AutoProvisionInfrastructure = true,
    EnableSessions = true
  };

  private static AzureServiceBusTransport _createTransport(
    FakeServiceBusClient client,
    RecordingAdminClient? adminClient,
    AzureServiceBusOptions? options = null) {
    return new AzureServiceBusTransport(
      client,
      _createJsonOptions(),
      options ?? _nonSessionOptions(),
      NullLogger<AzureServiceBusTransport>.Instance,
      adminClient);
  }

  private static TransportDestination _destination(string topic, Dictionary<string, JsonElement>? metadata = null) =>
    new(topic, "unit-sub", metadata);

  private static JsonElement _json(string raw) {
    using var doc = JsonDocument.Parse(raw);
    return doc.RootElement.Clone();
  }

  // ========================================
  // TEST DOUBLES
  // ========================================

  /// <summary>
  /// Mockable ServiceBusClient that never opens a connection. Returns fake processors and
  /// session processors so subscribe paths complete without a broker.
  /// </summary>
  private sealed class FakeServiceBusClient(string fullyQualifiedNamespace) : ServiceBusClient {
    public bool IsClosedValue { get; set; }
    public FakeProcessor? LastProcessor { get; private set; }
    public FakeSessionProcessor? LastSessionProcessor { get; private set; }

    public override string FullyQualifiedNamespace => fullyQualifiedNamespace;
    public override bool IsClosed => IsClosedValue;

    public override ServiceBusProcessor CreateProcessor(
      string topicName, string subscriptionName, ServiceBusProcessorOptions options) {
      LastProcessor = new FakeProcessor();
      return LastProcessor;
    }

    public override ServiceBusSessionProcessor CreateSessionProcessor(
      string topicName, string subscriptionName, ServiceBusSessionProcessorOptions options) {
      LastSessionProcessor = new FakeSessionProcessor();
      return LastSessionProcessor;
    }
  }

  /// <summary>Inert ServiceBusProcessor — start/stop/close are recorded no-ops.</summary>
  private sealed class FakeProcessor : ServiceBusProcessor {
    public bool StartCalled { get; private set; }

    public override Task StartProcessingAsync(CancellationToken cancellationToken = default) {
      StartCalled = true;
      return Task.CompletedTask;
    }

    public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  /// <summary>
  /// Inert ServiceBusSessionProcessor. The SDK's session processor delegates its events to
  /// the virtual InnerProcessor property, so a real (mocking-constructed) inner processor is
  /// supplied for event wiring to land on.
  /// </summary>
  private sealed class FakeSessionProcessor : ServiceBusSessionProcessor {
    private readonly InnerFakeProcessor _inner = new();
    public bool StartCalled { get; private set; }

    protected override ServiceBusProcessor InnerProcessor => _inner;

    public override Task StartProcessingAsync(CancellationToken cancellationToken = default) {
      StartCalled = true;
      return Task.CompletedTask;
    }

    public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed class InnerFakeProcessor : ServiceBusProcessor {
    }
  }

  /// <summary>
  /// Recording IServiceBusAdminClient covering topics, subscriptions, migration probes and
  /// rule management, with per-operation failure injection.
  /// </summary>
  private sealed class RecordingAdminClient : IServiceBusAdminClient {
    public HashSet<string> ExistingTopics { get; init; } = [];
    public List<string> TopicExistsCalls { get; } = [];
    public List<string> CreatedTopics { get; } = [];
    public HashSet<(string Topic, string Subscription)> ExistingSubscriptions { get; init; } = [];
    public HashSet<(string Topic, string Subscription)> SessionRequiredSubscriptions { get; init; } = [];
    public List<(string Topic, string Subscription, bool? RequiresSession, int MaxDeliveryCount)> CreatedSubscriptions { get; } = [];
    public List<(string Topic, string Subscription)> DeletedSubscriptions { get; } = [];
    public List<RuleProperties> ExistingRules { get; init; } = [];
    public List<(string Topic, string Subscription, string RuleName)> DeletedRules { get; } = [];
    public List<(string Topic, string Subscription, CreateRuleOptions Options)> CreatedRules { get; } = [];
    public int GetNamespaceCallCount { get; private set; }
    public int GetSubscriptionCallCount { get; private set; }
    public int SubscriptionExistsCallCount { get; private set; }

    public Exception? GetNamespaceException { get; init; }
    public Exception? CreateTopicException { get; init; }
    public Exception? CreateSubscriptionException { get; init; }
    public Exception? SubscriptionExistsException { get; init; }
    public Exception? CreateRuleException { get; init; }

    public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default) {
      GetNamespaceCallCount++;
      if (GetNamespaceException is not null) {
        return Task.FromException<NamespaceProperties>(GetNamespaceException);
      }
      // NamespaceProperties has no public constructor; the transport ignores the value.
      return Task.FromResult<NamespaceProperties>(null!);
    }

    public Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) {
      TopicExistsCalls.Add(topicName);
      return Task.FromResult(ExistingTopics.Contains(topicName));
    }

    public Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) {
      if (CreateTopicException is not null) {
        return Task.FromException(CreateTopicException);
      }
      CreatedTopics.Add(topicName);
      ExistingTopics.Add(topicName);
      return Task.CompletedTask;
    }

    public Task<bool> SubscriptionExistsAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      SubscriptionExistsCallCount++;
      if (SubscriptionExistsException is not null) {
        return Task.FromException<bool>(SubscriptionExistsException);
      }
      return Task.FromResult(ExistingSubscriptions.Contains((topicName, subscriptionName)));
    }

    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, int maxDeliveryCount, CancellationToken cancellationToken = default) {
      if (CreateSubscriptionException is not null) {
        return Task.FromException(CreateSubscriptionException);
      }
      CreatedSubscriptions.Add((topicName, subscriptionName, null, maxDeliveryCount));
      ExistingSubscriptions.Add((topicName, subscriptionName));
      return Task.CompletedTask;
    }

    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, bool requiresSession, int maxDeliveryCount, CancellationToken cancellationToken = default) {
      if (CreateSubscriptionException is not null) {
        return Task.FromException(CreateSubscriptionException);
      }
      CreatedSubscriptions.Add((topicName, subscriptionName, requiresSession, maxDeliveryCount));
      ExistingSubscriptions.Add((topicName, subscriptionName));
      if (requiresSession) {
        SessionRequiredSubscriptions.Add((topicName, subscriptionName));
      }
      return Task.CompletedTask;
    }

    public Task<SubscriptionProperties> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      GetSubscriptionCallCount++;
      var properties = ServiceBusModelFactory.SubscriptionProperties(
        topicName: topicName,
        subscriptionName: subscriptionName,
        lockDuration: TimeSpan.FromMinutes(1),
        requiresSession: SessionRequiredSubscriptions.Contains((topicName, subscriptionName)),
        defaultMessageTimeToLive: TimeSpan.FromDays(14),
        autoDeleteOnIdle: TimeSpan.FromDays(30),
        maxDeliveryCount: 10,
        userMetadata: string.Empty);
      return Task.FromResult(properties);
    }

    public Task DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      DeletedSubscriptions.Add((topicName, subscriptionName));
      ExistingSubscriptions.Remove((topicName, subscriptionName));
      return Task.CompletedTask;
    }

    public Task<long> GetSubscriptionActiveMessageCountAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);

    public async IAsyncEnumerable<RuleProperties> GetRulesAsync(
      string topicName,
      string subscriptionName,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      foreach (var rule in ExistingRules.ToList()) {
        yield return rule;
      }
    }

    public Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default) {
      DeletedRules.Add((topicName, subscriptionName, ruleName));
      ExistingRules.RemoveAll(r => r.Name == ruleName);
      return Task.CompletedTask;
    }

    public Task CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default) {
      if (CreateRuleException is not null) {
        return Task.FromException(CreateRuleException);
      }
      CreatedRules.Add((topicName, subscriptionName, options));
      return Task.CompletedTask;
    }
  }
}
