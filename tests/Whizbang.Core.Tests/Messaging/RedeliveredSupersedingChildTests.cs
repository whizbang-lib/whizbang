// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Lifecycle;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2350: a redelivered event of a superseding type is stored with the key and group its origin stored it with, exactly
/// as the live receive path stores it (<c>ReceivedInboxMessageBuilder</c>). Before, the redelivery bundle's children
/// carried no key, so a restored older copy was stored unkeyed: never reaped again, and the newest by version.
/// </summary>
/// <docs>fundamentals/events/superseding-events#redelivered-copies</docs>
[Category("Unit")]
public class RedeliveredSupersedingChildTests {
  private sealed class Composite(params IMessage[] inner) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  private sealed class Serializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonElement.Parse("{}"),
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => throw new NotSupportedException();
  }

  private static ServiceProvider _scope() =>
    new ServiceCollection().AddSingleton<IEnvelopeSerializer>(new Serializer()).BuildServiceProvider();

  private static MessageEnvelope<JsonElement> _source() => new() {
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    MessageId = MessageId.New(),
    Payload = JsonSerializer.SerializeToElement(new { }),
    Hops = [],
    SourceServiceId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
    SourceCommitSequence = 42,
  };

  private static JsonElement _payload<T>(T value) =>
    JsonSerializer.SerializeToElement(value, JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(T)));

  [Test]
  public async Task ARedeliveredSupersedingChild_CarriesTheKeyAndGroupItsOriginStoredAsync() {
    var stream = TrackedGuid.New().Value;
    var region = TrackedGuid.New().Value;
    var grouped = new SupersedingEventsTests.RegionOpened(stream, region);
    var keyed = new SupersedingEventsTests.EmbeddingUpdated(stream, "model-a", 3);
    var plain = new SupersedingEventsTests.PlainEvent(stream);
    var bundle = new RedeliveryComposite {
      StreamId = stream,
      InnerPayloads = [_payload(grouped), _payload(keyed), _payload(plain)],
      InnerTypeNames = [TypeNameFormatter.Format(grouped.GetType()), TypeNameFormatter.Format(keyed.GetType()),
                        TypeNameFormatter.Format(plain.GetType())],
      InnerEventIds = [TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value],
      InnerCommitSequences = [7, 8, 9],
    };

    var result = CompositeInboxFanout.TryExpand(bundle, _source(), _scope());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    var metadata = result.Children.Select(c => c.Metadata!).ToList();
    await Assert.That(metadata[0].SupersedingKey).IsEqualTo(SupersedingEvents.KeyOf(grouped))
      .Because("the key the producer stored, read from the payload the way the live receive path reads it");
    await Assert.That(metadata[0].SupersedingGroup).IsEqualTo("region-state");
    await Assert.That(metadata[1].SupersedingKey).IsEqualTo("model-a");
    await Assert.That(metadata[1].SupersedingGroup).IsNull();
    await Assert.That(metadata[2].SupersedingKey).IsNull().Because("a type that does not supersede carries no key");
    await Assert.That(result.Children.Select(c => (long?)c.SourceCommitSequence).ToList()).IsEquivalentTo(new long?[] { 7, 8, 9 })
      .Because("and each keeps its origin's commit sequence, which orders the copies of a key across services");
  }

  [Test]
  public async Task ATypedCompositesSupersedingChild_CarriesItsKeyAndGroup_AsADirectDeliveryDoesAsync() {
    var stream = TrackedGuid.New().Value;
    var grouped = new SupersedingEventsTests.RegionOpened(stream, TrackedGuid.New().Value);
    var keyed = new SupersedingEventsTests.EmbeddingUpdated(stream, "model-b", 3);

    var result = CompositeInboxFanout.TryExpand(new Composite(grouped, keyed), _source(), _scope());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Select(c => c.Metadata!.SupersedingKey).ToList())
      .IsEquivalentTo([SupersedingEvents.KeyOf(grouped), "model-b"]);
    await Assert.That(result.Children.Select(c => c.Metadata!.SupersedingGroup).ToList())
      .IsEquivalentTo(["region-state", null]);
  }
}
