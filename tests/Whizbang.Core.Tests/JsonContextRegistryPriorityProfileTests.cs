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
  private sealed class _profileProbe;
  private sealed class _priorityProbe;
  private sealed class _persistenceOnlyProbe;

  private sealed class _markerResolver(Type target, string marker) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == target
        ? JsonMetadataServices.CreateValueInfo<object>(options, new _markerConverter(marker))
        : null;
  }

  private sealed class _markerConverter(string marker) : JsonConverter<object> {
    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) =>
      writer.WriteStringValue(marker);
  }

  // Typed pair for the priority test, which actually serializes (so the resolver must return a
  // JsonTypeInfo whose Type matches the requested type — STJ rejects a mismatched one).
  private sealed class _priorityResolver(string marker) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(_priorityProbe)
        ? JsonMetadataServices.CreateValueInfo<_priorityProbe>(options, new _priorityConverter(marker))
        : null;
  }

  private sealed class _priorityConverter(string marker) : JsonConverter<_priorityProbe> {
    public override _priorityProbe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
    public override void Write(Utf8JsonWriter writer, _priorityProbe value, JsonSerializerOptions options) =>
      writer.WriteStringValue(marker);
  }

  [Test]
  public async Task CreateCombinedOptions_HigherPriorityResolver_WinsRegardlessOfRegistrationOrderAsync() {
    // Register the LOW-priority resolver FIRST, HIGH-priority SECOND — if ordering were registration-based,
    // "low" would win. Priority must flip that.
    JsonContextRegistry.RegisterContext(new _priorityResolver("low"), priority: 1);
    JsonContextRegistry.RegisterContext(new _priorityResolver("high"), priority: 10);

    var options = JsonContextRegistry.CreateCombinedOptions();
    var json = JsonSerializer.Serialize(new _priorityProbe(), options);

    await Assert.That(json).IsEqualTo("\"high\"")
      .Because("The higher-priority resolver must provide the JsonTypeInfo, independent of registration order.");
  }

  [Test]
  public async Task CreateCombinedOptions_PersistenceOnlyResolver_AbsentFromDefaultProfileAsync() {
    JsonContextRegistry.RegisterContext(
      new _markerResolver(typeof(_persistenceOnlyProbe), "persist"), priority: 10, profile: SerializationProfile.Persistence);

    var persistence = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var def = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Default);

    await Assert.That(persistence.TypeInfoResolver!.GetTypeInfo(typeof(_persistenceOnlyProbe), persistence)).IsNotNull()
      .Because("A Persistence-profile resolver must be present in the Persistence options.");
    await Assert.That(def.TypeInfoResolver!.GetTypeInfo(typeof(_persistenceOnlyProbe), def)).IsNull()
      .Because("A Persistence-profile resolver must NOT leak into the Default (transport) options.");
  }

  [Test]
  public async Task RegisterContext_WithoutProfile_AppliesToAllProfilesAsync() {
    // Backward compatibility: the existing single-arg overload must keep working for every profile.
    JsonContextRegistry.RegisterContext(new _markerResolver(typeof(_profileProbe), "all"));

    var persistence = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var def = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Default);

    await Assert.That(def.TypeInfoResolver!.GetTypeInfo(typeof(_profileProbe), def)).IsNotNull();
    await Assert.That(persistence.TypeInfoResolver!.GetTypeInfo(typeof(_profileProbe), persistence)).IsNotNull();
  }

  [Test]
  public async Task CreateCombinedOptions_DefaultOnlyConverter_AbsentFromPersistenceProfileAsync() {
    var converter = new _markerConverter("default-only");
    JsonContextRegistry.RegisterConverter(converter, priority: 0, profile: SerializationProfile.Default);

    var def = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Default);
    var persistence = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);

    await Assert.That(def.Converters).Contains(converter)
      .Because("A Default-profile converter belongs in the Default (transport) options.");
    await Assert.That(persistence.Converters).DoesNotContain(converter)
      .Because("A Default-only converter must be excluded from the Persistence options (object-mode WhizbangId).");
  }
}
