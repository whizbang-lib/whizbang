using System.Text.Json;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// The unroutable-command lock (topology arc phase 6), RabbitMQ side: a destination marked
/// RequireProvisionedEntity names a CONSUMER-provisioned exchange (per-namespace command
/// inbox / system broadcast inbox). The transport must NOT auto-declare it — today's
/// auto-declare would CREATE a bindingless exchange and the broker would silently drop every
/// message — and a missing exchange must fail the publish LOUDLY with
/// <see cref="UnroutableDestinationException"/> carrying the exchange name.
/// </summary>
public class RabbitMQUnroutableDestinationTests {
  private const string FLIPPED_ENTITY = "inbox.myapp.orders.commands";

  private static TransportDestination _markedDestination(string address = FLIPPED_ENTITY) =>
    new(address,
        "myapp.orders.commands.placeordercommand",
        new Dictionary<string, JsonElement> {
          [CommandInboxNaming.RequireProvisionedEntityMetadataKey] = JsonDocument.Parse("true").RootElement.Clone()
        });

  [Test]
  public async Task PublishAsync_Marked_MissingExchange_ThrowsUnroutableWithEntityNameAsync() {
    var channel = new FakeChannel(); // no exchanges exist
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    var exception = await Assert.ThrowsAsync<UnroutableDestinationException>(() =>
      transport.PublishAsync(RabbitTestWire.NewEnvelope(), _markedDestination()));

    await Assert.That(exception!.EntityName).IsEqualTo(FLIPPED_ENTITY);
  }

  [Test]
  public async Task PublishAsync_Marked_MissingExchange_NeverAutoDeclaresAsync() {
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    await Assert.ThrowsAsync<UnroutableDestinationException>(() =>
      transport.PublishAsync(RabbitTestWire.NewEnvelope(), _markedDestination()));

    await Assert.That(channel.PassiveExchangeDeclareCount).IsEqualTo(1)
      .Because("existence must be probed passively — an active declare would CREATE the bindingless exchange");
    await Assert.That(channel.DeclaredExchanges).IsEmpty()
      .Because("publishers never create consumer-provisioned inbox entities");
    await Assert.That(channel.BasicPublishAsyncCalled).IsFalse()
      .Because("nothing may reach the broker once the entity is known missing");
  }

  [Test]
  public async Task PublishAsync_Marked_ExchangeExists_PublishesAndCachesTheProbeAsync() {
    var channel = new FakeChannel { ExistingExchanges = { FLIPPED_ENTITY } };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    await transport.PublishAsync(RabbitTestWire.NewEnvelope("first"), _markedDestination());
    await transport.PublishAsync(RabbitTestWire.NewEnvelope("second"), _markedDestination());

    await Assert.That(channel.PublishedMessages.Count).IsEqualTo(2);
    await Assert.That(channel.PublishedMessages[0].Exchange).IsEqualTo(FLIPPED_ENTITY);
    await Assert.That(channel.PassiveExchangeDeclareCount).IsEqualTo(1)
      .Because("the positive probe is cached per process — one management op per entity, not per publish");
    await Assert.That(channel.DeclaredExchanges).IsEmpty();
  }

  [Test]
  public async Task PublishAsync_Marked_ProvisionedAfterFailure_SucceedsOnRetryAsync() {
    // The negative answer must NOT be cached: the operator provisions the handling service,
    // then the outbox retry succeeds — no publisher restart required.
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    await Assert.ThrowsAsync<UnroutableDestinationException>(() =>
      transport.PublishAsync(RabbitTestWire.NewEnvelope(), _markedDestination()));

    channel.ExistingExchanges.Add(FLIPPED_ENTITY); // the handling service provisions
    await transport.PublishAsync(RabbitTestWire.NewEnvelope(), _markedDestination());

    await Assert.That(channel.PublishedMessages.Count).IsEqualTo(1);
  }

  [Test]
  public async Task PublishAsync_Unmarked_AutoDeclaresExchangeAsTodayAsync() {
    // DEFAULT LOCK: destinations without the marker keep today's idempotent auto-declare.
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    await transport.PublishAsync(
      RabbitTestWire.NewEnvelope(), new TransportDestination("myapp.orders.events", "ordercreated"));

    await Assert.That(channel.DeclaredExchanges.Count).IsEqualTo(1);
    await Assert.That(channel.DeclaredExchanges[0].Exchange).IsEqualTo("myapp.orders.events");
    await Assert.That(channel.PassiveExchangeDeclareCount).IsEqualTo(0);
    await Assert.That(channel.PublishedMessages.Count).IsEqualTo(1);
  }
}
