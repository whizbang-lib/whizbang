using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Tests for the manifest-driven DARK provisioning path of
/// <see cref="ServiceBusInfrastructureProvisioner"/> (topology arc phase 5): every manifest
/// entity is created at boot with the SAME session/lock/delivery settings the subscribe-time
/// auto-provision uses, the per-process existence cache keeps re-provisioning at zero
/// management ops, and the ownership drift check flags a second service's subscription on an
/// owned command inbox.
/// </summary>
public class ServiceBusInfrastructureProvisionerManifestTests {
  private const string SERVICE_NAME = "order-service";

  /// <summary>Builds a manifest through the REAL strategy + builder (not hand-rolled records)
  /// so these tests lock the end-to-end derivation the worker will provision.</summary>
  private static TopologyManifest _namespaceManifest() {
    var context = new InboxSubscriptionContext(
      SERVICE_NAME,
      new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" },
      [new HandledMessageInfo("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands", MessageKind.Command)]);
    return TopologyManifestBuilder.Build(
      new SharedTopicOutboxStrategy(), new NamespaceInboxStrategy(), context, []);
  }

  private static TopologyManifest _sharedDefaultManifest() {
    var context = new InboxSubscriptionContext(
      SERVICE_NAME,
      new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" },
      [new HandledMessageInfo("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands", MessageKind.Command)]);
    return TopologyManifestBuilder.Build(
      new SharedTopicOutboxStrategy(), new SharedTopicInboxStrategy(), context, []);
  }

  private static ServiceBusInfrastructureProvisioner _provisioner(
      RecordingProvisioningAdminClient adminClient,
      AzureServiceBusOptions? options = null,
      TopologyDriftState? driftState = null,
      ILogger<ServiceBusInfrastructureProvisioner>? logger = null) =>
    new(adminClient,
        logger ?? NullLogger<ServiceBusInfrastructureProvisioner>.Instance,
        options,
        driftState);

  [Test]
  public async Task ProvisionManifest_PublishOnlyCommandInboxEntities_AreNeverCreatedByThePublisherAsync() {
    // Phase 6: a publisher whose manifest names a FLIPPED command inbox as a publish
    // destination must NOT create the entity — only the handling service does (dark
    // provisioning). A publisher-created, subscription-less inbox topic would turn
    // "no subscriber provisioned" into a silent broker-side drop instead of the loud
    // UnroutableDestinationException the flip guarantees.
    var adminClient = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(adminClient);
    var routingOptions = new RoutingOptions()
      .RouteCommandNamespaceToInbox(typeof(TestMessage).Namespace!);
    var context = new InboxSubscriptionContext(
      SERVICE_NAME, new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);
    var manifest = TopologyManifestBuilder.Build(
      new NamespaceOutboxStrategy(routingOptions),
      new SharedTopicInboxStrategy(),
      context,
      [
        new MessageTypeCatalogEntry(typeof(TestMessage), typeof(TestMessage).FullName!, "command", null),
        new MessageTypeCatalogEntry(typeof(TestMessage), typeof(TestMessage).FullName!, "event", null)
      ]);
    var flippedEntity = CommandInboxNaming.TopicFor(typeof(TestMessage).Namespace!);

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(manifest.PublishDestinations.Select(d => d.Address)).Contains(flippedEntity)
      .Because("precondition: the manifest really does name the flipped entity as a publish destination");
    await Assert.That(adminClient.CreatedTopics).DoesNotContain(flippedEntity);
    // Everything else still provisions exactly as before (event topic + subscription topic).
    await Assert.That(adminClient.CreatedTopics).Contains(typeof(TestMessage).Namespace!.ToLowerInvariant());
    await Assert.That(adminClient.CreatedTopics).Contains("inbox");
  }

  [Test]
  public async Task ProvisionManifest_CreatesEveryManifestTopicAsync() {
    // Arrange
    var adminClient = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(adminClient);

    // Act
    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    // Assert - subscription topics (per-namespace + system + transitional shared) all exist
    await Assert.That(adminClient.CreatedTopics).Contains("inbox.myapp.orders.commands");
    await Assert.That(adminClient.CreatedTopics).Contains("inbox.whizbang");
    await Assert.That(adminClient.CreatedTopics).Contains("inbox");
  }

