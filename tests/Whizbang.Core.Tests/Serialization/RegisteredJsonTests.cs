// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// The registered type information framework code serializes its own records with (multi-stream commands, recorded
/// integrity deliveries): read from the combined options, which are reused until a context or converter registers and
/// rebuilt after one does, so a late registration is never missed and an unchanged registry builds nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Serialization/RegisteredJson.cs</code-under-test>
[Category("JsonSerialization")]
[NotInParallel]
public class RegisteredJsonTests {
  [Test]
  public async Task TypeInfo_ForAFrameworkRecord_RoundTripsItAsync() {
    var checkpoint = new IntegrityCheckpoint {
      CheckpointStreamId = TrackedGuid.New().Value,
      OriginServiceId = TrackedGuid.New().Value,
      OriginServiceName = "orders-service",
      FromCommitSequence = 10,
      ToCommitSequence = 20,
      Buckets = [],
    };

    var json = JsonSerializer.Serialize(checkpoint, RegisteredJson.TypeInfo<IntegrityCheckpoint>());
    var back = JsonSerializer.Deserialize(json, RegisteredJson.TypeInfo<IntegrityCheckpoint>());

    await Assert.That(back).IsNotNull();
    await Assert.That(back!.OriginServiceId).IsEqualTo(checkpoint.OriginServiceId);
    await Assert.That(back.ToCommitSequence).IsEqualTo(20L);
  }

  [Test]
  public async Task Options_WithNoNewRegistration_AreReused_AndRebuiltAfterOneAsync() {
    var first = RegisteredJson.Options();
    var again = RegisteredJson.Options();

    JsonContextRegistry.RegisterConverter(new MarkerConverter(), priority: -1000);
    var afterRegistration = RegisteredJson.Options();

    await Assert.That(again).IsSameReferenceAs(first).Because("an unchanged registry must not rebuild the options");
    await Assert.That(afterRegistration).IsNotSameReferenceAs(first)
      .Because("a registration after the options were built must reach the next caller");
  }

  private sealed class MarkerConverter : JsonConverter<MarkerConverter> {
    public override MarkerConverter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => null;
    public override void Write(Utf8JsonWriter writer, MarkerConverter value, JsonSerializerOptions options) { }
  }
}
