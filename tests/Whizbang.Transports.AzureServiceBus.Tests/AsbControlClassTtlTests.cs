using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The control class's short TTL, executed on Azure Service Bus (topology arc phase 9). The mint
/// derives the lifetime and stamps it onto the destination; the transport LIFTS it out of the
/// metadata bag into <c>ServiceBusMessage.TimeToLive</c> — a lift, not a pass-through, because
/// every unlifted metadata key lands in <c>ApplicationProperties</c>, where a "Ttl" entry is inert
/// decoration the broker never reads. Same shape as the existing <c>StreamId → SessionId</c> lift.
/// </summary>
[Timeout(10_000)]
[Category("Transports")]
public class AsbControlClassTtlTests {
  [Test]
  public async Task PublishAsync_TtlStampedDestination_SetsMessageTimeToLiveAsync() {
    var (transport, client) = _createTransport();
    var envelope = AsbTransportTestData.CreateEnvelope();

    await transport.PublishAsync(envelope, ControlMessageTtl.Stamp(_destination(), TimeSpan.FromSeconds(120)));

    var sent = client.LastSender!.Sent;
    await Assert.That(sent).Count().IsEqualTo(1);
    await Assert.That(sent[0].TimeToLive).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task PublishAsync_NoTtlStamp_LeavesTheBrokerDefaultAsync() {
    // The single-namespace / control-class-disabled no-op guarantee: an unstamped publish must be
    // byte-identical to the pre-phase-9 wire shape. ServiceBusMessage's unset TimeToLive is
    // TimeSpan.MaxValue — "use the entity default".
    var (transport, client) = _createTransport();
    var envelope = AsbTransportTestData.CreateEnvelope();

    await transport.PublishAsync(envelope, _destination());

    await Assert.That(client.LastSender!.Sent[0].TimeToLive).IsEqualTo(TimeSpan.MaxValue);
  }

  [Test]
  public async Task PublishAsync_TtlStamp_DoesNotLeakIntoApplicationPropertiesAsync() {
    var (transport, client) = _createTransport();
    var envelope = AsbTransportTestData.CreateEnvelope();

    await transport.PublishAsync(envelope, ControlMessageTtl.Stamp(_destination(), TimeSpan.FromSeconds(120)));

    await Assert.That(client.LastSender!.Sent[0].ApplicationProperties.ContainsKey(ControlMessageTtl.METADATA_KEY))
      .IsFalse()
      .Because("the key is a framework rail, not a consumer-visible property — lifting it means "
             + "removing it, exactly as SessionId is lifted out of StreamId");
  }

  [Test]
  public async Task PublishBatchAsync_TtlStampedDestination_SetsTimeToLiveOnEveryMessageAsync() {
    var (transport, client) = _createTransport();
    var first = AsbTransportTestData.CreateEnvelope(content: "one");
    var second = AsbTransportTestData.CreateEnvelope(content: "two");

    var results = await transport.PublishBatchAsync(
      [_item(first), _item(second)],
      ControlMessageTtl.Stamp(_destination(), TimeSpan.FromSeconds(90)));

    await Assert.That(results.Count).IsEqualTo(2);

    // Assert over ALL batches rather than assuming both items share one. Streamless messages are
    // spread across bounded synthetic sessions (AsbSessionKey), and a ServiceBusMessageBatch must
    // carry a uniform session id — so two streamless items may legitimately be sent as two batches.
    // This test is about the TTL reaching every message; how they are grouped is not its subject,
    // and coupling it to a single batch made it fail for a reason it does not name.
    var messages = client.LastSender!.BatchStores.SelectMany(b => b).ToList();
    await Assert.That(messages.Count).IsEqualTo(2);
    foreach (var message in messages) {
      await Assert.That(message.TimeToLive).IsEqualTo(TimeSpan.FromSeconds(90));
    }
  }

  [Test]
  public async Task PublishBatchAsync_PerItemTtl_OverridesTheSharedDestinationAsync() {
    // A composite drain can carry mixed classes in one batch; per-item metadata is the
    // established override rail (BulkPublishItem.PerItemMetadata), so the TTL must honor it.
    var (transport, client) = _createTransport();
    var shared = AsbTransportTestData.CreateEnvelope(content: "shared");
    var overridden = AsbTransportTestData.CreateEnvelope(content: "overridden");

    await transport.PublishBatchAsync(
      [
        _item(shared),
        _item(overridden, perItemMetadata: ControlMessageTtl
          .Stamp(new TransportDestination("ignored"), TimeSpan.FromSeconds(5)).Metadata),
      ],
      ControlMessageTtl.Stamp(_destination(), TimeSpan.FromSeconds(90)));

    // Located by message id across ALL batches, not by index into one. Streamless items spread
    // across bounded synthetic sessions (AsbSessionKey) and a batch must carry a uniform session id,
    // so these two may be sent as two batches. Indexing into BatchStores[0] asserted an incidental
    // grouping this test does not care about — its subject is the per-item TTL override.
    var all = client.LastSender!.BatchStores.SelectMany(b => b).ToList();
    var sharedMessage = all.Single(m => m.MessageId == shared.MessageId.Value.ToString());
    var overriddenMessage = all.Single(m => m.MessageId == overridden.MessageId.Value.ToString());
    await Assert.That(sharedMessage.TimeToLive).IsEqualTo(TimeSpan.FromSeconds(90));
    await Assert.That(overriddenMessage.TimeToLive).IsEqualTo(TimeSpan.FromSeconds(5))
      .Because("per-item metadata must override the shared destination TTL regardless of how the "
             + "items happen to be grouped into batches");
  }

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client) _createTransport() {
    var client = new RaisableServiceBusClient();
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = false },
      NullLogger<AzureServiceBusTransport>.Instance);
    return (transport, client);
  }

  private static TransportDestination _destination() => new("bulk-topic", "orders.created");

  private static BulkPublishItem _item(
    MessageEnvelope<TestMessage> envelope,
    IReadOnlyDictionary<string, JsonElement>? perItemMetadata = null) => new() {
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
      MessageId = envelope.MessageId.Value,
      PerItemMetadata = perItemMetadata
    };
}
