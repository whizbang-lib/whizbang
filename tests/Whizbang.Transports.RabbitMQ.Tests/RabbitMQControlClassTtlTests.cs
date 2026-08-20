using System.Globalization;
using System.Text.Json;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// The control class's short TTL, executed on RabbitMQ (topology arc phase 9). The plan's mapping
/// for this transport is per-message expiry: the derived lifetime is lifted out of the destination
/// metadata into <c>BasicProperties.Expiration</c> (milliseconds, as a string — AMQP's encoding),
/// so an expired control message is discarded by the broker instead of queueing. Lifting matters
/// for the same reason it does on Service Bus: every unlifted metadata key lands in
/// <c>Headers</c>, where the broker never looks at it.
/// </summary>
[Category("Transports")]
public class RabbitMQControlClassTtlTests {
  [Test]
  public async Task PublishAsync_TtlStampedDestination_SetsExpirationInMillisecondsAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    await transport.PublishAsync(
      RabbitTestWire.NewEnvelope(),
      ControlMessageTtl.Stamp(new TransportDestination("control-exchange", "whizbang.core.messaging.integritycheckpoint"),
        TimeSpan.FromSeconds(120)));

    await Assert.That(channel.Published).Count().IsEqualTo(1);
    await Assert.That(channel.Published[0].Properties.Expiration)
      .IsEqualTo(120_000.ToString(CultureInfo.InvariantCulture));
  }

  [Test]
  public async Task PublishAsync_NoTtlStamp_LeavesExpirationUnsetAsync() {
    // The no-op guarantee: an unstamped publish keeps the pre-phase-9 wire shape exactly.
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    await transport.PublishAsync(RabbitTestWire.NewEnvelope(), new TransportDestination("control-exchange"));

    await Assert.That(channel.Published[0].Properties.Expiration).IsNull();
  }

  [Test]
  public async Task PublishAsync_TtlStamp_DoesNotLeakIntoHeadersAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    await transport.PublishAsync(
      RabbitTestWire.NewEnvelope(),
      ControlMessageTtl.Stamp(new TransportDestination("control-exchange"), TimeSpan.FromSeconds(120)));

    var headers = channel.Published[0].Properties.Headers;
    await Assert.That(headers is null || !headers.ContainsKey(ControlMessageTtl.METADATA_KEY)).IsTrue();
  }

  [Test]
  public async Task PublishBatchAsync_TtlStampedDestination_SetsExpirationOnEveryMessageAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    var results = await transport.PublishBatchAsync(
      [_item(RabbitTestWire.NewEnvelope("one")), _item(RabbitTestWire.NewEnvelope("two"))],
      ControlMessageTtl.Stamp(new TransportDestination("control-exchange", "#"), TimeSpan.FromSeconds(90)));

    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(channel.Published).Count().IsEqualTo(2);
    foreach (var published in channel.Published) {
      await Assert.That(published.Properties.Expiration)
        .IsEqualTo(90_000.ToString(CultureInfo.InvariantCulture));
    }
  }

  [Test]
  public async Task PublishBatchAsync_PerItemTtl_OverridesTheSharedDestinationAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    await transport.PublishBatchAsync(
      [
        _item(RabbitTestWire.NewEnvelope("shared")),
        _item(RabbitTestWire.NewEnvelope("overridden"), ControlMessageTtl
          .Stamp(new TransportDestination("ignored"), TimeSpan.FromSeconds(5)).Metadata),
      ],
      ControlMessageTtl.Stamp(new TransportDestination("control-exchange", "#"), TimeSpan.FromSeconds(90)));

    await Assert.That(channel.Published[0].Properties.Expiration)
      .IsEqualTo(90_000.ToString(CultureInfo.InvariantCulture));
    await Assert.That(channel.Published[1].Properties.Expiration)
      .IsEqualTo(5_000.ToString(CultureInfo.InvariantCulture));
  }

  private static BulkPublishItem _item(
    MessageEnvelope<TestMessage> envelope,
    IReadOnlyDictionary<string, JsonElement>? perItemMetadata = null) => new() {
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
      MessageId = envelope.MessageId.Value,
      PerItemMetadata = perItemMetadata
    };
}
