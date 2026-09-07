using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Covers <see cref="VersionedJsonSerializerRegistry.TryGet"/>, which
/// <c>VersionedJsonSerializerRegistryTests</c> never calls — those tests only exercise the
/// constructor's validation and <see cref="VersionedJsonSerializerRegistry.Current"/>.
/// </summary>
public class VersionedJsonSerializerRegistryCoverageTests {

  private sealed class StubSerializer(int version) : IVersionedJsonSerializer {
    public int Version => version;
    public JsonDocument SerializePayload(object model, JsonTypeInfo typeInfo) => JsonDocument.Parse("{}");
    public object DeserializePayload(JsonElement payload, JsonTypeInfo typeInfo) => new();
  }

  /// <summary>
  /// If TryGet stopped actually recalling by version — always missing, or always returning
  /// Current — an event stored under an OLDER schema version would be upcast/read with the wrong
  /// serializer, silently corrupting a historical payload instead of replaying it as it was written.
  /// </summary>
  [Test]
  public async Task TryGet_KnownVersion_ReturnsThatVersionsSerializerAsync() {
    var v1 = new StubSerializer(1);
    var v2 = new StubSerializer(2);
    var registry = new VersionedJsonSerializerRegistry([v1, v2]);

    var found = registry.TryGet(1, out var serializer);

    await Assert.That(found).IsTrue();
    await Assert.That(serializer).IsSameReferenceAs(v1)
      .Because("recalling a stored blob's version must return the SERIALIZER THAT VERSION was "
             + "written with, not the current writer — an old payload upcasts through its own "
             + "version's reader, never the newest one");
  }

  /// <summary>
  /// A version nobody registered (e.g. corrupted/future data) must fail closed — reporting "not
  /// found" — rather than the caller having to guess, or TryGet fabricating a match.
  /// </summary>
  [Test]
  public async Task TryGet_UnknownVersion_ReturnsFalseAsync() {
    var registry = new VersionedJsonSerializerRegistry([new StubSerializer(1)]);

    var found = registry.TryGet(99, out var serializer);

    await Assert.That(found).IsFalse();
    await Assert.That(serializer).IsNull();
  }
}
