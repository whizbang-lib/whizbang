// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>A message that names some of its properties for the wire, and hides one.</summary>
public sealed record WireNamedItemsCountedEvent(
    [property: StreamId] Guid BatchId,
    [property: JsonPropertyName("n")] int Count,
    [property: JsonPropertyName("src")] string Source,
    string Label) : IEvent {
  /// <summary>Never serialized.</summary>
  [JsonIgnore]
  public string Scratch { get; set; } = "";
}

/// <summary>
/// Round-trips messages through the generated message JSON contexts: the <c>[JsonPropertyName]</c>
/// names are what goes on the wire and what is read back, properties without the attribute keep their
/// C# names, an unconditional <c>[JsonIgnore]</c> property is neither written nor read, and a
/// conditional one is skipped only when writing a null or default value.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("Serialization")]
public class GeneratedContextJsonPropertyNameTests {

  private static JsonSerializerOptions _options() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    options.MakeReadOnly();
    return options;
  }

  [Test]
  public async Task GeneratedContext_ResolvesTheMessage_SoTheRoundTripExercisesItAsync() {
    var options = _options();

    var info = ((IJsonTypeInfoResolver)MessageJsonContext.Default).GetTypeInfo(typeof(WireNamedItemsCountedEvent), options);

    await Assert.That(info).IsNotNull()
      .Because("the generated context, not a reflection fallback, must be the one serializing this message");
    await Assert.That(info!.Properties.Select(p => p.Name)).IsEquivalentTo(["BatchId", "n", "src", "Label"]);
  }

  [Test]
  public async Task GeneratedContext_WritesAndReadsTheAttributeNamesAsync() {
    var options = _options();
    var batchId = Guid.CreateVersion7();
    var original = new WireNamedItemsCountedEvent(batchId, 3, "import", "batch") { Scratch = "local only" };

    var json = JsonSerializer.Serialize(original, options);
    var written = JsonNode.Parse(json)!.AsObject();

    await Assert.That(written.Select(p => p.Key)).IsEquivalentTo(["BatchId", "n", "src", "Label"])
      .Because("attribute names replace the C# names, other names are unchanged, and the ignored property is absent");
    await Assert.That(written["n"]!.GetValue<int>()).IsEqualTo(3);

    var read = JsonSerializer.Deserialize<WireNamedItemsCountedEvent>(json, options)!;

    await Assert.That(read.BatchId).IsEqualTo(batchId);
    await Assert.That(read.Count).IsEqualTo(3)
      .Because("the constructor parameter binds from the renamed property");
    await Assert.That(read.Label).IsEqualTo("batch");
    await Assert.That(read.Source).IsEqualTo("import");
    await Assert.That(read.Scratch).IsEqualTo("");
  }

  /// <summary>
  /// A scope carried inside a message keeps every value it was given. The scope's identifiers are
  /// marked <c>[JsonIgnore(Condition = WhenWritingNull)]</c>, and the generated context used to drop
  /// every <c>[JsonIgnore]</c> property outright, so a scope-update command lost its tenant and user
  /// on the way through the framework's serializer.
  /// </summary>
  [Test]
  public async Task MessageCarryingAScope_KeepsItsConditionallyIgnoredValuesAsync() {
    var options = _options();
    var command = new UpdateStreamScopeCommand {
      StreamId = Guid.CreateVersion7(),
      NewScope = new PerspectiveScope {
        TenantId = "tenant-1",
        UserId = "user-1",
        AllowedPrincipals = ["group:readers"],
        Extensions = [new ScopeExtension("region", "north"), new ScopeExtension("flag", null)],
      },
    };

    var json = JsonSerializer.Serialize(command, options);
    var scope = JsonNode.Parse(json)!["NewScope"]!.AsObject();

    await Assert.That(scope.Select(p => p.Key)).IsEquivalentTo(["t", "u", "ap", "ex"])
      .Because("set values are written under their attribute names; null ones and the false markers are skipped, as System.Text.Json skips them");
    await Assert.That(scope["ex"]![1]!.AsObject().Select(p => p.Key)).IsEquivalentTo(["k"]);

    var read = JsonSerializer.Deserialize<UpdateStreamScopeCommand>(json, options)!;

    await Assert.That(read.NewScope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(read.NewScope.UserId).IsEqualTo("user-1");
    await Assert.That(read.NewScope.CustomerId).IsNull();
    await Assert.That(read.NewScope.AllowedPrincipals).IsEquivalentTo(["group:readers"]);
    await Assert.That(read.NewScope.Extensions[0].Value).IsEqualTo("north");
    await Assert.That(read.NewScope.IsSystem).IsFalse();
  }

  [Test]
  public async Task MessageCarryingASystemScope_WritesTheMarkerOnlyWhenSetAsync() {
    var options = _options();
    var command = new UpdateStreamScopeCommand {
      StreamId = Guid.CreateVersion7(),
      NewScope = new PerspectiveScope { IsSystem = true },
    };

    var json = JsonSerializer.Serialize(command, options);

    await Assert.That(JsonNode.Parse(json)!["NewScope"]!["sys"]!.GetValue<bool>()).IsTrue()
      .Because("WhenWritingDefault skips only the default value; a set marker is written");
    await Assert.That(JsonSerializer.Deserialize<UpdateStreamScopeCommand>(json, options)!.NewScope.IsSystem).IsTrue();
  }
}
