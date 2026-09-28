using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// <c>JsonContextRegistry.WithCompleteChain</c>: the options the framework reads and writes a
/// consumer's stored messages with. The registry's contexts answer first, the host's resolver and
/// converters behind them, and a discriminator out of first position is accepted (#938, #939).
/// </summary>
[Category("JsonSerialization")]
public class JsonContextRegistryCompleteChainTests {

  [Test]
  public async Task WithCompleteChain_RegistryBuiltOptions_ReturnedUnchangedAsync() {
    var combined = JsonContextRegistry.CreateCombinedOptions();

    await Assert.That(JsonContextRegistry.WithCompleteChain(combined)).IsSameReferenceAs(combined)
      .Because("options the registry built are already complete; replacing them would discard their metadata cache");
  }

  [Test]
  public async Task WithCompleteChain_NoHostOptions_IsTheRegistryAndReusedAsync() {
    var generation = JsonContextRegistry.Generation;
    var first = JsonContextRegistry.WithCompleteChain(null);
    var second = JsonContextRegistry.WithCompleteChain(null);

    await Assert.That(first.AllowOutOfOrderMetadataProperties).IsTrue();
    // Another test may register a context in between; only then may the options differ.
    await Assert.That(ReferenceEquals(first, second) || JsonContextRegistry.Generation != generation).IsTrue()
      .Because("building options per call throws the serializer's metadata cache away every time");
  }

  [Test]
  public async Task WithCompleteChain_HostOptions_ResolvesRegisteredTypesTheHostCannotAsync() {
    var host = new JsonSerializerOptions { TypeInfoResolver = new NothingResolver() };

    var generation = JsonContextRegistry.Generation;
    var completed = JsonContextRegistry.WithCompleteChain(host);
    var again = JsonContextRegistry.WithCompleteChain(host);

    await Assert.That(completed).IsNotSameReferenceAs(host);
    await Assert.That(completed.AllowOutOfOrderMetadataProperties).IsTrue()
      .Because("a jsonb column moves a discriminator behind any shorter key");
    await Assert.That(completed.GetTypeInfo(typeof(TenantCollectiveScope))).IsNotNull();
    await Assert.That(ReferenceEquals(again, completed) || JsonContextRegistry.Generation != generation).IsTrue()
      .Because("the completed options are reused for the same host options while the registry is unchanged");
    await Assert.That(JsonContextRegistry.WithCompleteChain(completed)).IsSameReferenceAs(completed)
      .Because("completing a completed set changes nothing");
  }

  [Test]
  public async Task WithCompleteChain_HostResolver_StillAnswersForTypesOnlyItKnowsAsync() {
    var host = new JsonSerializerOptions { TypeInfoResolver = new HostOnlyResolver() };

    var completed = JsonContextRegistry.WithCompleteChain(host);
    var info = completed.GetTypeInfo(typeof(HostOnlyType));

    await Assert.That(info.Type).IsEqualTo(typeof(HostOnlyType))
      .Because("a type only the host's resolver can describe must keep resolving behind the registry");
  }

  [Test]
  public async Task WithCompleteChain_HostWithoutResolver_UsesTheRegistryAloneAsync() {
    var host = new JsonSerializerOptions();

    var completed = JsonContextRegistry.WithCompleteChain(host);

    await Assert.That(completed.GetTypeInfo(typeof(TenantCollectiveScope))).IsNotNull();
  }

  [Test]
  public async Task WithCompleteChain_HostConvertersFirst_RegistryConvertersAddedBehindThemOnceAsync() {
    var registryConverters = JsonContextRegistry.CreateCombinedOptions().Converters;
    var host = new JsonSerializerOptions { TypeInfoResolver = new NothingResolver() };
    host.Converters.Add(new HostOnlyConverter());
    if (registryConverters.Count > 0) {
      host.Converters.Add(registryConverters[0]);
    }

    var completed = JsonContextRegistry.WithCompleteChain(host);

    await Assert.That(completed.Converters[0]).IsTypeOf<HostOnlyConverter>()
      .Because("the host's converters keep their place in front");
    foreach (var registryConverter in registryConverters) {
      await Assert.That(completed.Converters.Count(c => c.GetType() == registryConverter.GetType())).IsEqualTo(1)
        .Because("a registry converter is added only for a type the host does not already convert");
    }
  }

  [Test]
  public async Task WithCompleteChain_HostSettingsAndResolver_KeptAheadOfTheRegistryAsync() {
    var host = new JsonSerializerOptions {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      TypeInfoResolver = new HostOnlyResolver(),
    };

    var completed = JsonContextRegistry.WithCompleteChain(host);

    await Assert.That(completed.PropertyNamingPolicy).IsEqualTo(JsonNamingPolicy.CamelCase)
      .Because("a naming policy the host chose is how its stored rows were written; reading them any other way loses fields");
    await Assert.That(completed.GetTypeInfo(typeof(HostOnlyType)).Converter).IsTypeOf<HostOnlyConverter>();
  }

  [Test]
  [NotInParallel]
  public async Task WithCompleteChain_AfterALateRegistration_RebuildsAsync() {
    var host = new JsonSerializerOptions { TypeInfoResolver = new NothingResolver() };
    var before = JsonContextRegistry.WithCompleteChain(host);
    var beforeNoHost = JsonContextRegistry.WithCompleteChain(null);

    JsonContextRegistry.RegisterContext(new NothingResolver());

    await Assert.That(JsonContextRegistry.WithCompleteChain(host)).IsNotSameReferenceAs(before)
      .Because("an assembly that registers late brings contexts options built earlier cannot resolve");
    await Assert.That(JsonContextRegistry.WithCompleteChain(null)).IsNotSameReferenceAs(beforeNoHost);
  }

  // ------------------------------------------------------------------------------------------

  private sealed class NothingResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
  }

  /// <summary>A type no registered context describes.</summary>
  private sealed class HostOnlyType;

  private sealed class HostOnlyResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(HostOnlyType)
        ? JsonMetadataServices.CreateValueInfo<HostOnlyType>(options, new HostOnlyConverter())
        : null;
  }

  private sealed class HostOnlyConverter : JsonConverter<HostOnlyType> {
    public override HostOnlyType? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();
    public override void Write(Utf8JsonWriter writer, HostOnlyType value, JsonSerializerOptions options) => writer.WriteNullValue();
  }
}
