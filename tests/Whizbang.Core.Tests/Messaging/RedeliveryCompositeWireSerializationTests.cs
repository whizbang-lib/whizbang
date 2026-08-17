using System.Text.Json;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Stream-integrity R1c prerequisite: a <see cref="Whizbang.Core.Messaging.RedeliveryComposite"/>
/// must round-trip through the combined JSON options with its polymorphic inner <see cref="IMessage"/>
/// list intact — discriminators written on serialize, concrete payloads restored on deserialize.
/// This is the wire fidelity every repair bundle depends on.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryComposite.cs</code-under-test>
[Category("Messaging")]
public class RedeliveryCompositeWireSerializationTests {

  public sealed record WireProbeEvent : IEvent {
    [StreamId]
    public Guid ProbeStreamId { get; init; }
    public int X { get; init; }
  }

  private static bool _registered;
  private static readonly Lock _lock = new();

  private static void _ensureRegistered() {
    lock (_lock) {
      if (!_registered) {
        JsonContextRegistry.RegisterContext(RedeliveryCompositeWireJsonContext.Default);
        JsonContextRegistry.RegisterDerivedType<IMessage, WireProbeEvent>(TypeNameFormatter.FormatClrTypeName(typeof(WireProbeEvent)));
        JsonContextRegistry.RegisterDerivedType<IEvent, WireProbeEvent>(TypeNameFormatter.FormatClrTypeName(typeof(WireProbeEvent)));
        _registered = true;
      }
    }
  }

  [Test]
  public async Task RedeliveryComposite_RoundTripsRawInner_ThroughCombinedOptionsAsync() {
    _ensureRegistered();
    var options = JsonContextRegistry.CreateCombinedOptions();
    var streamId = TrackedGuid.NewMedo().Value;
    var e1 = TrackedGuid.NewMedo().Value;
    var e2 = TrackedGuid.NewMedo().Value;
    var composite = new Whizbang.Core.Messaging.RedeliveryComposite {
      StreamId = streamId,
      InnerPayloads = [JsonDocument.Parse("{\"x\":2}").RootElement.Clone(), JsonDocument.Parse("{\"x\":3}").RootElement.Clone()],
      InnerTypeNames = ["Contracts.WireProbe, Contracts", "Contracts.WireProbe, Contracts"],
      InnerEventIds = [e1, e2],
    };

    var json = JsonSerializer.Serialize(composite, options.GetTypeInfo(typeof(Whizbang.Core.Messaging.RedeliveryComposite)));

    var back = (Whizbang.Core.Messaging.RedeliveryComposite)JsonSerializer.Deserialize(
      json, options.GetTypeInfo(typeof(Whizbang.Core.Messaging.RedeliveryComposite)))!;
    await Assert.That(back.InnerEventIds).IsEquivalentTo([e1, e2]);
    await Assert.That(back.InnerTypeNames).IsEquivalentTo(composite.InnerTypeNames);
    await Assert.That(back.InnerPayloads.Select(pd => pd.GetProperty("x").GetInt32()).ToList()).IsEquivalentTo([2, 3])
      .Because("the repaired bodies must survive the wire byte-for-byte.");
  }

  [Test]
  public async Task RedeliveryComposite_ArbitraryPayloadShapes_NeedNoTypeMetadataAsync() {
    // The AOT cliff this design removes: a consumer payload whose shape (e.g. a set-typed
    // property) has no reachable metadata in the polymorphic resolver chain. Raw carry serializes
    // it verbatim — the composite needs metadata only for ITSELF.
    _ensureRegistered();
    var options = JsonContextRegistry.CreateCombinedOptions();
    var exotic = "{\"tags\":[\"a\",\"b\"],\"nested\":{\"set\":[1,2,3]},\"unregistered\":true}";
    var composite = new Whizbang.Core.Messaging.RedeliveryComposite {
      StreamId = TrackedGuid.NewMedo().Value,
      InnerPayloads = [JsonDocument.Parse(exotic).RootElement.Clone()],
      InnerTypeNames = ["Totally.Unregistered.Type, Nowhere"],
      InnerEventIds = [TrackedGuid.NewMedo().Value],
    };

    var json = JsonSerializer.Serialize(composite, options.GetTypeInfo(typeof(Whizbang.Core.Messaging.RedeliveryComposite)));
    var back = (Whizbang.Core.Messaging.RedeliveryComposite)JsonSerializer.Deserialize(
      json, options.GetTypeInfo(typeof(Whizbang.Core.Messaging.RedeliveryComposite)))!;

    await Assert.That(back.InnerPayloads[0].GetRawText()).IsEqualTo(composite.InnerPayloads[0].GetRawText())
      .Because("an origin must be able to repair events whose payload shapes it has NO serializer " +
               "metadata for — typed rehydration made exactly this throw and the repair never shipped.");
  }
}

/// <summary>JSON context for the wire round-trip probe (production-parity registration).</summary>
[JsonSerializable(typeof(RedeliveryCompositeWireSerializationTests.WireProbeEvent))]
public sealed partial class RedeliveryCompositeWireJsonContext : JsonSerializerContext;
