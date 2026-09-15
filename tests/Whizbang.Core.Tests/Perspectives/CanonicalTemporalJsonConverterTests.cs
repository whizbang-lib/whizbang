using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>A perspective model holding one of every temporal shape.</summary>
internal sealed class TemporalWriterModel {
  public DateTime OccurredAt { get; init; }
  public DateTimeOffset RecordedAt { get; init; }
  public DateOnly Day { get; init; }
  public TimeOnly Clock { get; init; }
  public TimeSpan Elapsed { get; init; }
  public DateTime? MaybeAt { get; init; }
}

/// <summary>Every temporal shape, optional, plus one that is not temporal at all.</summary>
internal sealed class OptionalShapesModel {
  public DateTime? At { get; init; }
  public DateTimeOffset? Offset { get; init; }
  public DateOnly? Day { get; init; }
  public TimeOnly? Clock { get; init; }
  public TimeSpan? Elapsed { get; init; }
  public string Label { get; init; } = string.Empty;
}

/// <summary>Metadata for the optional-shapes model.</summary>
[JsonSerializable(typeof(OptionalShapesModel))]
internal sealed partial class OptionalShapesJsonContext : JsonSerializerContext;

/// <summary>
/// The model's metadata, source-generated the way a perspective's is.
/// </summary>
/// <remarks>
/// Needed for the same reason the upsert's atomic path needs it: without resolvable metadata the
/// serializer has nothing to write from, and the upsert falls back to the mapped path rather than
/// serializing at all. A test using an unresolvable model would exercise the fallback and prove
/// nothing about the writer.
/// </remarks>
[JsonSerializable(typeof(TemporalWriterModel))]
internal sealed partial class TemporalWriterJsonContext : JsonSerializerContext;

