using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests;

/// <summary>
/// Locks in the priority-ranked, profile-scoped provider lookup added to <see cref="JsonContextRegistry"/>.
/// Persistence vs Default(transport) profiles let WhizbangId stay scalar for the event store while
/// perspective persistence uses object-mode — both sourced from the same cross-assembly union, ordered by
/// priority (deterministic, not assembly-load order). The registry is additive-and-static, so every test
/// here uses unique marker types to stay isolated from cross-test registrations.
/// </summary>
public partial class JsonContextRegistryTests {

  // Unique probe types so assertions are immune to other tests' registrations.
  private sealed class ProfileProbe;
  private sealed class PriorityProbe;
  private sealed class PersistenceOnlyProbe;
  private sealed class ConverterProbe;

  // A converter scoped to a unique probe type. CRITICAL: it must NOT be JsonConverter&lt;object&gt; — the
  // registry is process-wide and has no unregister, so a JsonConverter&lt;object&gt; would be added to every
  // subsequent CreateCombinedOptions(Default) and hijack ALL object-typed serialization in the test run
  // (e.g. MessageEnvelope deserialization in the work-coordinator suites). Typed to ConverterProbe,
  // CanConvert only matches that type, so it can't pollute anything else.
  private sealed class TypedMarkerConverter : JsonConverter<ConverterProbe> {
    public override ConverterProbe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
    public override void Write(Utf8JsonWriter writer, ConverterProbe value, JsonSerializerOptions options) =>
      writer.WriteStringValue("converter-probe");
  }

  private sealed class MarkerResolver(Type target, string marker) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == target
        ? JsonMetadataServices.CreateValueInfo<object>(options, new MarkerConverter(marker))
        : null;
  }

  private sealed class MarkerConverter(string marker) : JsonConverter<object> {
    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) =>
      writer.WriteStringValue(marker);
  }

  // Typed pair for the priority test, which actually serializes (so the resolver must return a
  // JsonTypeInfo whose Type matches the requested type — STJ rejects a mismatched one).
  private sealed class PriorityResolver(string marker) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(PriorityProbe)
        ? JsonMetadataServices.CreateValueInfo<PriorityProbe>(options, new PriorityConverter(marker))
        : null;
  }

  private sealed class PriorityConverter(string marker) : JsonConverter<PriorityProbe> {
    public override PriorityProbe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
    public override void Write(Utf8JsonWriter writer, PriorityProbe value, JsonSerializerOptions options) =>
      writer.WriteStringValue(marker);
  }

  [Test]
  public async Task CreateCombinedOptions_HigherPriorityResolver_WinsRegardlessOfRegistrationOrderAsync() {
    // Register the LOW-priority resolver FIRST, HIGH-priority SECOND — if ordering were registration-based,
    // "low" would win. Priority must flip that.
    JsonContextRegistry.RegisterContext(new PriorityResolver("low"), priority: 1);
    JsonContextRegistry.RegisterContext(new PriorityResolver("high"), priority: 10);

    var options = JsonContextRegistry.CreateCombinedOptions();
    var json = JsonSerializer.Serialize(new PriorityProbe(), options);

    await Assert.That(json).IsEqualTo("\"high\"")
      .Because("The higher-priority resolver must provide the JsonTypeInfo, independent of registration order.");
  }

  [Test]
  public async Task CreateCombinedOptions_PersistenceOnlyResolver_AbsentFromDefaultProfileAsync() {
    JsonContextRegistry.RegisterContext(
      new MarkerResolver(typeof(PersistenceOnlyProbe), "persist"), priority: 10, profile: SerializationProfile.Persistence);

    var persistence = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var def = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Default);

    await Assert.That(persistence.TypeInfoResolver!.GetTypeInfo(typeof(PersistenceOnlyProbe), persistence)).IsNotNull()
      .Because("A Persistence-profile resolver must be present in the Persistence options.");
    await Assert.That(def.TypeInfoResolver!.GetTypeInfo(typeof(PersistenceOnlyProbe), def)).IsNull()
      .Because("A Persistence-profile resolver must NOT leak into the Default (transport) options.");
  }

  [Test]
  public async Task RegisterContext_WithoutProfile_AppliesToAllProfilesAsync() {
    // Backward compatibility: the existing single-arg overload must keep working for every profile.
    JsonContextRegistry.RegisterContext(new MarkerResolver(typeof(ProfileProbe), "all"));

    var persistence = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var def = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Default);

    await Assert.That(def.TypeInfoResolver!.GetTypeInfo(typeof(ProfileProbe), def)).IsNotNull();
    await Assert.That(persistence.TypeInfoResolver!.GetTypeInfo(typeof(ProfileProbe), persistence)).IsNotNull();
  }

  [Test]
  public async Task CreateCombinedOptions_DefaultOnlyConverter_AbsentFromPersistenceProfileAsync() {
    var converter = new TypedMarkerConverter();
    JsonContextRegistry.RegisterConverter(converter, priority: 0, profile: SerializationProfile.Default);

    var def = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Default);
    var persistence = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);

    await Assert.That(def.Converters).Contains(converter)
      .Because("A Default-profile converter belongs in the Default (transport) options.");
    await Assert.That(persistence.Converters).DoesNotContain(converter)
      .Because("A Default-only converter must be excluded from the Persistence options (object-mode WhizbangId).");
  }
}
