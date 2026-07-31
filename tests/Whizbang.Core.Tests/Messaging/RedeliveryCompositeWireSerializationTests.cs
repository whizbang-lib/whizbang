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
  public async Task RedeliveryComposite_RoundTripsPolymorphicInner_ThroughCombinedOptionsAsync() {
    _ensureRegistered();
    var options = JsonContextRegistry.CreateCombinedOptions();
    var streamId = TrackedGuid.NewMedo().Value;
    var e1 = TrackedGuid.NewMedo().Value;
    var e2 = TrackedGuid.NewMedo().Value;
    var composite = new Whizbang.Core.Messaging.RedeliveryComposite {
      StreamId = streamId,
      Inner = [new WireProbeEvent { X = 2 }, new WireProbeEvent { X = 3 }],
      InnerEventIds = [e1, e2],
    };

    var json = JsonSerializer.Serialize(composite, options.GetTypeInfo(typeof(Whizbang.Core.Messaging.RedeliveryComposite)));

    await Assert.That(json).Contains("$type")
      .Because($"inner IMessage items must carry type discriminators or no receiver can rehydrate them — " +
               $"a composite defined in Core carrying another assembly's events is exactly the cross-assembly " +
               $"case the serializing-options polymorphic binding exists for. JSON: {json}");

    var back = (Whizbang.Core.Messaging.RedeliveryComposite)JsonSerializer.Deserialize(
      json, options.GetTypeInfo(typeof(Whizbang.Core.Messaging.RedeliveryComposite)))!;
    await Assert.That(back.InnerEventIds).IsEquivalentTo([e1, e2]);
    await Assert.That(back.Inner.Cast<WireProbeEvent>().Select(p => p.X).ToList()).IsEquivalentTo([2, 3])
      .Because("the repaired bodies must survive the wire byte-for-byte.");
  }
}

/// <summary>JSON context for the wire round-trip probe (production-parity registration).</summary>
[JsonSerializable(typeof(RedeliveryCompositeWireSerializationTests.WireProbeEvent))]
public sealed partial class RedeliveryCompositeWireJsonContext : JsonSerializerContext;
