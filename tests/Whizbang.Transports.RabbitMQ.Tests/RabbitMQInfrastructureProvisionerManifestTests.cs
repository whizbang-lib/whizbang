using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for the manifest-driven DARK provisioning path of
/// <see cref="RabbitMQInfrastructureProvisioner"/> (topology arc phase 5): exchange per
/// manifest topic, queue + binding per subscription (queue name <c>{service}-{exchange}</c> —
/// the transport's convention), idempotent via the per-process cache with declare-counters
/// asserted bounded, and the ownership drift probe (exchange exists without our queue) flagged
/// on owned command inboxes.
/// </summary>
public class RabbitMQInfrastructureProvisionerManifestTests {
  private const string SERVICE_NAME = "order-service";

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

  private static (RabbitMQInfrastructureProvisioner Provisioner, FakeChannel Channel, TopologyDriftState Drift)
      _fixture(RabbitMQOptions? options = null, FakeChannel? channel = null) {
    var fakeChannel = channel ?? new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var driftState = new TopologyDriftState();
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool,
      NullLogger<RabbitMQInfrastructureProvisioner>.Instance,
      options,
      driftState);
    return (provisioner, fakeChannel, driftState);
  }

  [Test]
  public async Task ProvisionManifest_PublishOnlyCommandInboxEntities_AreNeverDeclaredByThePublisherAsync() {
    // Phase 6: a publisher whose manifest names a FLIPPED command inbox as a publish
    // destination must NOT declare the exchange — only the handling service does (dark
    // provisioning). A publisher-declared, bindingless inbox exchange would turn
    // "no subscriber provisioned" into a silent broker-side drop instead of the loud
    // UnroutableDestinationException the flip guarantees.
    var (provisioner, channel, _) = _fixture();
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
    await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange)).DoesNotContain(flippedEntity);
    // Everything else still provisions exactly as before (event exchange + subscription exchange).
    await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange))
      .Contains(typeof(TestMessage).Namespace!.ToLowerInvariant());
    await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange)).Contains("inbox");
  }

  [Test]
  public async Task ProvisionManifest_DeclaresExchangeQueueAndBindingPerSubscriptionAsync() {
    // Arrange
    var (provisioner, channel, _) = _fixture();

    // Act
    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    // Assert - per-namespace inbox: exchange + "{service}-{exchange}" queue + binding
    await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange))
      .Contains("inbox.myapp.orders.commands");
    await Assert.That(channel.ExistingQueues)
      .Contains($"{SERVICE_NAME}-inbox.myapp.orders.commands");
    await Assert.That(channel.QueueBindings.Select(b => (b.Queue, b.Exchange)))
      .Contains(($"{SERVICE_NAME}-inbox.myapp.orders.commands", "inbox.myapp.orders.commands"));
    // ...and the system broadcast inbox binds its routing patterns
    await Assert.That(channel.QueueBindings
        .Where(b => b.Exchange == "inbox.whizbang")
        .Select(b => b.RoutingKey))
      .Contains("whizbang.core.minting.#");
  }

  [Test]
  public async Task ProvisionManifest_QueueArguments_MatchTransportConventionAsync() {
    // Args parity lock: the transport declares subscription queues with the dead-letter
    // exchange argument (AutoDeclareDeadLetterExchange default true) — a DARK-provisioned
    // queue with DIFFERENT args would make the later subscribe-time declare fail with
    // PRECONDITION_FAILED. Shared code path, locked here.
    var (provisioner, channel, _) = _fixture();

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    var queueName = $"{SERVICE_NAME}-inbox.myapp.orders.commands";
    var arguments = channel.QueueDeclareArgumentsByQueue[queueName];
    await Assert.That(arguments!.ContainsKey("x-dead-letter-exchange")).IsTrue();
    await Assert.That((string)arguments["x-dead-letter-exchange"]!)
      .IsEqualTo("inbox.myapp.orders.commands.dlx");
  }

  [Test]
  public async Task ProvisionManifest_SecondCall_PerformsZeroDeclaresAsync() {
    // Idempotence lock: re-provisioning the same manifest must hit the existence cache.
    var (provisioner, channel, _) = _fixture();
    var manifest = _namespaceManifest();

    await provisioner.ProvisionManifestAsync(manifest);
    var declaresAfterFirstBoot = channel.TotalDeclareOpCount;

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(channel.TotalDeclareOpCount).IsEqualTo(declaresAfterFirstBoot)
      .Because("re-provisioning the same manifest must not touch the broker");
  }

  [Test]
  public async Task ProvisionManifest_BootDeclareCount_BoundedAsync() {
    // Boot budget lock: declares stay linear in entity count — per subscription at most
    // 1 exchange (+1 DLX exchange) + 1 queue (+1 DLQ queue) + bindings (patterns + 1 DLQ),
    // plus 2 passive probes per owned command inbox, plus 1 exchange per publish destination.
    var (provisioner, channel, _) = _fixture();
    var manifest = _namespaceManifest();

    await provisioner.ProvisionManifestAsync(manifest);

    var subscriptionCount = manifest.Subscriptions.Count;
    var patternBindings = manifest.Subscriptions.Sum(s =>
      s.Metadata?.TryGetValue("RoutingPatterns", out var p) == true && p is IReadOnlyList<string> list
        ? Math.Max(list.Count, 1)
        : 1);
    var ownedInboxCount = manifest.Subscriptions.Count(s =>
      s.Metadata?.ContainsKey(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey) == true);
    var publishTopics = manifest.PublishDestinations.Select(d => d.Address).Distinct().Count();
    var budget = publishTopics
      + (subscriptionCount * 2 * 2)      // exchange + DLX, queue + DLQ
      + patternBindings + subscriptionCount // pattern bindings + DLQ binding
      + (ownedInboxCount * 2);           // passive probe pair
    await Assert.That(channel.TotalDeclareOpCount).IsLessThanOrEqualTo(budget)
      .Because($"boot declares must stay within {budget} for {subscriptionCount} subscriptions");
  }

  [Test]
  public async Task ProvisionManifest_OwnedInboxExchangeExistsWithoutOurQueue_RecordsDriftAsync() {
    // The RabbitMQ ownership drift probe: AMQP cannot enumerate queues bound to an exchange,
    // so the observable is "the command-inbox exchange already exists but OUR conventional
    // queue does not" — during the DARK phase only a subscribing owner creates inbox.<ns>
    // entities, so a pre-existing exchange without our queue means another service claimed it.
    var channel = new FakeChannel {
      ExistingExchanges = { "inbox.myapp.orders.commands" }
    };
    var (provisioner, _, driftState) = _fixture(channel: channel);

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    await Assert.That(driftState.HasDrift).IsTrue();
    var finding = driftState.Findings.Single();
    await Assert.That(finding.Entity).IsEqualTo("inbox.myapp.orders.commands");
  }

  [Test]
  public async Task ProvisionManifest_OwnedInboxExchangeAndOurQueueExist_NoDriftAsync() {
    // A previous boot of THIS service created both — not drift.
    var channel = new FakeChannel {
      ExistingExchanges = { "inbox.myapp.orders.commands" },
      ExistingQueues = { $"{SERVICE_NAME}-inbox.myapp.orders.commands" }
    };
    var (provisioner, _, driftState) = _fixture(channel: channel);

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    await Assert.That(driftState.HasDrift).IsFalse();
  }

  [Test]
  public async Task ProvisionManifest_FreshEntities_NoDriftAsync() {
    // First boot ever: nothing exists — the probe must not flag the normal path.
    var (provisioner, _, driftState) = _fixture();

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    await Assert.That(driftState.HasDrift).IsFalse();
  }

  [Test]
  public async Task ProvisionManifest_SharedEntities_NeverDriftProbedAsync() {
    // The shared inbox and system broadcast inbox legitimately pre-exist with other services'
    // queues — only owned per-namespace command inboxes are probed.
    var channel = new FakeChannel {
      ExistingExchanges = { "inbox", "inbox.whizbang" }
    };
    var (provisioner, _, driftState) = _fixture(channel: channel);

    await provisioner.ProvisionManifestAsync(_namespaceManifest());

    await Assert.That(driftState.HasDrift).IsFalse()
      .Because("pre-existing shared entities are the normal topology, not an ownership violation");
  }

  [Test]
  public async Task ProvisionManifest_DefaultSharedStrategy_DeclaresExactlyTodaysEntitiesAsync() {
    // DARK guarantee: an existing consumer on the DEFAULT shared strategy gets ZERO new
    // entities — the shared inbox exchange/queue/bindings (+DLX/DLQ) and publish exchanges.
    var (provisioner, channel, _) = _fixture();

    await provisioner.ProvisionManifestAsync(_sharedDefaultManifest());

    var exchanges = channel.DeclaredExchanges.Select(e => e.Exchange).Distinct().ToList();
    foreach (var exchange in exchanges) {
      await Assert.That(exchange.StartsWith("inbox.", StringComparison.Ordinal)
          && !exchange.StartsWith("inbox.dlx", StringComparison.Ordinal)).IsFalse()
        .Because($"'{exchange}' — zero new (namespace-shaped) entities for existing consumers");
    }
    await Assert.That(exchanges).Contains("inbox");
    await Assert.That(channel.ExistingQueues).Contains($"{SERVICE_NAME}-inbox");
  }

  [Test]
  public async Task ProvisionManifest_NullManifest_ThrowsArgumentNullExceptionAsync() {
    var (provisioner, _, _) = _fixture();

    await Assert.That(() => provisioner.ProvisionManifestAsync(null!))
      .Throws<ArgumentNullException>();
  }
}
