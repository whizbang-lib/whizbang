using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// One bad derived type must not poison every polymorphic serialize. Observed live: a consumer
/// event carried a property type (<c>HashSet&lt;string&gt;</c>) that no registered context held
/// metadata for. The type itself RESOLVED (its root metadata existed), so the resolvability guard
/// admitted it — but STJ configures a polymorphic typeinfo by configuring every derived type's
/// PROPERTY GRAPH, so the first serialize of ANY message on that options failed with
/// NotSupportedException. Every redelivery publish in the fleet failed; repairs selected events,
/// threw at serialize, and redelivered in a loop.
///
/// <para>
/// The guard now TRIAL-CONFIGURES each derived type (on scratch options with the same resolver
/// chain, so the composite-contains-IMessage cycle stays safe) and QUARANTINES the ones that
/// cannot configure: serialization of every other type proceeds, the bad type is skipped, and
/// its identity is surfaced via <see cref="JsonContextRegistry.QuarantinedDerivedTypes"/> so a
/// health surface can name it instead of the whole wire going dark.
/// </para>
/// </summary>
/// <docs>fundamentals/messages/json-serialization</docs>
[NotInParallel("JsonContextRegistryMutation")]
public class PolymorphicQuarantineTests {

  /// <summary>A healthy wire event — must keep serializing no matter what else registers.</summary>
  public sealed record HealthyProbeEvent : IEvent {
    [StreamId]
    public Guid ProbeStreamId { get; init; }
    public int X { get; init; }
  }

  /// <summary>The production shape: root metadata EXISTS (the resolvability guard admits it) but
  /// a property's type has no metadata anywhere, so CONFIGURE throws.</summary>
  public sealed record PoisonedProbeEvent : IEvent {
    [StreamId]
    public Guid ProbeStreamId { get; init; }
    public HashSet<string> Keys { get; init; } = [];
  }

  /// <summary>
  /// Hand-written context reproducing the live failure faithfully: it serves ROOT metadata for
  /// <see cref="PoisonedProbeEvent"/> whose property metadata initializer binds the
  /// <c>HashSet&lt;string&gt;</c> property against the serializing options — exactly what a
  /// source-generated context does — and those options hold no HashSet metadata, so CONFIGURE
  /// (not resolution) throws.
  /// </summary>
  private sealed class _poisonResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type != typeof(PoisonedProbeEvent)) {
        return null;
      }
      var info = JsonMetadataServices.CreateObjectInfo<PoisonedProbeEvent>(options, new JsonObjectInfoValues<PoisonedProbeEvent> {
        ObjectCreator = () => new PoisonedProbeEvent(),
        PropertyMetadataInitializer = ctx => [
          JsonMetadataServices.CreatePropertyInfo<HashSet<string>>(ctx.Options, new JsonPropertyInfoValues<HashSet<string>> {
            IsProperty = true,
            DeclaringType = typeof(PoisonedProbeEvent),
            PropertyName = nameof(PoisonedProbeEvent.Keys),
            Getter = o => ((PoisonedProbeEvent)o!).Keys,
          }),
        ],
        SerializeHandler = null,
      });
      return info;
    }
  }

  [Test]
  public async Task PoisonedDerivedType_IsQuarantined_EveryOtherSerializeStillWorksAsync() {
    JsonContextRegistry.RegisterContext(QuarantineProbeJsonContext.Default);
    JsonContextRegistry.RegisterContext(new _poisonResolver());
    JsonContextRegistry.RegisterDerivedType<IMessage, HealthyProbeEvent>(
      TypeNameFormatter.FormatClrTypeName(typeof(HealthyProbeEvent)));
    JsonContextRegistry.RegisterDerivedType<IEvent, HealthyProbeEvent>(
      TypeNameFormatter.FormatClrTypeName(typeof(HealthyProbeEvent)));
    JsonContextRegistry.RegisterDerivedType<IMessage, PoisonedProbeEvent>(
      TypeNameFormatter.FormatClrTypeName(typeof(PoisonedProbeEvent)));
    JsonContextRegistry.RegisterDerivedType<IEvent, PoisonedProbeEvent>(
      TypeNameFormatter.FormatClrTypeName(typeof(PoisonedProbeEvent)));

    var options = JsonContextRegistry.CreateCombinedOptions();

    // The load-bearing assertion: serializing a HEALTHY message through the polymorphic base —
    // the composite/envelope path that failed fleet-wide — must succeed even though a poisoned
    // type is registered beside it.
    IMessage healthy = new HealthyProbeEvent { ProbeStreamId = TrackedGuid.NewMedo().Value, X = 7 };
    var json = JsonSerializer.Serialize(healthy, options.GetTypeInfo(typeof(IMessage)));
    await Assert.That(json).Contains("\"X\":7")
      .Because("one bad contract type must degrade THAT type, never the whole wire");

    await Assert.That(JsonContextRegistry.QuarantinedDerivedTypes
        .Any(q => q.DerivedType == typeof(PoisonedProbeEvent))).IsTrue()
      .Because("the quarantine must NAME the offender — a silent skip would hide a real contract defect");
  }
}

[JsonSerializable(typeof(PolymorphicQuarantineTests.HealthyProbeEvent))]
public sealed partial class QuarantineProbeJsonContext : JsonSerializerContext;
