// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// The RabbitMQ receive paths hand every message that is a valid envelope to the handler, which stores it, before the
/// broker is told anything; only a body that is not a readable envelope is refused, and it goes to the dead-letter
/// exchange, never dropped (#1720).
/// </summary>
/// <remarks>
/// <para>
/// A message whose envelope type this service has no JSON metadata for was nacked without requeue before anything
/// stored it, and with no consumer it was acked on the single-message path: gone with no row. A body the deserializer
/// threw on left <see cref="RabbitMQTransport.FlushBatchAsync"/> with the exception, and the batch collector re-queues
/// a batch whose flush throws, so that message and every neighbor in its batch were flushed again on every sliding
/// window, never acked or nacked.
/// </para>
/// <para>
/// Driven through the internal flush seam and the subscription's delivery callback, so every outcome is awaited
/// directly; the channel double records each ack and nack.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQTransport.cs</code-under-test>
public class RabbitMQReceiveStoresBeforeSettlingTests {
  private const string QUEUE = "store-first-queue";
  private const string UNKNOWN_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Upstream.Contracts.OrderShipped, Upstream.Contracts]], Whizbang.Core";

  [Test]
  public async Task Flush_ABodyTheDeserializerThrowsOn_IsDeadLettered_AndItsNeighborsAreStoredAndAckedAsync() {
    var channel = new RecordingChannel();
    var transport = await _transportAsync(channel);
    var (propsA, bodyA) = RabbitTestWire.ValidWireMessage("a");
    var (propsBad, _) = RabbitTestWire.ValidWireMessage("bad");
    var (propsB, bodyB) = RabbitTestWire.ValidWireMessage("b");
    var handled = new List<TransportMessage>();

    await Assert.That(async () => await transport.FlushBatchAsync(
      [_pending(channel, propsA, bodyA, 1), _pending(channel, propsBad, "{\"id\": not json"u8.ToArray(), 2), _pending(channel, propsB, bodyB, 3)],
      (batch, _) => {
        handled.AddRange(batch);
        return Task.CompletedTask;
      },
      null,
      QUEUE)).ThrowsNothing()
      .Because("a flush that throws is re-queued by the batch collector, so the batch would be flushed again forever");

    await Assert.That(handled.Select(m => m.Envelope.MessageId.Value.ToString()))
      .IsEquivalentTo([propsA.MessageId!, propsB.MessageId!]);
    await Assert.That(channel.AckAttempts).IsEquivalentTo([1UL, 3UL]);
    await Assert.That(channel.NackAttempts).IsEquivalentTo([(2UL, false)])
      .Because("a body that is not JSON cannot be stored as a message, so it goes to the dead-letter exchange");
  }

  [Test]
  public async Task Flush_AnEnvelopeOfATypeThisServiceDoesNotKnow_IsHandedOnToBeStoredBeforeItIsAckedAsync() {
    var channel = new RecordingChannel();
    var transport = await _transportAsync(channel);
    var (props, body) = _unknownTypeWireMessage();
    var acksWhenHandled = -1;
    var handled = new List<TransportMessage>();

    await transport.FlushBatchAsync(
      [_pending(channel, props, body, 4)],
      (batch, _) => {
        acksWhenHandled = channel.AckAttempts.Count;
        handled.AddRange(batch);
        return Task.CompletedTask;
      },
      null,
      QUEUE);

    await Assert.That(handled).Count().IsEqualTo(1)
      .Because("a valid envelope is stored first, whatever this service knows of its type");
    await Assert.That(handled[0].Envelope).IsTypeOf<MessageEnvelope<JsonElement>>();
    await Assert.That(handled[0].Envelope.MessageId.Value.ToString()).IsEqualTo(props.MessageId);
    await Assert.That(handled[0].EnvelopeType).IsEqualTo(UNKNOWN_ENVELOPE_TYPE)
      .Because("the row records the type the producer named");
    await Assert.That(acksWhenHandled).IsEqualTo(0).Because("the broker hears nothing until the handler has stored it");
    await Assert.That(channel.AckAttempts).IsEquivalentTo([4UL]);
    await Assert.That(channel.NackAttempts).IsEmpty();
  }

  [Test]
  public async Task Deliver_AMessageNoLocalConsumerTakes_IsHandedOnToBeStoredBeforeItIsAckedAsync() {
    using var meter = new Meter("Whizbang.Tests.RabbitMQReceiveStoresBeforeSettlingTests");
    var policy = new MessageDiscardPolicy(
      registry: new EmptyReceptorRegistry(),
      logger: new CapturingLogger<MessageDiscardPolicy>(),
      meter: meter,
      routingOptions: Options.Create(new RoutingOptions()),
      markerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance));
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, discardPolicy: policy);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => {
        handled.Add(envelope);
        return Task.CompletedTask;
      },
      RabbitTestWire.Destination());

    var (props, body) = RabbitTestWire.ValidWireMessage("unconsumed");
    await RabbitTestWire.DeliverAsync(channel, props, body, 5);

    await Assert.That(handled).Count().IsEqualTo(1)
      .Because("the skip is decided after the message is stored, where it is durable and inspectable");
    await Assert.That(channel.AckAttempts).IsEquivalentTo([5UL]);
  }

  [Test]
  public async Task Deliver_AnEnvelopeOfATypeThisServiceDoesNotKnow_IsHandedOnToBeStoredAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var handled = new List<(IMessageEnvelope Envelope, string? EnvelopeType)>();
    await transport.SubscribeAsync(
      (envelope, envelopeType, _) => {
        handled.Add((envelope, envelopeType));
        return Task.CompletedTask;
      },
      RabbitTestWire.Destination());

    var (props, body) = _unknownTypeWireMessage();
    await RabbitTestWire.DeliverAsync(channel, props, body, 6);

    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(handled[0].EnvelopeType).IsEqualTo(UNKNOWN_ENVELOPE_TYPE);
    await Assert.That(channel.AckAttempts).IsEquivalentTo([6UL]);
    await Assert.That(channel.NackAttempts).IsEmpty();
  }

  // ===== Helpers =====

  /// <summary>A valid envelope whose envelope type header names a payload type no context here describes.</summary>
  private static (BasicProperties Properties, byte[] Body) _unknownTypeWireMessage() {
    var (properties, body) = RabbitTestWire.ValidWireMessage("from-another-service");
    properties.Headers!["EnvelopeType"] = Encoding.UTF8.GetBytes(UNKNOWN_ENVELOPE_TYPE);
    return (properties, body);
  }

  private static async Task<RabbitMQTransport> _transportAsync(RecordingChannel channel) {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    return await RabbitTestWire.NewInitializedTransportAsync(connection);
  }

  private static RabbitMQTransport.PendingRabbitMessage _pending(
      IChannel channel, IReadOnlyBasicProperties properties, byte[] body, ulong deliveryTag) {
    var args = new BasicDeliverEventArgs(
      consumerTag: "test-consumer",
      deliveryTag: deliveryTag,
      redelivered: false,
      exchange: "test-exchange",
      routingKey: "#",
      properties: properties,
      body: body,
      cancellationToken: CancellationToken.None);
    return new RabbitMQTransport.PendingRabbitMessage(channel, args, body, QUEUE);
  }
}
