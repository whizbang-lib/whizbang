using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests;

/// <summary>
/// That the registry says when it has changed, so options built from it can be reused until then.
/// </summary>
/// <remarks>
/// A set of serializer options carries the serializer's metadata cache, which is expensive to
/// build and thrown away with the options. A caller that rebuilt per call to be safe against a
/// late registration paid that cost on every document; a caller that cached forever could not
/// resolve a type from an assembly loaded after it cached. The generation is what lets a caller
/// do neither: reuse while nothing changed, rebuild once when something did.
/// </remarks>
/// <docs>extending/internals/json-serialization-customizations</docs>
public class JsonContextRegistryGenerationTests {
  /// <summary>Registering a context advances the generation.</summary>
  [Test]
  public async Task RegisteringAContextAdvancesTheGenerationAsync() {
    var before = JsonContextRegistry.Generation;

    JsonContextRegistry.RegisterContext(new NothingResolver());

    await Assert.That(JsonContextRegistry.Generation).IsGreaterThan(before);
  }

  /// <summary>Registering a converter advances it too, since the options carry converters as well.</summary>
  [Test]
  public async Task RegisteringAConverterAdvancesTheGenerationAsync() {
    var before = JsonContextRegistry.Generation;

    JsonContextRegistry.RegisterConverter(new NothingConverter(), priority: -1000, SerializationProfile.Persistence);

    await Assert.That(JsonContextRegistry.Generation).IsGreaterThan(before);
  }

  /// <summary>Between registrations it holds still: building options is a read, not a change.</summary>
  [Test]
  [NotInParallel]
  public async Task TheGenerationHoldsStillBetweenRegistrationsAsync() {
    var first = JsonContextRegistry.Generation;
    _ = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    _ = JsonContextRegistry.CreateCombinedOptions();

    await Assert.That(JsonContextRegistry.Generation).IsEqualTo(first)
      .Because("building options is a read; only a registration is a change");
  }

  private sealed class NothingResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
  }

  /// <summary>A converter for a type nothing in this repository serializes, so it changes nothing.</summary>
  private sealed class NothingConverter : JsonConverter<NothingConverter> {
    public override NothingConverter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => null;
    public override void Write(Utf8JsonWriter writer, NothingConverter value, JsonSerializerOptions options) { }
  }
}
