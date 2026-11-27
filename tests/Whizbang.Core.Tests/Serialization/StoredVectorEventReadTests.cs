// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>A passage of text with its embedding, nested in an event.</summary>
/// <param name="Text">The text.</param>
/// <param name="Embedding">Its embedding.</param>
public sealed record StoredVectorChunk(string Text, float[] Embedding);

/// <summary>An event carrying every shape of vector property the stored form distinguishes.</summary>
public sealed record StoredVectorPassageEmbeddedEvent : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid PassageId { get; init; }

  /// <summary>A top-level vector: packed by default.</summary>
  public float[] Embedding { get; init; } = [];

  /// <summary>A list vector under a declared name: packed by default, under that name.</summary>
  [JsonPropertyName("alt")]
  public IReadOnlyList<float> Alternate { get; init; } = [];

  /// <summary>Doubles: never packed, they do not fit binary32.</summary>
  public double[] Precise { get; init; } = [];

  /// <summary>Opted out: stays a JSON array.</summary>
  [StoredAsJsonArray]
  public float[] Centroid { get; init; } = [];

  /// <summary>A nested object's vector: packed, by its segmented path.</summary>
  public StoredVectorChunk Lead { get; init; } = new("", []);

  /// <summary>Vectors inside collection elements: the element type carries the reader too.</summary>
  public List<StoredVectorChunk> Chunks { get; init; } = [];
}

/// <summary>
/// Issue #2624 (V1 of the vector storage plan), through the real generated context and the options every event store,
/// replay and rebuild reads with: a body whose vectors are packed (the form the stored-body packing, V2, will write)
/// reads back bit-exact; writing still writes JSON arrays; and a <c>double[]</c> or an opted-out vector is never read
/// as packed.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.StoredVectors.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Serialization/StoredVectorConverters.cs</code-under-test>
[Category("Serialization")]
public class StoredVectorEventReadTests {
  /// <summary>Where the passage event's vectors are, as JSON names from the payload's root.</summary>
  private static readonly string[][] _vectorPaths = [["Embedding"], ["alt"], ["Lead", "Embedding"], ["Chunks", "0", "Embedding"]];

  private static StoredVectorPassageEmbeddedEvent _passage() => new() {
    PassageId = Guid.CreateVersion7(),
    Embedding = [0.1f, -0.0f, float.Epsilon, float.MaxValue],
    Alternate = [1.5f, -2.25f],
    Precise = [0.1, 0.2],
    Centroid = [3f, 4f],
    Lead = new StoredVectorChunk("lead", [7f, 1e-38f]),
    Chunks = [new StoredVectorChunk("a", [9f, -1e-45f])],
  };

  private static JsonNode? _at(JsonNode root, IReadOnlyList<string> path) =>
    path.Aggregate<string, JsonNode?>(root, static (node, segment) =>
      node is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)] : node?[segment]);

  /// <summary>Replaces the array at each path with its packed form.</summary>
  private static string _packAt(string json, IEnumerable<string[]> paths) {
    var root = JsonNode.Parse(json)!;
    foreach (var path in paths) {
      var array = _at(root, path)!.AsArray();
      var parent = path.Length == 1 ? root : _at(root, path[..^1])!;
      parent[path[^1]] = StoredVectorConvertersTests.Pack([.. array.Select(static n => n!.GetValue<float>())]);
    }
    return root.ToJsonString();
  }

  private static int[] _bits(IEnumerable<float> values) => [.. values.Select(BitConverter.SingleToInt32Bits)];

  [Test]
  [Arguments(SerializationProfile.Default)]
  [Arguments(SerializationProfile.Persistence)]
  public async Task ABodyWithPackedVectors_ReadsBackBitExactAsync(SerializationProfile profile) {
    var options = JsonContextRegistry.CreateCombinedOptions(profile);
    var written = _passage();
    var packed = _packAt(JsonSerializer.Serialize(written, options), _vectorPaths);

    var read = JsonSerializer.Deserialize<StoredVectorPassageEmbeddedEvent>(packed, options)!;

    await Assert.That(packed).DoesNotContain("\"Embedding\":[0.1")
      .Because("the fixture must hold the packed form, or the test proves nothing");
    await Assert.That(_bits(read.Embedding)).IsEquivalentTo(_bits(written.Embedding));
    await Assert.That(_bits(read.Alternate)).IsEquivalentTo(_bits(written.Alternate));
    await Assert.That(_bits(read.Lead.Embedding)).IsEquivalentTo(_bits(written.Lead.Embedding));
    await Assert.That(_bits(read.Chunks.Single().Embedding)).IsEquivalentTo(_bits(written.Chunks.Single().Embedding))
      .Because("a collection element's type carries the reader as any other type does");
    await Assert.That(read.Precise).IsEquivalentTo(written.Precise);
    await Assert.That(read.Centroid).IsEquivalentTo(written.Centroid);
  }

  [Test]
  public async Task WritingAnEvent_StillWritesJsonArraysAsync() {
    var root = JsonNode.Parse(JsonSerializer.Serialize(_passage(), JsonContextRegistry.CreateCombinedOptions()))!;

    foreach (var path in _vectorPaths) {
      await Assert.That(_at(root, path)).IsTypeOf<JsonArray>()
        .Because("no write packs in this release; the wire and the stored body keep JSON floats until V2's gate opens");
    }
  }

  [Test]
  [Arguments("Precise")]
  [Arguments("Centroid")]
  public async Task AnOptedOutOrDoubleVector_IsNeverReadAsPackedAsync(string member) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var root = JsonNode.Parse(JsonSerializer.Serialize(_passage(), options))!;
    root[member] = StoredVectorConvertersTests.Pack(3f, 4f);

    await Assert.That(() => JsonSerializer.Deserialize<StoredVectorPassageEmbeddedEvent>(root.ToJsonString(), options))
      .Throws<JsonException>()
      .Because("a double[] does not fit binary32 and an opted-out vector is stored as numbers, so neither has a packed reader");
  }
}
