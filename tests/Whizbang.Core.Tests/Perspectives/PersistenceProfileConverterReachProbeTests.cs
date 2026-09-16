using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>A temporal declared on a base class, which a member walk of the model misses.</summary>
internal abstract class ReachAudited {
  public DateTimeOffset RecordedAt { get; init; }
}

/// <summary>A positional record, so its members arrive through constructor parameters.</summary>
internal sealed record ReachOccurrence(Guid OccurrenceId, DateOnly Day, TimeSpan Length);

/// <summary>A nested object holding a temporal and an optional one.</summary>
internal sealed class ReachWindow {
  public TimeOnly Opens { get; init; }
  public DateTime? MaybeAt { get; init; }
}

/// <summary>One of every placement: top level, inherited, nested, in a collection of records.</summary>
internal sealed class ReachModel : ReachAudited {
  public DateTime OccurredAt { get; init; }
  public ReachWindow Window { get; init; } = new();
  public List<ReachOccurrence> Occurrences { get; init; } = [];
}

/// <summary>The model's metadata, source-generated the way a perspective's is.</summary>
[JsonSerializable(typeof(ReachModel))]
internal sealed partial class ReachJsonContext : JsonSerializerContext;

/// <summary>
/// Whether a converter placed on the options of the persistence profile reaches every temporal in a
/// document that source-generated metadata describes, with nothing discovered per model.
/// </summary>
/// <remarks>
/// <para>
/// A per-model modifier converts the properties a generator named and nothing else: not a member
/// inherited from a base class, not a nested object, not an element of a collection, not the
/// framework's own metadata. An options converter is applied by the serializer wherever the type
/// occurs. Whether that holds through source-generated contexts, and through a positional record's
/// constructor, is the serializer's business and is measured here rather than assumed.
/// </para>
/// <para>
/// The options are built exactly as the upsert builds them: the profile's union, combined with the
/// model's own metadata as a caller's would be, and the converters added to the options as
/// registration on the profile adds them.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public class PersistenceProfileConverterReachProbeTests {
  private static readonly DateTime _occurredAt = new(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);

  private static JsonSerializerOptions _options() {
    var union = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var options = new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(union.TypeInfoResolver!, ReachJsonContext.Default),
    };
    // At the front, not the end. The serializer takes the first converter that handles a type, and
    // the union already carries a profile-wide DateTimeOffset converter that writes a rendering; a
    // registration on the profile wins by priority, which in the options is position.
    options.Converters.Insert(0, new CanonicalTemporalJsonConverters.InstantConverter());
    options.Converters.Insert(0, new CanonicalTemporalJsonConverters.OffsetInstantConverter());
    options.Converters.Insert(0, new CanonicalTemporalJsonConverters.DayConverter());
    options.Converters.Insert(0, new CanonicalTemporalJsonConverters.TimeOfDayConverter());
    options.Converters.Insert(0, new CanonicalTemporalJsonConverters.DurationConverter());
    return options;
  }

  private static ReachModel _model() => new() {
    OccurredAt = _occurredAt,
    RecordedAt = new DateTimeOffset(_occurredAt).AddHours(1),
    Window = new ReachWindow { Opens = new TimeOnly(9, 30), MaybeAt = _occurredAt.AddDays(1) },
    Occurrences = [new ReachOccurrence(Guid.CreateVersion7(), new DateOnly(2026, 3, 5), TimeSpan.FromMinutes(90))],
  };

  /// <summary>Every placement in the model is written as a number.</summary>
  [Test]
  public async Task AnOptionsConverterReachesEveryPlacementInTheModelAsync() {
    var options = _options();
    var info = (JsonTypeInfo<ReachModel>)options.GetTypeInfo(typeof(ReachModel));

    var json = JsonSerializer.Serialize(_model(), info);
    var doc = JsonDocument.Parse(json).RootElement;

    await Assert.That(doc.GetProperty("OccurredAt").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(doc.GetProperty("RecordedAt").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("an inherited member is a member; the serializer does not know it was inherited");
    await Assert.That(doc.GetProperty("Window").GetProperty("Opens").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(doc.GetProperty("Window").GetProperty("MaybeAt").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("a nullable temporal with a value is wrapped by the serializer around the same converter");
    var occurrence = doc.GetProperty("Occurrences")[0];
    await Assert.That(occurrence.GetProperty("Day").ValueKind).IsEqualTo(JsonValueKind.Number);
    await Assert.That(occurrence.GetProperty("Length").ValueKind).IsEqualTo(JsonValueKind.Number);
  }

  /// <summary>The framework's own metadata, resolved from the profile's union, is written the same way.</summary>
  [Test]
  public async Task AnOptionsConverterReachesTheFrameworkMetadataAsync() {
    var options = _options();
    var info = (JsonTypeInfo<PerspectiveMetadata>)options.GetTypeInfo(typeof(PerspectiveMetadata));

    var json = JsonSerializer.Serialize(
      new PerspectiveMetadata { EventType = "probe", EventId = "1", Timestamp = _occurredAt }, info);
    var doc = JsonDocument.Parse(json).RootElement;

    await Assert.That(doc.GetProperty("Timestamp").ValueKind).IsEqualTo(JsonValueKind.Number);
  }

  /// <summary>
  /// The document reads back equal, including through the positional record's constructor.
  /// </summary>
  /// <remarks>
  /// The constructor path is the one the mapped path cannot bind and the one the opaque form relies
  /// on. A converter that reached the writer but not the parameter binder would write a document
  /// the serializer itself could not read.
  /// </remarks>
  [Test]
  public async Task TheDocumentRoundTripsThroughTheRecordConstructorAsync() {
    var options = _options();
    var info = (JsonTypeInfo<ReachModel>)options.GetTypeInfo(typeof(ReachModel));
    var original = _model();

    var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, info), info);

    await Assert.That(back).IsNotNull();
    await Assert.That(back!.OccurredAt).IsEqualTo(original.OccurredAt);
    await Assert.That(back.RecordedAt).IsEqualTo(original.RecordedAt);
    await Assert.That(back.Window.Opens).IsEqualTo(original.Window.Opens);
    await Assert.That(back.Window.MaybeAt).IsEqualTo(original.Window.MaybeAt);
    await Assert.That(back.Occurrences[0].Day).IsEqualTo(original.Occurrences[0].Day);
    await Assert.That(back.Occurrences[0].Length).IsEqualTo(original.Occurrences[0].Length);
  }
}
