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
internal sealed partial class TemporalWriterJsonContext : JsonSerializerContext { }

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
    return new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        union.TypeInfoResolver!, TemporalWriterJsonContext.Default),
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
}
