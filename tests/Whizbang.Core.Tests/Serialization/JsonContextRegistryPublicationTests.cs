// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// That <see cref="JsonContextRegistry.Generation"/> counts a registration only once the registration
/// is visible, so options cached against a generation hold every registration it counts (#1900).
/// </summary>
/// <remarks>
/// <para>
/// A cache keyed on the generation reads it, builds from the registry, and reuses the build until the
/// generation moves. If a registration advanced the generation before its entry was in the registry, a
/// build in between read the new generation, missed the entry, and was reused as current until some
/// later registration. The types the missed entry describes then could not be read through the cached
/// options, for as long as nothing else registered.
/// </para>
/// <para>
/// Each test builds inside that window deterministically, on one thread: the registry's test seam runs
/// a build at the point after a registration takes its sequence number and before the registration is
/// visible. The build in the window may miss the entry; what must hold is that the options returned
/// after the registration completes include it.
/// </para>
/// </remarks>
/// <docs>extending/internals/json-serialization-customizations</docs>
[Category("JsonSerialization")]
[NotInParallel]
public class JsonContextRegistryPublicationTests {
  private const string REUSED_STALE =
    "a build made before the registration was visible must not be reused once the registration completes";

  /// <summary>A context registered while options are being built is in the options after it completes.</summary>
  [Test]
  public async Task AContextRegisteredDuringABuild_IsInTheOptionsAfterwardAsync() {
    var resolver = new MarkerResolver<ContextMarker>();

    using (JsonContextRegistry.AfterSequenceAllocatedForTests(_buildCompleteChain)) {
      JsonContextRegistry.RegisterContext(resolver, priority: int.MaxValue);
    }

    var options = JsonContextRegistry.WithCompleteChain(null);
    var info = options.TypeInfoResolver!.GetTypeInfo(typeof(ContextMarker), options);

    await Assert.That(info).IsNotNull().Because(REUSED_STALE);
    await Assert.That(resolver.Consulted).IsTrue();
  }

  /// <summary>A converter registered while options are being built is in the options after it completes.</summary>
  [Test]
  public async Task AConverterRegisteredDuringABuild_IsInTheOptionsAfterwardAsync() {
    var converter = new MarkerConverter();

    using (JsonContextRegistry.AfterSequenceAllocatedForTests(_buildCompleteChain)) {
      JsonContextRegistry.RegisterConverter(converter, priority: -1000);
    }

    var options = JsonContextRegistry.WithCompleteChain(null);

    await Assert.That(options.Converters.Contains(converter)).IsTrue().Because(REUSED_STALE);
  }

  /// <summary>A modifier registered while options are being built runs in the options after it completes.</summary>
  [Test]
  public async Task AModifierRegisteredDuringABuild_RunsInTheOptionsAfterwardAsync() {
    JsonContextRegistry.RegisterContext(new MarkerResolver<ModifierMarker>(), priority: int.MaxValue);
    var modified = false;
    void Modify(JsonTypeInfo info) => modified |= info.Type == typeof(ModifierMarker);

    try {
      using (JsonContextRegistry.AfterSequenceAllocatedForTests(_buildCompleteChain)) {
        JsonContextRegistry.RegisterTypeInfoModifier(Modify);
      }

      var options = JsonContextRegistry.WithCompleteChain(null);
      _ = options.TypeInfoResolver!.GetTypeInfo(typeof(ModifierMarker), options);

      await Assert.That(modified).IsTrue().Because(REUSED_STALE);
    } finally {
      JsonContextRegistry.RemoveTypeInfoModifierForTests(Modify);
    }
  }

  /// <summary>
  /// The same holds for options completed from a host's own options, which are cached per host
  /// options instance against the generation.
  /// </summary>
  [Test]
  public async Task AContextRegisteredDuringACompletion_IsInTheHostsCompletedOptionsAfterwardAsync() {
    var hostOptions = new JsonSerializerOptions();
    var resolver = new MarkerResolver<HostMarker>();

    using (JsonContextRegistry.AfterSequenceAllocatedForTests(() => _ = JsonContextRegistry.WithCompleteChain(hostOptions))) {
      JsonContextRegistry.RegisterContext(resolver, priority: int.MaxValue);
    }

    var options = JsonContextRegistry.WithCompleteChain(hostOptions);
    var info = options.TypeInfoResolver!.GetTypeInfo(typeof(HostMarker), options);

    await Assert.That(info).IsNotNull().Because(REUSED_STALE);
  }

  /// <summary>Disposing the seam's scope puts back whatever callback was there before it.</summary>
  [Test]
  public async Task TheSeamsScopeRestoresThePreviousCallbackAsync() {
    var outerRuns = 0;
    var innerRuns = 0;

    using (JsonContextRegistry.AfterSequenceAllocatedForTests(() => outerRuns++)) {
      using (JsonContextRegistry.AfterSequenceAllocatedForTests(() => innerRuns++)) {
        JsonContextRegistry.RegisterConverter(new MarkerConverter(), priority: -1000);
      }
      JsonContextRegistry.RegisterConverter(new MarkerConverter(), priority: -1000);
    }
    JsonContextRegistry.RegisterConverter(new MarkerConverter(), priority: -1000);

    await Assert.That(innerRuns).IsEqualTo(1);
    await Assert.That(outerRuns).IsEqualTo(1)
      .Because("the inner scope hands back the outer callback, and the outer scope clears it");
  }

  private static void _buildCompleteChain() => _ = JsonContextRegistry.WithCompleteChain(null);

  /// <summary>Describes one private type and nothing else, recording whether it was asked.</summary>
  private sealed class MarkerResolver<T> : IJsonTypeInfoResolver {
    public bool Consulted { get; private set; }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type != typeof(T)) {
        return null;
      }
      Consulted = true;
      return JsonTypeInfo.CreateJsonTypeInfo<T>(options);
    }
  }

  private sealed class ContextMarker;

  private sealed class ModifierMarker;

  private sealed class HostMarker;

  /// <summary>A converter for a type nothing else serializes, so registering it changes nothing else.</summary>
  private sealed class MarkerConverter : JsonConverter<MarkerConverter> {
    public override MarkerConverter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => null;
    public override void Write(Utf8JsonWriter writer, MarkerConverter value, JsonSerializerOptions options) { }
  }
}
