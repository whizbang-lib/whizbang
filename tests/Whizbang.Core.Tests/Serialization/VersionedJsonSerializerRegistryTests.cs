using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Tests for the versioned-serializer registry — the framework recalls the correct serializer
/// implementation by version. Both type-agnostic (base interface) and type-specific (generic
/// interface) serializers register and resolve uniformly by <see cref="IVersionedJsonSerializer.Version"/>.
/// </summary>
public class VersionedJsonSerializerRegistryTests {
  [Test]
  public async Task Current_IsHighestRegisteredVersionAsync() {
    var registry = new VersionedJsonSerializerRegistry([new V1Serializer(), new V2Serializer()]);

    await Assert.That(registry.Current.Version).IsEqualTo(2);
  }

  [Test]
  public async Task TryGet_RecallsSerializerByVersionAsync() {
    var registry = new VersionedJsonSerializerRegistry([new V1Serializer(), new V2Serializer()]);

    var got = registry.TryGet(1, out var v1);

    await Assert.That(got).IsTrue();
    await Assert.That(v1!.Version).IsEqualTo(1);
  }

  [Test]
  public async Task TryGet_UnknownVersion_ReturnsFalseAsync() {
    var registry = new VersionedJsonSerializerRegistry([new V2Serializer()]);

    await Assert.That(registry.TryGet(99, out _)).IsFalse();
  }

  [Test]
  public async Task TypeAgnosticSerializer_RoundTripsViaTypeInfoAsync() {
    var serializer = new V2Serializer();
    var ti = _modelTypeInfo();
    var model = new Sample { Count = 9 };

    using var payload = serializer.SerializePayload(model, ti);
    var back = (Sample)serializer.DeserializePayload(payload.RootElement, ti);

    await Assert.That(back.Count).IsEqualTo(9);
  }

  [Test]
  public async Task GenericSerializer_IsResolvableAsBaseByVersionAsync() {
    // A type-specific serializer (implements the generic interface) registers + recalls
    // uniformly through the non-generic base the registry keys on.
    var registry = new VersionedJsonSerializerRegistry([new TypedSampleSerializer()]);

    var got = registry.TryGet(7, out var s);

    await Assert.That(got).IsTrue();
    await Assert.That(s).IsAssignableTo<IVersionedJsonSerializer<Sample>>();
  }

  private static JsonTypeInfo<Sample> _modelTypeInfo() =>
    (JsonTypeInfo<Sample>)new JsonSerializerOptions { TypeInfoResolver = SampleContext.Default }
      .GetTypeInfo(typeof(Sample));

  public sealed class Sample { public int Count { get; set; } }

  // Type-agnostic serializers: one instance serves any model type via the supplied TypeInfo.
  private sealed class V1Serializer : IVersionedJsonSerializer {
    public int Version => 1;
    public JsonDocument SerializePayload(object model, JsonTypeInfo typeInfo) => JsonSerializer.SerializeToDocument(model, typeInfo);
    public object DeserializePayload(JsonElement payload, JsonTypeInfo typeInfo) => JsonSerializer.Deserialize(payload.GetRawText(), typeInfo)!;
  }

  private sealed class V2Serializer : IVersionedJsonSerializer {
    public int Version => 2;
    public JsonDocument SerializePayload(object model, JsonTypeInfo typeInfo) => JsonSerializer.SerializeToDocument(model, typeInfo);
    public object DeserializePayload(JsonElement payload, JsonTypeInfo typeInfo) => JsonSerializer.Deserialize(payload.GetRawText(), typeInfo)!;
  }

  // Type-specific serializer: implements the generic interface (and thus the base).
  private sealed class TypedSampleSerializer : IVersionedJsonSerializer<Sample> {
    public int Version => 7;
    public JsonDocument SerializePayload(Sample model, JsonTypeInfo<Sample> typeInfo) => JsonSerializer.SerializeToDocument(model, typeInfo);
    public Sample DeserializePayload(JsonElement payload, JsonTypeInfo<Sample> typeInfo) => JsonSerializer.Deserialize(payload.GetRawText(), typeInfo)!;
    public JsonDocument SerializePayload(object model, JsonTypeInfo typeInfo) => SerializePayload((Sample)model, (JsonTypeInfo<Sample>)typeInfo);
    public object DeserializePayload(JsonElement payload, JsonTypeInfo typeInfo) => DeserializePayload(payload, (JsonTypeInfo<Sample>)typeInfo);
  }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(VersionedJsonSerializerRegistryTests.Sample))]
internal sealed partial class SampleContext : System.Text.Json.Serialization.JsonSerializerContext;
