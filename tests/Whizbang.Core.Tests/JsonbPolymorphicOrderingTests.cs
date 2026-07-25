using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests;

/// <summary>
/// Regression tests for the Postgres-jsonb key-reordering vs System.Text.Json positional-discriminator
/// defect. A stored event/message whose payload contains a NESTED polymorphic member is written with
/// the <c>$type</c> discriminator first, but a <c>jsonb</c> column normalizes object keys by
/// (UTF-8 byte length, then ordinal) — so <c>$type</c> (5 bytes) is pushed out of first position
/// whenever the polymorphic object has a DIRECT sibling key of ≤4 chars. STJ then refuses to read the
/// discriminator unless <see cref="JsonSerializerOptions.AllowOutOfOrderMetadataProperties"/> is set,
/// which <see cref="JsonContextRegistry.CreateCombinedOptions()"/> must enable.
///
/// <para>Unit tests here simulate the jsonb reorder with <see cref="JsonbKeyOrderSimulator"/> (a
/// string round-trip cannot reproduce it — it preserves key order). The simulator is validated against
/// a real Postgres <c>::jsonb::text</c> result in <see cref="Simulator_MatchesRealPostgresJsonbOrderingAsync"/>.
/// The end-to-end version through a live jsonb column lives in the EF Core Postgres integration tests.</para>
/// </summary>
public partial class JsonbPolymorphicOrderingTests {
  // ----------------------------------------------------------------------------------------------
  // The fix: CreateCombinedOptions must enable out-of-order metadata on BOTH serialization profiles.
  // Event store reads use Default; perspective persistence uses Persistence. Both read from jsonb.
  // ----------------------------------------------------------------------------------------------