  [Test]
  public async Task ProvisionManifest_CreatesSubscriptionsWithTransportSettingsAsync() {
    // The DARK-provisioned subscription must be indistinguishable from what subscribe-time
    // auto-provision would create: same RequiresSession (EnableSessions), same
    // MaxDeliveryCount (MaxDeliveryAttempts) — shared code path, locked here.
    var adminClient = new RecordingProvisioningAdminClient();
    var options = new AzureServiceBusOptions { EnableSessions = true, MaxDeliveryAttempts = 7 };
    var provisioner = _provisioner(adminClient, options);

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    var expectedName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(
      SERVICE_NAME, "inbox.myapp.orders.commands");
    var created = adminClient.CreatedSubscriptions
      .Single(s => s.Topic == "inbox.myapp.orders.commands");
    await Assert.That(created.Subscription).IsEqualTo(expectedName)
      .Because("the provisioner must derive names exactly as the transport does — "
             + "ServiceBusSubscriptionNameHelper, not a reimplementation");
    await Assert.That(created.RequiresSession).IsEqualTo(true);
    await Assert.That(created.MaxDeliveryCount).IsEqualTo(7);
  }

  [Test]
  public async Task ProvisionManifest_SecondCall_PerformsZeroManagementOpsAsync() {
    // Idempotence lock: the per-process existence cache must absorb re-provisioning
    // (connection recovery, watchdog resubscribes) at ZERO management-plane cost.
    var adminClient = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(adminClient);
    var manifest = _namespaceManifest();

    await provisioner.ProvisionManifestAsync(manifest);
    var opsAfterFirstBoot = adminClient.ManagementOpCount;

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(adminClient.ManagementOpCount).IsEqualTo(opsAfterFirstBoot)
      .Because("re-provisioning the same manifest must hit the existence cache, not the broker");
  }

  [Test]
  public async Task ProvisionManifest_BootManagementOpCount_BoundedAsync() {
    // Boot budget lock: ops must stay linear in entity count — at most 2 per topic
    // (exists + create), 4 per subscription (exists + create + rule enumeration + rule
    // create), and 1 drift enumeration per owned command inbox.
    var adminClient = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(adminClient);
    var manifest = _namespaceManifest();

    await provisioner.ProvisionManifestAsync(manifest);

    var topicCount = manifest.PublishDestinations.Select(d => d.Address)
      .Concat(manifest.Subscriptions.Select(s => s.Topic))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Count();
    var subscriptionCount = manifest.Subscriptions.Count;
    var ownedInboxCount = manifest.Subscriptions.Count(s =>
      s.Metadata?.ContainsKey(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey) == true);
    var budget = (2 * topicCount) + (4 * subscriptionCount) + ownedInboxCount;
    await Assert.That(adminClient.ManagementOpCount).IsLessThanOrEqualTo(budget)
      .Because($"boot management ops must stay within {budget} for {topicCount} topics + "
             + $"{subscriptionCount} subscriptions ({ownedInboxCount} owned command inboxes)");
  }

