using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

/// <summary>A model used only to prove the registry seam, so registering for it is harmless.</summary>
internal sealed class RegistryProbeModel {
  public DateTime At { get; init; }
}

/// <summary>Metadata for the registry probe.</summary>
[JsonSerializable(typeof(RegistryProbeModel))]
internal sealed partial class RegistryProbeJsonContext : JsonSerializerContext;

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
/// That the serializer writes a perspective's dates, times and durations in the canonical form.
/// </summary>
/// <remarks>
/// <para>
/// A perspective document has two writers and only one of them is Entity Framework. A row is written
/// by the upsert, which serializes the model with System.Text.Json and sends the document as a
/// parameter; the mapping is what reads it back and what compiles a filter over it. A value
/// conversion attaches to the mapping, so on its own it changes the reading and not the writing.
/// </para>
/// <para>
/// If the two disagree the failure is total rather than partial: the writer produces a rendering,
/// the reader expects a number, and every row written after the change is unreadable by the code
/// meant to read it. These are the assertions on the writer's half.
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
  /// combines a caller's.
  /// </summary>
  private static JsonSerializerOptions _optionsFor(SerializationProfile profile) {
    var union = JsonContextRegistry.CreateCombinedOptions(profile);
    var resolver = JsonTypeInfoResolver.Combine(
      union.TypeInfoResolver!, TemporalWriterJsonContext.Default);

    // What the generator emits for a perspective: the canonical form applied to this model's own
    // temporal properties and to nothing else. Applied here rather than registered globally, because
    // a global registration reaches every date in every document — including the framework's own
    // PerspectiveMetadata.Timestamp, which is mapped and read with no matching conversion and became
    // unreadable the moment it was written as a number.
    if (profile == SerializationProfile.Persistence) {
      resolver = resolver.WithAddedModifier(info =>
        CanonicalTemporalJsonConverters.ApplyTo(
          info, typeof(TemporalWriterModel),
          "OccurredAt", "RecordedAt", "Day", "Clock", "Elapsed", "MaybeAt"));
    }

    return new JsonSerializerOptions(union) { TypeInfoResolver = resolver };
  }

  /// <summary>
  /// The probe's options, built here rather than in each test so the options are created once per
  /// call site rather than inline beside the serialization.
  /// </summary>
  private static JsonSerializerOptions _probeOptionsFor(SerializationProfile profile) {
    var union = JsonContextRegistry.CreateCombinedOptions(profile);

    // Mirrors what the upsert does with a caller's options: combine the union with another resolver,
    // then wrap the finished chain so the modifiers reach what that resolver answers for.
    var chain = JsonTypeInfoResolver.Combine(
      union.TypeInfoResolver!, RegistryProbeJsonContext.Default);

    return new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonContextRegistry.WithRegisteredModifiers(chain, profile),
    };
  }

  /// <summary>Options carrying the conversion for every optional shape on the probe model.</summary>
  private static JsonSerializerOptions _optionalShapesOptions() {
    var union = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var chain = JsonTypeInfoResolver.Combine(
      union.TypeInfoResolver!, OptionalShapesJsonContext.Default);

    return new JsonSerializerOptions(union) {
      TypeInfoResolver = chain.WithAddedModifier(info =>
        CanonicalTemporalJsonConverters.ApplyTo(
          info, typeof(OptionalShapesModel), "At", "Offset", "Day", "Clock", "Elapsed", "Label")),
    };
  }

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
    var json = JsonSerializer.Serialize(_model(), _optionsFor(SerializationProfile.Persistence));
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
      .Because($"'{property}' is written by the upsert and read by the mapping, and the mapping now "
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
    await Assert.That(written.GetProperty("Day").GetInt32())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochDays(new DateOnly(2026, 3, 4)));
    await Assert.That(written.GetProperty("Clock").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToMicrosecondsOfDay(new TimeOnly(5, 6, 7)));
    await Assert.That(written.GetProperty("Elapsed").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToTicks(TimeSpan.FromMinutes(3)));
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
    var options = _optionsFor(SerializationProfile.Persistence);
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
  /// A date on a framework document is not converted, which is the failure this shape prevents.
  /// </summary>
  /// <remarks>
  /// <c>PerspectiveMetadata.Timestamp</c> is mapped by the framework with no matching conversion, so
  /// a canonical number written there is a row the reader cannot parse. A converter registered on the
  /// options reached it; one applied to a named model's named properties does not. This is the test
  /// that would have caught that, and it did not exist when the converter was registered globally.
  /// </remarks>
  [Test]
  public async Task AFrameworkDocumentIsNotConvertedAsync() {
    var options = _optionsFor(SerializationProfile.Persistence);
    var metadata = new Whizbang.Core.Lenses.PerspectiveMetadata { Timestamp = _origin };

    var json = JsonSerializer.Serialize(metadata, options);
    var written = JsonDocument.Parse(json).RootElement;

    await Assert.That(written.GetProperty("Timestamp").ValueKind).IsEqualTo(JsonValueKind.String)
      .Because("the framework maps and reads this document with no matching conversion, so a number "
        + "written here is a row nothing can parse");
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
    var json = JsonSerializer.Serialize(_model(), _optionsFor(SerializationProfile.Default));
    var written = JsonDocument.Parse(json).RootElement;

    await Assert.That(written.GetProperty("OccurredAt").ValueKind).IsEqualTo(JsonValueKind.String)
      .Because("a date on the wire is read by systems and releases this one does not control, so "
        + "the canonical form is scoped to the documents this library owns");
  }

  /// <summary>
  /// A modifier registered through the registry reaches the options the writer resolves.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the seam the generated code actually uses. The generator emits a
  /// <c>RegisterTypeInfoModifier</c> call, and the generator test asserts that the call is emitted;
  /// neither says the registry applies what it was handed. Without this, the text could be perfect
  /// and the conversion never happen.
  /// </para>
  /// <para>
  /// The modifier names a type declared only for this test, which is what makes registering one
  /// process-wide harmless: it returns immediately for every other type the serializer resolves,
  /// exactly as a real perspective's does.
  /// </para>
  /// </remarks>
  [Test]
  public async Task AModifierRegisteredThroughTheRegistryIsAppliedAsync() {
    JsonContextRegistry.RegisterTypeInfoModifier(
      info => CanonicalTemporalJsonConverters.ApplyTo(info, typeof(RegistryProbeModel), "At"),
      SerializationProfile.Persistence);

    var options = _probeOptionsFor(SerializationProfile.Persistence);
    var json = JsonSerializer.Serialize(new RegistryProbeModel { At = _origin }, options);
    var written = JsonDocument.Parse(json).RootElement;

    await Assert.That(written.GetProperty("At").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("the generated code registers its conversion this way, so a registry that did not "
        + "apply what it was handed would leave every emitted call inert");
    await Assert.That(written.GetProperty("At").GetInt64())
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(_origin));
  }

  /// <summary>
  /// A modifier scoped to one profile does not reach another.
  /// </summary>
  /// <remarks>
  /// The scoping is what keeps the perspective's stored form out of the transport payload, so it is
  /// worth proving rather than trusting: registered for the wrong profile, this change would be a
  /// wire-format break rather than a storage decision.
  /// </remarks>
  [Test]
  public async Task AModifierDoesNotReachAnotherProfileAsync() {
    JsonContextRegistry.RegisterTypeInfoModifier(
      info => CanonicalTemporalJsonConverters.ApplyTo(info, typeof(RegistryProbeModel), "At"),
      SerializationProfile.Persistence);

    var options = _probeOptionsFor(SerializationProfile.Default);
    var json = JsonSerializer.Serialize(new RegistryProbeModel { At = _origin }, options);
    var written = JsonDocument.Parse(json).RootElement;

    await Assert.That(written.GetProperty("At").ValueKind).IsEqualTo(JsonValueKind.String)
      .Because("a date on the wire is read by systems this release does not control, so a modifier "
        + "scoped to persistence has to stay out of the transport profile");
  }

  /// <summary>
  /// An optional value that is present round-trips through the wrapper that handles it.
  /// </summary>
  /// <remarks>
  /// The model used elsewhere in this file leaves the optional property null, and a null is omitted
  /// before a converter is reached, so nothing was exercising the wrapper's read path. A value that
  /// is present is the case a model actually stores.
  /// </remarks>
  [Test]
  public async Task APresentOptionalValueRoundTripsAsync() {
    var options = _optionsFor(SerializationProfile.Persistence);
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
  /// rather than omitting them, still has to read. A wrapper without its null branch would hand the
  /// underlying converter a null token and get the epoch, which reads back as a real date.
  /// </remarks>
  [Test]
  public async Task AnExplicitNullReadsBackAsAbsentAsync() {
    var options = _optionsFor(SerializationProfile.Persistence);
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
  /// One shape per type rather than one test per type, because the thing being checked is the lookup
  /// that picks a converter: a shape it does not recognize falls through and the property is written
  /// as whatever it was, which for a date is a rendering the reader cannot parse.
  /// </remarks>
  [Test]
  public async Task EveryOptionalShapeIsConvertedAsync() {
    var options = _optionalShapesOptions();
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
        .Because($"'{key}' is temporal, so the lookup has to find a converter for its optional form "
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
  /// The optional wrapper writes a null as a null.
  /// </summary>
  /// <remarks>
  /// Reached directly because the serializer omits a null property before a converter sees it, so
  /// this branch cannot be exercised through a model. It still has to be right: a document written
  /// by something that emits nulls rather than omitting them goes through here, and a wrapper that
  /// handed the null to the underlying converter would write the epoch.
  /// </remarks>
  [Test]
  public async Task TheOptionalWrapperWritesANullAsANullAsync() {
    var converter = new CanonicalTemporalJsonConverters.NullableConverter<DateTime>(
      new CanonicalTemporalJsonConverters.InstantConverter());

    await using var buffer = new MemoryStream();
    await using (var writer = new Utf8JsonWriter(buffer)) {
      converter.Write(writer, null, JsonSerializerOptions.Default);
    }

    await Assert.That(System.Text.Encoding.UTF8.GetString(buffer.ToArray())).IsEqualTo("null");
  }

  /// <summary>
  /// A temporal property the conversion was not told about is left alone.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the guarantee the per-property shape exists for. Applied per type, the conversion
  /// reached every date everywhere and made framework documents unreadable; applied per property, it
  /// has to touch the names it was given and nothing else, <em>including</em> other temporal
  /// properties on the very same model.
  /// </para>
  /// <para>
  /// That case is not hypothetical. A model can hold a date the generator deliberately did not
  /// convert, a nested one being the example, and converting it anyway would store a number where
  /// the reader expects a rendering.
  /// </para>
  /// </remarks>
  [Test]
  public async Task ATemporalPropertyNotNamedIsLeftAloneAsync() {
    var union = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var options = _partiallyNamedOptions(union);

    var model = new OptionalShapesModel { At = _origin, Offset = new DateTimeOffset(_origin, TimeSpan.Zero) };
    var written = JsonDocument.Parse(JsonSerializer.Serialize(model, options)).RootElement;

    await Assert.That(written.GetProperty("At").ValueKind).IsEqualTo(JsonValueKind.Number)
      .Because("'At' was named, so it is converted");
    await Assert.That(written.GetProperty("Offset").ValueKind).IsEqualTo(JsonValueKind.String)
      .Because("'Offset' is temporal and was NOT named, so it keeps the form the serializer would "
        + "have given it; converting it anyway is the per-type mistake in miniature");
  }

  /// <summary>Options naming only one of the model's temporal properties.</summary>
  private static JsonSerializerOptions _partiallyNamedOptions(JsonSerializerOptions union) {
    var chain = JsonTypeInfoResolver.Combine(
      union.TypeInfoResolver!, OptionalShapesJsonContext.Default);

    return new JsonSerializerOptions(union) {
      TypeInfoResolver = chain.WithAddedModifier(info =>
        CanonicalTemporalJsonConverters.ApplyTo(info, typeof(OptionalShapesModel), "At")),
    };
  }
}