  [Test]
  public async Task CreateCombinedOptions_EnablesOutOfOrderMetadata_DefaultProfileAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Default);
    await Assert.That(options.AllowOutOfOrderMetadataProperties).IsTrue();
  }

  [Test]
  public async Task CreateCombinedOptions_EnablesOutOfOrderMetadata_PersistenceProfileAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    await Assert.That(options.AllowOutOfOrderMetadataProperties).IsTrue();
  }

  // ----------------------------------------------------------------------------------------------
  // Behavioral repro through Whizbang's REAL registry + CreateCombinedOptions: a nested polymorphic
  // IMessage with a short (≤4-char) direct property, key-reordered like jsonb, must still round-trip.
  // Pre-fix this throws NotSupportedException (the confirmed bug); post-fix it deserializes.
  // ----------------------------------------------------------------------------------------------

  [Test]
  public async Task NestedPolymorphic_ShortKey_JsonbReordered_RoundTripsThroughCombinedOptionsAsync() {
    JsonContextRegistry.RegisterDerivedType<IMessage, TestTinyKeyEvent>(nameof(TestTinyKeyEvent));
    JsonContextRegistry.RegisterContext(TinyKeyJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();

    var polyInfo = JsonContextRegistry.GetPolymorphicTypeInfo<IMessage>(options);
    await Assert.That(polyInfo).IsNotNull();

    var orderId = Guid.NewGuid();
    IMessage original = new TestTinyKeyEvent(42, orderId);

    // STJ writes $type first: {"$type":"TestTinyKeyEvent","X":42,"OrderId":"..."}
    var written = JsonSerializer.Serialize(original, polyInfo!);
    // Postgres jsonb reorders to put the 1-char "X" before the 5-char "$type".
    var jsonbNormalized = JsonbKeyOrderSimulator.Normalize(written);
    await Assert.That(jsonbNormalized.IndexOf("\"X\"", StringComparison.Ordinal))
      .IsLessThan(jsonbNormalized.IndexOf("\"$type\"", StringComparison.Ordinal));

    // Read it back through the same combined options.
    var back = JsonSerializer.Deserialize(jsonbNormalized, polyInfo!);
    await Assert.That(back).IsTypeOf<TestTinyKeyEvent>();
    await Assert.That(((TestTinyKeyEvent)back!).X).IsEqualTo(42);
    await Assert.That(((TestTinyKeyEvent)back).OrderId).IsEqualTo(orderId);
  }

  // ----------------------------------------------------------------------------------------------
  // Mechanism locks (pure STJ, no Whizbang): document exactly why it fails and why the flag fixes it.
  // These pass regardless of the library fix — they pin the STJ contract the fix relies on.
  // ----------------------------------------------------------------------------------------------

  [Test]
  public async Task Stj_OutOfOrderDiscriminator_FlagOff_ThrowsAsync() {
    var opts = _mechanismOptions(false);
    var reordered = """{"Object":{"A":1,"Id":"seed","$type":"ShapeDto","Shape":"Rect"}}""";
    await Assert.That(async () => JsonSerializer.Deserialize<MechHolder>(reordered, opts))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task Stj_OutOfOrderDiscriminator_FlagOn_RoundTripsAsync() {
    var opts = _mechanismOptions(true);
    var reordered = """{"Object":{"A":1,"Id":"seed","$type":"ShapeDto","Shape":"Rect"}}""";
    var holder = JsonSerializer.Deserialize<MechHolder>(reordered, opts);
    await Assert.That(holder!.Object).IsTypeOf<ShapeDto>();
    await Assert.That(((ShapeDto)holder.Object).Shape).IsEqualTo("Rect");
  }

  [Test]
  public async Task Stj_InOrderDiscriminator_FlagOff_UnaffectedAsync() {
    var opts = _mechanismOptions(false);
    var inOrder = """{"Object":{"$type":"ShapeDto","A":1,"Id":"seed","Shape":"Rect"}}""";
    var holder = JsonSerializer.Deserialize<MechHolder>(inOrder, opts);
    await Assert.That(holder!.Object).IsTypeOf<ShapeDto>();
  }

  // ----------------------------------------------------------------------------------------------
  // Simulator fidelity: validated against a real Postgres `SELECT '<payload>'::jsonb::text`.
  // ----------------------------------------------------------------------------------------------

  [Test]
  public async Task Simulator_MatchesRealPostgresJsonbOrderingAsync() {
    // Real Postgres output captured from `whizbang-test-postgres` for this exact input:
    //   {"A": {"X": 680, "Y": 544.996}, "Id": "seed-fanfare-2", "$type": "ShapeObjectDto", "Shape": "Rect"}
    var written = """{"$type":"ShapeObjectDto","Shape":"Rect","Id":"seed-fanfare-2","A":{"X":680,"Y":544.996}}""";
    var normalized = JsonbKeyOrderSimulator.Normalize(written);

    var obj = JsonNode.Parse(normalized)!.AsObject();
    var keyOrder = obj.Select(kv => kv.Key).ToArray();
    // Postgres orders by (length, then ordinal): A(1), Id(2), $type(5 — '$'<'S'), Shape(5).
    await Assert.That(keyOrder).IsEquivalentTo(_expectedShapeKeyOrder);
  }

  [Test]
  public async Task Simulator_FiveCharSibling_KeepsDiscriminatorFirstAsync() {
    // A 5-char sibling does NOT displace $type ('$'=0x24 < any identifier byte). This is why a
    // consumer (whose polymorphic leaves have all-long direct props) never triggers the bug.
    var written = """{"$type":"X","Shape":"Rect","Alpha":"a"}""";
    var normalized = JsonbKeyOrderSimulator.Normalize(written);
    await Assert.That(normalized.IndexOf("\"$type\"", StringComparison.Ordinal)).IsEqualTo(1); // first key
  }

  // ----------------------------------------------------------------------------------------------
  // Helpers + support types
  // ----------------------------------------------------------------------------------------------

  private static readonly string[] _expectedShapeKeyOrder = ["A", "Id", "$type", "Shape"];

  private static JsonSerializerOptions _mechanismOptions(bool _allowOutOfOrder) {
    var resolver = new DefaultJsonTypeInfoResolver();
    resolver.Modifiers.Add(static ti => {
      if (ti.Type == typeof(PictureDto)) {
        ti.PolymorphismOptions = new JsonPolymorphismOptions {
          TypeDiscriminatorPropertyName = "$type",
          UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor
        };
        ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(ShapeDto), "ShapeDto"));
      }
    });
    return new JsonSerializerOptions { TypeInfoResolver = resolver, AllowOutOfOrderMetadataProperties = _allowOutOfOrder };
  }

  internal sealed record TestTinyKeyEvent(int X, Guid OrderId) : IEvent;

  [JsonSerializable(typeof(TestTinyKeyEvent))]
  internal sealed partial class TinyKeyJsonContext : JsonSerializerContext { }

  private sealed record MechHolder(PictureDto Object);
  private abstract record PictureDto;
  private sealed record ShapeDto : PictureDto { public string Shape { get; init; } = ""; public int A { get; init; } public string? Id { get; init; } }
}

/// <summary>
/// Reorders JSON object keys the way a PostgreSQL <c>jsonb</c> column does: within every object, keys
/// are ordered by (UTF-8 byte length, then ordinal byte comparison), recursively, whitespace stripped.
/// Lets unit tests reproduce the reorder that a string round-trip cannot. Validated against a real
/// Postgres <c>::jsonb::text</c> in <see cref="JsonbPolymorphicOrderingTests.Simulator_MatchesRealPostgresJsonbOrderingAsync"/>.
/// </summary>
internal static class JsonbKeyOrderSimulator {
  public static string Normalize(string json) {
    var node = JsonNode.Parse(json);
    return _normalizeNode(node)?.ToJsonString() ?? "null";
  }

  private static JsonNode? _normalizeNode(JsonNode? _node) {
    switch (_node) {
      case JsonObject obj:
        var ordered = new JsonObject();
        foreach (var kvp in obj
            .OrderBy(k => Encoding.UTF8.GetByteCount(k.Key))
            .ThenBy(k => k.Key, StringComparer.Ordinal)) {
          ordered[kvp.Key] = _normalizeNode(kvp.Value?.DeepClone());
        }
        return ordered;
      case JsonArray arr:
        var list = new JsonArray();
        foreach (var item in arr) {
          list.Add(_normalizeNode(item?.DeepClone()));
        }
        return list;
      default:
        return _node?.DeepClone();
    }
  }
}
