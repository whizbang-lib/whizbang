using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The RECEIVE half of the redelivery-composite wire contract, exactly as a transport runs it:
/// the producer's <see cref="EnvelopeSerializer"/> stamps the wire envelope-type name, and the
/// consumer resolves that name through <see cref="JsonContextRegistry.GetTypeInfoByName"/> to bind
/// the bytes back to <see cref="MessageEnvelope{RedeliveryComposite}"/> before fan-out.
///
/// <para>
/// Observed live: the name mapping EXISTED (the generator registers composites by name, deliberately)
/// but the registered resolver could not DISPATCH the envelope type — the name lookup returned null,
/// the transport's type-binder fallback threw, and every repair bundle in a fleet was abandoned and
/// redelivered forever. Backfill cannot work if the deficient service cannot bind the bundle.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public class RedeliveryCompositeEnvelopeReceiveTests {

  private static MessageEnvelope<RedeliveryComposite> _buildEnvelope() {
    using var doc = JsonDocument.Parse("""{"orderId":"abc","qty":3}""");
    return new MessageEnvelope<RedeliveryComposite> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RedeliveryComposite {
        StreamId = TrackedGuid.NewMedo().Value,
        InnerPayloads = [doc.RootElement.Clone()],
        InnerTypeNames = ["Fake.Contracts.OrderPlaced, Fake.Contracts"],
        InnerEventIds = [TrackedGuid.NewMedo().Value],
        OriginServiceId = TrackedGuid.NewMedo().Value,
        InnerCommitSequences = [41L],
      },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = "deficient-consumer",
      StateOnly = true,
    };
  }

  [Test]
  public async Task WireEnvelopeTypeName_ResolvesThroughRegistry_AndRoundTripsAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var serializer = new EnvelopeSerializer(options);
    var envelope = _buildEnvelope();

    var serialized = serializer.SerializeEnvelope<RedeliveryComposite>(envelope);
    var wireJson = JsonSerializer.Serialize(serialized.JsonEnvelope,
      options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));

    // The consumer-side binding step, verbatim: wire name -> registry -> typeinfo.
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(serialized.EnvelopeType, options);
    await Assert.That(typeInfo).IsNotNull()
      .Because("the producer stamps this exact name on the wire — a null here means every " +
               "redelivery bundle is unbindable at the consumer and repairs loop forever");

    var roundTripped = JsonSerializer.Deserialize(wireJson, typeInfo!) as MessageEnvelope<RedeliveryComposite>;
    await Assert.That(roundTripped).IsNotNull();
    await Assert.That(roundTripped!.Payload!.InnerTypeNames[0]).IsEqualTo("Fake.Contracts.OrderPlaced, Fake.Contracts");
    await Assert.That(roundTripped.Payload.InnerCommitSequences![0]).IsEqualTo(41L);
    await Assert.That(roundTripped.Payload.InnerPayloads[0].GetProperty("qty").GetInt32()).IsEqualTo(3);
    await Assert.That(roundTripped.StateOnly).IsTrue()
      .Because("state-only must survive the wire round-trip or backfilled history re-fires triggers");
    await Assert.That(roundTripped.Target).IsEqualTo("deficient-consumer")
      .Because("a directed envelope must stay directed through the typed receive");
  }
}