/// <summary>
/// That the serializer writes a perspective's dates, times and durations in the canonical form,
/// under the persistence profile, with nothing registered per model.
/// </summary>
/// <remarks>
/// <para>
/// A perspective document has two writers and only one of them is Entity Framework. A row is written
/// by the upsert, which serializes the model with System.Text.Json and sends the document as a
/// parameter; the mapping is what reads it back and what compiles a filter over it. Entity Framework
/// converts every temporal it maps by convention, so the serializer has to convert every temporal
/// it writes, or a row written by one is unreadable by the other.
/// </para>
/// <para>
/// So the converters are registered on the persistence profile's options, where the serializer
/// applies them wherever the type occurs: inherited members, nested objects, collection elements
/// and the framework's own metadata included. Nothing is discovered per model; there is nothing to
/// miss.
/// </para>
/// <para>
/// Scoped to the persistence profile deliberately. The default profile is transport and the event
/// store, where a date is part of a payload other systems and older releases read; nothing about
/// indexing a perspective asks for that to change, and changing it would be a wire-format break.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public class CanonicalTemporalJsonConverterTests {
  private static readonly DateTime _origin = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

  /// <summary>
  /// The options the upsert resolves, with the model's metadata combined in exactly as the upsert
  /// combines a caller's. Nothing is added for the model: the profile carries the conversion.
  /// </summary>
  private static JsonSerializerOptions _optionsFor(SerializationProfile profile, IJsonTypeInfoResolver model) {
    var union = JsonContextRegistry.CreateCombinedOptions(profile);
    return new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(union.TypeInfoResolver!, model),
    };
  }

  private static JsonSerializerOptions _writerOptions(SerializationProfile profile) =>
    _optionsFor(profile, TemporalWriterJsonContext.Default);

  private static TemporalWriterModel _model() => new() {
    OccurredAt = _origin,
    RecordedAt = new DateTimeOffset(_origin, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(330)),
    Day = new DateOnly(2026, 3, 4),
    Clock = new TimeOnly(5, 6, 7),
    Elapsed = TimeSpan.FromMinutes(3),
    MaybeAt = null,
  };

  /// <summary>
  /// Serializes through the same options the upsert resolves, so this exercises the writer rather
  /// than a converter in isolation.
  /// </summary>
  private static JsonElement _written() {
    var json = JsonSerializer.Serialize(_model(), _writerOptions(SerializationProfile.Persistence));
    return JsonDocument.Parse(json).RootElement;
  }

  /// <summary>Each temporal property is written as a number.</summary>
  [Test]
  [Arguments("OccurredAt")]
  [Arguments("RecordedAt")]
  [Arguments("Day")]
  [Arguments("Clock")]
  [Arguments("Elapsed")]
  public async Task ATemporalPropertyIsWrittenAsANumberAsync(string property) {
    var written = _written();

    await Assert.That(written.GetProperty(property).ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because($"'{property}' is written by the upsert and read by the mapping, and the mapping "
        + "expects a number; a rendering here makes every row written unreadable by the reader");
  }

  /// <summary>The number written is the one the format defines.</summary>
  /// <remarks>
  /// Asserted against the format rather than against a literal, because the writer, the reader and
  /// the backfill all have to produce the same value and only one definition of it can be right.
  /// </remarks>
  [Test]
  public async Task TheNumberIsTheOneTheFormatDefinesAsync() {
    var written = _written();

    await Assert.That(written.GetProperty("OccurredAt").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_origin));
    await Assert.That(written.GetProperty("Day").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(new DateOnly(2026, 3, 4)));
    await Assert.That(written.GetProperty("Clock").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToMicrosecondsOfDay(new TimeOnly(5, 6, 7)));
    await Assert.That(written.GetProperty("Elapsed").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToMicroseconds(TimeSpan.FromMinutes(3)));
  }

  /// <summary>
  /// An offset is written as the instant it names, whichever offset it was carrying.
  /// </summary>
  [Test]
  public async Task AnOffsetIsWrittenAsItsInstantAsync() {
    var written = _written();

    await Assert.That(written.GetProperty("RecordedAt").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(
        new DateTimeOffset(_origin, TimeSpan.Zero)))
      .Because("the model carried a five and a half hour offset, and one instant has to have exactly "
        + "one stored value or no query can produce them all");
  }

  /// <summary>A round trip through the writer's own options returns what went in.</summary>
  [Test]
  public async Task TheModelRoundTripsAsync() {
    var options = _writerOptions(SerializationProfile.Persistence);
    var json = JsonSerializer.Serialize(_model(), options);
    var restored = JsonSerializer.Deserialize<TemporalWriterModel>(json, options);

    await Assert.That(restored).IsNotNull();
    await Assert.That(restored!.OccurredAt).IsEqualTo(_origin);
    await Assert.That(restored.Day).IsEqualTo(new DateOnly(2026, 3, 4));
    await Assert.That(restored.Clock).IsEqualTo(new TimeOnly(5, 6, 7));
    await Assert.That(restored.Elapsed).IsEqualTo(TimeSpan.FromMinutes(3));
    await Assert.That(restored.RecordedAt)
      .IsEqualTo(new DateTimeOffset(_origin, TimeSpan.Zero));
  }

  /// <summary>An absent optional value is a null, not a number.</summary>
  /// <remarks>
  /// The serializer omits or nulls it before a converter is reached, which is the behavior an absent
  /// date wants. Asserted because a converter that produced a zero would store the epoch, and the
  /// epoch reads back as a real date rather than as nothing.
  /// </remarks>
  [Test]
  public async Task AnAbsentOptionalValueIsNotANumberAsync() {
    var written = _written();

    var present = written.TryGetProperty("MaybeAt", out var value);
    await Assert.That(!present || value.ValueKind == JsonValueKind.Null).IsTrue();
  }

  /// <summary>
  /// The framework's own document is converted like any other, because it is a document.
  /// </summary>
  /// <remarks>
  /// <c>PerspectiveMetadata.Timestamp</c> was once deliberately left as a rendering, because the
  /// mapping read it with no matching conversion and a number there was a row nothing could parse.
  /// The mapping now converts every temporal it maps, the framework's included, so the writer has
  /// to as well; a rendering here would be the same disagreement from the other side.
  /// </remarks>
  [Test]
  public async Task AFrameworkDocumentIsConvertedLikeAnyOtherAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var metadata = new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = _origin };

    var json = JsonSerializer.Serialize(metadata, options.GetTypeInfo(typeof(PerspectiveMetadata)));
    var written = JsonDocument.Parse(json).RootElement;

    await Assert.That(written.GetProperty("Timestamp").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_origin))
      .Because("the mapping converts the framework's timestamp with every other temporal it maps, so "
        + "the writer has to produce the number the mapping reads");
  }

  /// <summary>
  /// The transport and event-store profile is deliberately unchanged.
  /// </summary>
  /// <remarks>
  /// A date in a message payload is read by other systems and by older releases of this one. The
  /// perspective's stored form is an internal decision about a table this library owns; a payload's
  /// is not, and nothing about indexing a perspective asks for it to change. If this ever starts
  /// failing, the scope of the change grew past what was decided.
  /// </remarks>
  [Test]
  public async Task TheTransportProfileStillWritesARenderingAsync() {
    var json = JsonSerializer.Serialize(_model(), _writerOptions(SerializationProfile.Default));
    var written = JsonDocument.Parse(json).RootElement;

    foreach (var property in new[] { "OccurredAt", "RecordedAt", "Day", "Clock", "Elapsed" }) {
      await Assert.That(written.GetProperty(property).ValueKind).IsEqualTo(JsonValueKind.String)
        .Because($"'{property}' on the wire is read by systems and releases this one does not control, "
          + "so the canonical form is scoped to the documents this library owns");
    }
  }

  /// <summary>
  /// The framework's own document on the transport profile is a rendering too.
  /// </summary>
  [Test]
  public async Task AFrameworkDocumentOnTheTransportProfileIsARenderingAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Default);
    var metadata = new PerspectiveMetadata { EventType = "e", EventId = "1", Timestamp = _origin };

    var json = JsonSerializer.Serialize(metadata, options.GetTypeInfo(typeof(PerspectiveMetadata)));

    await Assert.That(JsonDocument.Parse(json).RootElement.GetProperty("Timestamp").ValueKind)
      .IsEqualTo(JsonValueKind.String);
  }

  /// <summary>
  /// An optional value that is present round-trips through the wrapper the serializer supplies.
  /// </summary>
  [Test]
  public async Task APresentOptionalValueRoundTripsAsync() {
    var options = _writerOptions(SerializationProfile.Persistence);
    var model = new TemporalWriterModel {
      OccurredAt = _origin,
      RecordedAt = new DateTimeOffset(_origin, TimeSpan.Zero),
      Day = new DateOnly(2026, 3, 4),
      Clock = new TimeOnly(5, 6, 7),
      Elapsed = TimeSpan.FromMinutes(3),
      MaybeAt = _origin.AddHours(2),
    };

    var json = JsonSerializer.Serialize(model, options);
    var written = JsonDocument.Parse(json).RootElement;

    await Assert.That(written.GetProperty("MaybeAt").ValueKind).IsEqualTo(JsonValueKind.Number);

    var restored = JsonSerializer.Deserialize<TemporalWriterModel>(json, options);
    await Assert.That(restored!.MaybeAt).IsEqualTo(_origin.AddHours(2));
  }

  /// <summary>
  /// An explicit JSON null reads back as absent rather than as the epoch.
  /// </summary>
  /// <remarks>
  /// A document written before the optional property existed, or by something that writes nulls
  /// rather than omitting them, still has to read. The serializer's own nullable wrapper handles the
  /// null before a converter is reached, and this is the assertion that it does.
  /// </remarks>
  [Test]
  public async Task AnExplicitNullReadsBackAsAbsentAsync() {
    var options = _writerOptions(SerializationProfile.Persistence);
    const string json = """
      {"OccurredAt": 0, "RecordedAt": 0, "Day": 0, "Clock": 0, "Elapsed": 0, "MaybeAt": null}
      """;

    var restored = JsonSerializer.Deserialize<TemporalWriterModel>(json, options);

    await Assert.That(restored).IsNotNull();
    await Assert.That(restored!.MaybeAt).IsNull()
      .Because("a null has to stay absent; handed to the underlying converter it would become the "
        + "epoch, which reads back as a real date rather than as nothing");
  }

  /// <summary>
  /// Every optional temporal shape is converted, and a property that is not temporal is not.
  /// </summary>
  /// <remarks>
  /// One shape per type rather than one test per type, because the thing being checked is that the
  /// profile carries a converter for every kind and that the serializer wraps each for its optional
  /// form: a shape it does not recognize is written as whatever it was, which for a date is a
  /// rendering the reader cannot parse.
  /// </remarks>
  [Test]
  public async Task EveryOptionalShapeIsConvertedAsync() {
    var options = _optionsFor(SerializationProfile.Persistence, OptionalShapesJsonContext.Default);
    var model = new OptionalShapesModel {
      At = _origin,
      Offset = new DateTimeOffset(_origin, TimeSpan.Zero),
      Day = new DateOnly(2026, 3, 4),
      Clock = new TimeOnly(5, 6, 7),
      Elapsed = TimeSpan.FromMinutes(3),
      Label = "kept",
    };

    var written = JsonDocument.Parse(JsonSerializer.Serialize(model, options)).RootElement;

    foreach (var key in new[] { "At", "Offset", "Day", "Clock", "Elapsed" }) {
      await Assert.That(written.GetProperty(key).ValueKind).IsEqualTo(JsonValueKind.Number)
        .Because($"'{key}' is temporal, so the profile has to carry a converter for its optional form "
          + "as well as its bare one");
    }

    await Assert.That(written.GetProperty("Label").ValueKind).IsEqualTo(JsonValueKind.String)
      .Because("a string is already in a form an index can reach, so converting it would be cost "
        + "without purpose");

    var restored = JsonSerializer.Deserialize<OptionalShapesModel>(
      JsonSerializer.Serialize(model, options), options);
    await Assert.That(restored!.Offset).IsEqualTo(new DateTimeOffset(_origin, TimeSpan.Zero));
    await Assert.That(restored.Day).IsEqualTo(new DateOnly(2026, 3, 4));
    await Assert.That(restored.Clock).IsEqualTo(new TimeOnly(5, 6, 7));
    await Assert.That(restored.Elapsed).IsEqualTo(TimeSpan.FromMinutes(3));
  }

  /// <summary>
  /// The persistence profile's converters are on the options, ahead of the transport profile's.
  /// </summary>
  /// <remarks>
  /// The serializer takes the first converter that handles a type. The transport profile registers a
  /// lenient <c>DateTimeOffset</c> reader that writes a rendering; if it reached the persistence
  /// profile at all, or reached it first, an offset would be written as text there and every other
  /// kind as a number. The ordering is the registration's priority, and this pins it.
  /// </remarks>
  [Test]
  public async Task ThePersistenceProfileCarriesTheCanonicalConvertersFirstAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var names = options.Converters.Select(c => c.GetType().Name).ToList();

    string[] canonical = [
      nameof(CanonicalTemporalJsonConverters.InstantConverter),
      nameof(CanonicalTemporalJsonConverters.OffsetInstantConverter),
      nameof(CanonicalTemporalJsonConverters.DayConverter),
      nameof(CanonicalTemporalJsonConverters.TimeOfDayConverter),
      nameof(CanonicalTemporalJsonConverters.DurationConverter),
    ];
    foreach (var name in canonical) {
      await Assert.That(names).Contains(name);
    }
    await Assert.That(names).DoesNotContain(nameof(LenientDateTimeOffsetConverter))
      .Because("the lenient reader is the transport profile's; on this profile the canonical one "
        + "answers for an offset, renderings included");
    await Assert.That(names).DoesNotContain(nameof(LenientNullableDateTimeOffsetConverter));
  }
}
