using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// The plain-queue analogue of sessionless provisioning (topology arc phase 9). RabbitMQ has no
/// sessions; its ordering primitive is <c>x-single-active-consumer</c>, which pins a queue to one
/// consumer at a time. A control-class queue is declared PLAIN — no single-active-consumer — so
/// control traffic fans out across every consumer instead of queueing behind one, exactly as the
/// class's "consumers need no ordering" contract says.
/// </summary>
[Category("Transports")]
public class RabbitMQControlClassProvisioningTests {
  [Test]
  public async Task ProvisionManifest_ControlClassQueue_IsDeclaredPlainAsync() {
    var channel = new FakeChannel();
    var provisioner = _provisioner(channel, new RabbitMQOptions { EnableSingleActiveConsumer = true });

    await provisioner.ProvisionManifestAsync(_manifest(_controlSubscription()));

    var args = _argsFor(channel, CommandInboxNaming.ControlBroadcastTopic);
    await Assert.That(args!.ContainsKey("x-single-active-consumer")).IsFalse()
      .Because("pinning the control queue to one consumer reintroduces exactly the head-of-line "
             + "queueing the class exists to avoid");
  }

  [Test]
  public async Task ProvisionManifest_DurableBroadcastQueue_KeepsSingleActiveConsumerAsync() {
    // The other half of the split: durable system commands keep their ordering primitive.
    var channel = new FakeChannel();
    var provisioner = _provisioner(channel, new RabbitMQOptions { EnableSingleActiveConsumer = true });

    await provisioner.ProvisionManifestAsync(_manifest(_durableBroadcastSubscription()));

    var args = _argsFor(channel, CommandInboxNaming.SystemBroadcastTopic);
    await Assert.That(args!["x-single-active-consumer"]).IsEqualTo(true);
  }

  [Test]
  public async Task ProvisionManifest_ControlClassQueue_KeepsItsDeadLetterExchangeAsync() {
    // Sessionless/plain is about ORDERING, not about losing the DLX: the broker valve must stay
    // reachable — that is the whole point of taking the class off sessions (phase 8.5).
    var channel = new FakeChannel();
    var provisioner = _provisioner(channel, new RabbitMQOptions {
      EnableSingleActiveConsumer = true,
      AutoDeclareDeadLetterExchange = true,
    });

    await provisioner.ProvisionManifestAsync(_manifest(_controlSubscription()));

    var args = _argsFor(channel, CommandInboxNaming.ControlBroadcastTopic);
    await Assert.That(args!.ContainsKey("x-dead-letter-exchange")).IsTrue();
  }

  private static IDictionary<string, object?>? _argsFor(FakeChannel channel, string exchange) {
    var queueName = $"orders-service-{exchange}";
    return channel.QueueDeclareArgumentsByQueue[queueName];
  }

  private static RabbitMQInfrastructureProvisioner _provisioner(FakeChannel channel, RabbitMQOptions options) {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    return new RabbitMQInfrastructureProvisioner(
      pool, NullLogger<RabbitMQInfrastructureProvisioner>.Instance, options);
  }

  private static TopologyManifest _manifest(params InboxSubscription[] subscriptions) =>
    new("orders-service", [], subscriptions);

  private static InboxSubscription _controlSubscription() => new(
    Topic: CommandInboxNaming.ControlBroadcastTopic,
    FilterExpression: "whizbang.core.messaging.#",
    Metadata: new Dictionary<string, object> {
      ["RoutingPatterns"] = new List<string> { "whizbang.core.messaging.#" },
      [NamespaceInboxStrategy.ControlClassMetadataKey] = true,
    });

  private static InboxSubscription _durableBroadcastSubscription() => new(
    Topic: CommandInboxNaming.SystemBroadcastTopic,
    FilterExpression: "whizbang.core.commands.system.#",
    Metadata: new Dictionary<string, object> {
      ["RoutingPatterns"] = new List<string> { "whizbang.core.commands.system.#" },
    });
}
