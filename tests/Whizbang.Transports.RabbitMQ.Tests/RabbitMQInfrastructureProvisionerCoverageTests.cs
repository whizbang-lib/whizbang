using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Round-23 coverage additions for <see cref="RabbitMQInfrastructureProvisioner"/>'s
/// manifest-driven DARK provisioning path: the per-process existence cache short-circuit for
/// PUBLISH destinations (as opposed to subscriptions, already locked by
/// <c>RabbitMQInfrastructureProvisionerManifestTests</c>), the routing-pattern fallback when a
/// subscription carries a <c>FilterExpression</c> but no "RoutingPatterns" metadata, and the
/// ownership-drift probe's best-effort swallow of an unexpected (non-broker-404) failure.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQInfrastructureProvisioner.cs</code-under-test>
public class RabbitMQInfrastructureProvisionerCoverageTests {
  private const string SERVICE_NAME = "coverage-service";

  private static (RabbitMQInfrastructureProvisioner Provisioner, FakeChannel Channel) _fixture(
      FakeChannel? channel = null, ILogger<RabbitMQInfrastructureProvisioner>? logger = null) {
    var fakeChannel = channel ?? new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var channelPool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      channelPool,
      logger ?? NullLogger<RabbitMQInfrastructureProvisioner>.Instance,
      options: null,
      driftState: new TopologyDriftState());
    return (provisioner, fakeChannel);
  }

  // Every pod runs ProvisionManifestAsync at least once per boot, and the manifest is stable
  // across boots for an unchanged deployment — so if a publish destination already recorded in
  // the per-process cache were re-declared instead of skipped, EVERY restart would repeat the
  // exchange declare. Harmless while the args match, but it defeats the cache's entire purpose
  // and would hide the case the cache exists to short-circuit.
  [Test]
  public async Task ProvisionManifest_RepeatedPublishDestination_SkipsSecondExchangeDeclareAsync() {
    var (provisioner, channel) = _fixture();
    const string address = "myapp.orders.events";
    var manifest = new TopologyManifest(
      SERVICE_NAME,
      [new TopologyPublishDestination("MyApp.Orders.Events.OrderCreated", MessageKind.Event, address, "ordercreated", "group-1")],
      []);

    await provisioner.ProvisionManifestAsync(manifest);
    var declaresAfterFirstBoot = channel.DeclaredExchanges.Count(e => e.Exchange == address);

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(declaresAfterFirstBoot).IsEqualTo(1)
      .Because("precondition: the first pass must have declared the exchange exactly once");
    await Assert.That(channel.DeclaredExchanges.Count(e => e.Exchange == address))
      .IsEqualTo(declaresAfterFirstBoot)
      .Because("a publish destination already recorded by an earlier boot pass must be skipped, "
             + "not re-declared, on every subsequent call");
  }

  // A subscription built without "RoutingPatterns" metadata (e.g. a hand-built or legacy
  // strategy that only sets FilterExpression) must still bind on each of its comma-separated
  // patterns. Falling back to the match-all "#" wildcard instead would subscribe the queue to
  // EVERY message on the exchange rather than only the routing keys the strategy intended.
  [Test]
  public async Task ProvisionManifest_SubscriptionWithoutRoutingPatternsMetadata_SplitsFilterExpressionOnCommaAsync() {
    var (provisioner, channel) = _fixture();
    var subscription = new InboxSubscription(
      Topic: "custom.exchange",
      FilterExpression: "orders.*,shipping.*",
      Metadata: null);
    var manifest = new TopologyManifest(SERVICE_NAME, [], [subscription]);

    await provisioner.ProvisionManifestAsync(manifest);

    var queueName = $"{SERVICE_NAME}-custom.exchange";
    var boundRoutingKeys = channel.QueueBindings
      .Where(b => b.Queue == queueName && b.Exchange == "custom.exchange")
      .Select(b => b.RoutingKey)
      .ToList();
    await Assert.That(boundRoutingKeys).Contains("orders.*");
    await Assert.That(boundRoutingKeys).Contains("shipping.*");
    await Assert.That(boundRoutingKeys).DoesNotContain("#")
      .Because("the FilterExpression fallback must be honored instead of the match-all wildcard");
  }

  // "RoutingPatterns" metadata that is PRESENT but empty is the dangerous middle case: a strategy
  // that computed its pattern list and came up with nothing still leaves the key in place. The
  // metadata branch must then fall through to FilterExpression rather than returning the empty
  // list it found -- a subscription provisioned with zero bindings receives nothing at all, which
  // looks exactly like a healthy idle consumer and gets diagnosed as a producer problem.
  [Test]
  public async Task ProvisionManifest_EmptyRoutingPatternsMetadata_FallsBackToFilterExpressionAsync() {
    var (provisioner, channel) = _fixture();
    var subscription = new InboxSubscription(
      Topic: "custom.exchange",
      FilterExpression: "orders.*",
      Metadata: new Dictionary<string, object> { ["RoutingPatterns"] = Array.Empty<string>() });
    var manifest = new TopologyManifest(SERVICE_NAME, [], [subscription]);

    await provisioner.ProvisionManifestAsync(manifest);

    var queueName = $"{SERVICE_NAME}-custom.exchange";
    var boundRoutingKeys = channel.QueueBindings
      .Where(b => b.Queue == queueName && b.Exchange == "custom.exchange")
      .Select(b => b.RoutingKey)
      .ToList();
    await Assert.That(boundRoutingKeys).Contains("orders.*")
      .Because("an empty pattern list must not shadow the FilterExpression the subscription also carries");
    await Assert.That(boundRoutingKeys).IsNotEmpty()
      .Because("a queue provisioned with no bindings silently receives nothing, which is "
             + "indistinguishable from an idle consumer and gets misdiagnosed as a producer fault");
  }

  // The ownership-drift probe is explicitly best-effort (see class remarks on the production
  // type): an unrelated broker/channel-pool fault while probing — as opposed to the expected
  // "entity doesn't exist" 404 signal already covered elsewhere — must never abort startup
  // provisioning. If this swallow regressed, one flaky probe would crash the whole boot
  // sequence instead of merely skipping its own drift check for that entity.
  [Test]
  public async Task ProvisionManifest_OwnershipProbeThrowsUnexpectedException_SwallowsTracesAndCompletesProvisioningAsync() {
    var channel = new ThrowingPassiveDeclareChannel();
    var logger = new DebugCapturingLogger();
    var (provisioner, _) = _fixture(channel, logger);
    var subscription = new InboxSubscription(
      Topic: "inbox.myapp.orders.commands",
      FilterExpression: null,
      Metadata: new Dictionary<string, object> {
        [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = true,
      });
    var manifest = new TopologyManifest(SERVICE_NAME, [], [subscription]);

    await Assert.That(async () => await provisioner.ProvisionManifestAsync(manifest)).ThrowsNothing();

    await Assert.That(logger.Messages.Any(m => m.Contains("Ownership drift probe", StringComparison.Ordinal)))
      .IsTrue()
      .Because("a swallowed probe fault must still leave a trace, or a flaky broker during "
             + "startup silently disables the ownership check with nothing an operator could find");
    await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange))
      .Contains("inbox.myapp.orders.commands")
      .Because("this service's own declares must still complete despite the probe fault");
  }

  /// <summary>Passive exchange declares fail with an unrelated broker fault instead of the
  /// expected "doesn't exist" 404 — exercises the drift probe's generic best-effort catch.</summary>
  private sealed class ThrowingPassiveDeclareChannel : FakeChannel, IChannel {
    public new Task ExchangeDeclarePassiveAsync(string exchange, CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("simulated broker fault during ownership probe");
  }

  /// <summary>A logger enabled at every level, so the guarded debug trace actually runs.</summary>
  private sealed class DebugCapturingLogger : ILogger<RabbitMQInfrastructureProvisioner> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];

    public List<string> Messages {
      get { lock (_lock) { return [.. _messages]; } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }
}