  [Test]
  public async Task ProvisionManifest_OwnedCommandInbox_ForeignSubscription_RecordsDriftAsync() {
    // The census-mandated runtime ownership check: build-time analysis cannot see a SECOND
    // service's registration, so the provisioning path enumerates subscriptions on each
    // command inbox it is about to own and flags any foreign one (health Degraded + error log).
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { "inbox.myapp.orders.commands" },
      ExistingSubscriptions = { ("inbox.myapp.orders.commands", "billing-service-inbox.myapp.orders.commands") }
    };
    var driftState = new TopologyDriftState();
    var provisioner = _provisioner(adminClient, driftState: driftState);

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    await Assert.That(driftState.HasDrift).IsTrue();
    var finding = driftState.Findings.Single();
    await Assert.That(finding.Entity).IsEqualTo("inbox.myapp.orders.commands");
    await Assert.That(finding.ObservedSubscription).IsEqualTo("billing-service-inbox.myapp.orders.commands");
    // ...and this service's own subscription is still created — drift degrades, never blocks boot.
    var ownName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(
      SERVICE_NAME, "inbox.myapp.orders.commands");
    await Assert.That(adminClient.CreatedSubscriptions.Select(s => s.Subscription)).Contains(ownName);
  }

  [Test]
  public async Task ProvisionManifest_OwnedCommandInbox_OwnSubscriptionOnly_NoDriftAsync() {
    // A previous boot of THIS service is not drift — only a foreign subscription is.
    var ownName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(
      SERVICE_NAME, "inbox.myapp.orders.commands");
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { "inbox.myapp.orders.commands" },
      ExistingSubscriptions = { ("inbox.myapp.orders.commands", ownName) }
    };
    var driftState = new TopologyDriftState();
    var provisioner = _provisioner(adminClient, driftState: driftState);

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    await Assert.That(driftState.HasDrift).IsFalse();
  }

  [Test]
  public async Task ProvisionManifest_SharedEntities_NeverDriftCheckedAsync() {
    // The shared inbox and the system broadcast inbox legitimately carry EVERY service's
    // subscription — the ownership check applies only to owned per-namespace command inboxes.
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { "inbox", "inbox.whizbang" },
      ExistingSubscriptions = {
        ("inbox", "billing-service-inbox"),
        ("inbox.whizbang", "billing-service-inbox.whizbang")
      }
    };
    var driftState = new TopologyDriftState();
    var provisioner = _provisioner(adminClient, driftState: driftState);

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    await Assert.That(driftState.HasDrift).IsFalse()
      .Because("foreign subscriptions on shared entities are the normal topology");
  }

  [Test]
  public async Task ProvisionManifest_DefaultSharedStrategy_CreatesExactlyTodaysEntitiesAsync() {
    // DARK guarantee: an existing consumer on the DEFAULT shared strategy gets ZERO new
    // entities from manifest provisioning — the shared inbox topic and its own subscription,
    // nothing namespace-shaped.
    var adminClient = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(adminClient);

    await provisioner.ProvisionManifestAsync(_sharedDefaultManifest());

    await Assert.That(adminClient.CreatedTopics.Count).IsEqualTo(1);
    await Assert.That(adminClient.CreatedTopics).Contains("inbox");
    await Assert.That(adminClient.CreatedSubscriptions.Count).IsEqualTo(1);
    await Assert.That(adminClient.CreatedSubscriptions[0].Topic).IsEqualTo("inbox");
    foreach (var topic in adminClient.CreatedTopics) {
      await Assert.That(topic.StartsWith("inbox.", StringComparison.Ordinal)).IsFalse()
        .Because("zero new entities for existing consumers — that is the DARK guarantee");
    }
  }

  [Test]
  public async Task ProvisionManifest_SubscriptionWithRoutingPatterns_AppliesFilterRuleAsync() {
    // Subscribe-time auto-provision applies the routing-pattern SqlFilter; a DARK-provisioned
    // subscription must not sit filterless until first subscribe (an unfiltered shared-inbox
    // subscription would buffer EVERY command in the meantime).
    var adminClient = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(adminClient);

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    var systemRule = adminClient.CreatedRules
      .Where(r => r.Topic == "inbox.whizbang")
      .ToList();
    await Assert.That(systemRule.Count).IsEqualTo(1)
      .Because("the system broadcast inbox subscription carries routing patterns — the filter "
             + "must be applied at provision time, same as subscribe time");
  }

  [Test]
  public async Task ProvisionManifest_RetirementManifest_SharedInboxReceivesZeroManagementOpsAsync() {
    // Phase 7: under full flip + retirement the manifest carries zero shared-inbox
    // references, so the provisioner never touches the legacy entity — no creates, no
    // subscription writes, no rules, not even an existence probe. The per-namespace inbox
    // and the system broadcast inbox still provision exactly as phase 5 shipped them.
    var adminClient = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(adminClient);
    var options = new RoutingOptions().RouteAllCommandNamespacesToInbox().RetireSharedInbox();
    var context = new InboxSubscriptionContext(
      SERVICE_NAME,
      new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      [new HandledMessageInfo("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands", MessageKind.Command)]);
    var manifest = TopologyManifestBuilder.Build(
      new NamespaceOutboxStrategy(options), new NamespaceInboxStrategy(options), context, []);

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(adminClient.CreatedTopics).Contains("inbox.myapp.orders.commands");
    await Assert.That(adminClient.CreatedTopics).Contains("inbox.whizbang");
    await Assert.That(adminClient.CreatedTopics).DoesNotContain("inbox");
    await Assert.That(adminClient.TopicExistenceChecks).DoesNotContain("inbox")
      .Because("retirement means ZERO management operations on the legacy shared topic — probes included");
    await Assert.That(adminClient.CreatedSubscriptions.Select(s => s.Topic)).DoesNotContain("inbox")
      .Because("no subscription may recreate the catch-all after retirement");
    await Assert.That(adminClient.CreatedRules.Select(r => r.Topic)).DoesNotContain("inbox");
  }

  [Test]
  public async Task ProvisionManifest_NullManifest_ThrowsArgumentNullExceptionAsync() {
    var provisioner = _provisioner(new RecordingProvisioningAdminClient());

    await Assert.That(() => provisioner.ProvisionManifestAsync(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ProvisionManifest_SubscriptionCreate409Race_LogsAndSkipsAsync() {
    // Multiple instances provision concurrently at startup by design — a 409 from another
    // instance winning the subscription-create race must not fail boot for the loser. The
    // observable contract is: no throw, and a diagnostic naming which subscription raced.
    var adminClient = new RecordingProvisioningAdminClient {
      CreateSubscriptionException = new RequestFailedException(409, "Subscription already exists", "Conflict", null)
    };
    var logger = new CapturingLogger<ServiceBusInfrastructureProvisioner>();
    var provisioner = _provisioner(adminClient, logger: logger);

    // Act - should not throw despite every subscription create racing
    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    var ownName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(
      SERVICE_NAME, "inbox.myapp.orders.commands");
    await Assert.That(logger.Messages.Any(m =>
        m.Contains("inbox.myapp.orders.commands", StringComparison.Ordinal)
        && m.Contains(ownName, StringComparison.Ordinal)))
      .IsTrue()
      .Because("the diagnostic must name which topic/subscription raced, not merely fire");
  }

  [Test]
  public async Task ProvisionManifest_OwnershipDriftCheck_EnumerationFails_LogsAndContinuesAsync() {
    // The ownership-drift check is explicitly best-effort: a transient management-plane error
    // while enumerating existing subscriptions must never block provisioning or startup. The
    // real invariant is that the provisioner completes normally and still creates this
    // service's own subscription; the log line is evidence the failure was noticed, not
    // swallowed blind.
    var adminClient = new RecordingProvisioningAdminClient {
      GetSubscriptionsException = new RequestFailedException(503, "Service temporarily unavailable", "ServiceUnavailable", null)
    };
    var logger = new CapturingLogger<ServiceBusInfrastructureProvisioner>();
    var provisioner = _provisioner(adminClient, logger: logger);

    // Act - should not throw despite the drift-check enumeration failing
    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    var ownName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName(
      SERVICE_NAME, "inbox.myapp.orders.commands");
    await Assert.That(adminClient.CreatedSubscriptions.Select(s => s.Subscription)).Contains(ownName)
      .Because("a best-effort drift-check failure must not block provisioning of this service's own subscription");
    await Assert.That(logger.Messages.Any(m => m.Contains("inbox.myapp.orders.commands", StringComparison.Ordinal)))
      .IsTrue()
      .Because("the diagnostic must name which topic's drift check was skipped, not merely fire");
  }

  /// <summary>A logger that is enabled at every level and keeps what it was told — the
  /// zero-sink logger built via LoggerFactory.Create(...) reports IsEnabled(Debug) as false
  /// with no provider attached, which would never enter the guarded log statements this file
  /// tests against.</summary>
  private sealed class CapturingLogger<T> : ILogger<T> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];

    public List<string> Messages {
      get { lock (_lock) { return [.. _messages]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }
}
