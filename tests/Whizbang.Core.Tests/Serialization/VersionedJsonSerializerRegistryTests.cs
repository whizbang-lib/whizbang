using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// The registry that picks which payload format is written and which can be read.
/// <para>
/// Versioned serializers exist so a stored payload written by an older build still deserializes
/// after the format changes. That only holds if the version-to-serializer mapping is unambiguous:
/// two serializers claiming one version means the reader's choice depends on registration order,
/// so the same stored bytes could be read differently by two hosts running the same code. The
/// registry refuses that at construction rather than resolving it silently.
/// </para>
/// <para>
/// An empty registry is refused for the same reason — there would be no format to write, and the
/// failure would otherwise surface on the first serialize, far from the wiring that caused it.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Serialization/VersionedJsonSerializerRegistry.cs</code-under-test>
public class VersionedJsonSerializerRegistryTests {

  private sealed class StubSerializer(int version) : IVersionedJsonSerializer {
    public int Version => version;
    public JsonDocument SerializePayload(object model, JsonTypeInfo typeInfo)
      => JsonDocument.Parse("{}");
    public object DeserializePayload(JsonElement payload, JsonTypeInfo typeInfo)
      => new();
  }

  [Test]
  public async Task TwoSerializersClaimingOneVersion_AreRefusedAtConstructionAsync() {
    await Assert.That(() => new VersionedJsonSerializerRegistry(
        [new StubSerializer(1), new StubSerializer(1)]))
      .Throws<ArgumentException>()
      .Because("the reader's choice would depend on registration order, so the same stored bytes "
             + "could be read differently by two hosts running identical code");
  }

  [Test]
  public async Task AnEmptyRegistry_IsRefusedRatherThanFailingOnFirstUseAsync() {
    await Assert.That(() => new VersionedJsonSerializerRegistry([]))
      .Throws<ArgumentException>()
      .Because("with no serializer there is no format to write, and discovering that on the first "
             + "serialize puts the failure far from the wiring that caused it");
  }

  [Test]
  public async Task TheHighestVersion_BecomesTheCurrentWriterAsync() {
    var registry = new VersionedJsonSerializerRegistry(
      [new StubSerializer(1), new StubSerializer(3), new StubSerializer(2)]);

    await Assert.That(registry.Current.Version).IsEqualTo(3)
      .Because("new payloads are written in the newest format; picking anything else would write "
             + "old-format data that a later migration then has to account for");
  }
}
