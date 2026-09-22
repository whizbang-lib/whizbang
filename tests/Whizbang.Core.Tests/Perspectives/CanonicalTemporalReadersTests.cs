using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// That one reader per kind serves every path that reads a stored temporal.
/// </summary>
/// <remarks>
/// <para>
/// A stored document is read two ways: System.Text.Json reads an opaque document through the
/// persistence profile's converters, and Entity Framework reads a mapped document through its own
/// JSON reader, property by property. Before this, only the first way tolerated a rendering, and
/// only the first way refused an unexpected token with a <see cref="JsonException"/> that named the
/// type, the forms accepted and the token found; the second failed with the reader's own generic
/// error, which nothing could classify. Both now call the same reader, so whatever one path reads
/// the other reads, and whatever one refuses the other refuses in the same words.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/CanonicalTemporalReaders.cs</code-under-test>
[Category("Core")]
[Category("Perspectives")]
public class CanonicalTemporalReadersTests {
  private static readonly DateTime _instant = new(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);

  private delegate T Reader<out T>(ref Utf8JsonReader reader);

  private static T _read<T>(Reader<T> read, string json) {
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();
    return read(ref reader);
  }

  private static JsonException? _readError<T>(Reader<T> read, string json) {
    try {
      _read(read, json);
      return null;
    } catch (JsonException ex) {
      return ex;
    }
  }

  /// <summary>A number is the canonical unit for every kind.</summary>
  [Test]
  public async Task ANumberIsReadInTheCanonicalUnitAsync() {
    var micros = CanonicalTemporalFormat.ToEpochMicroseconds(_instant);

    await Assert.That(_read(CanonicalTemporalReaders.Instant, $"{micros}")).IsEqualTo(_instant);
    await Assert.That(_read(CanonicalTemporalReaders.OffsetInstant, $"{micros}"))
      .IsEqualTo(new DateTimeOffset(_instant));
    await Assert.That(_read(CanonicalTemporalReaders.Day,
        $"{CanonicalTemporalFormat.ToEpochMicroseconds(new DateOnly(2026, 3, 4))}"))
      .IsEqualTo(new DateOnly(2026, 3, 4));
    await Assert.That(_read(CanonicalTemporalReaders.TimeOfDay, "18367000000"))
      .IsEqualTo(new TimeOnly(5, 6, 7));
    await Assert.That(_read(CanonicalTemporalReaders.Duration, "180000000"))
      .IsEqualTo(TimeSpan.FromMinutes(3));
  }

  /// <summary>A rendering is read as the value it renders, for every kind.</summary>
  [Test]
  public async Task ARenderingIsReadAsTheValueItRendersAsync() {
    await Assert.That(_read(CanonicalTemporalReaders.Instant, "\"2026-03-04T05:06:07.89Z\"")).IsEqualTo(_instant);
    await Assert.That(_read(CanonicalTemporalReaders.OffsetInstant, "\"2026-03-04T10:36:07.89+05:30\""))
      .IsEqualTo(new DateTimeOffset(_instant));
    await Assert.That(_read(CanonicalTemporalReaders.Day, "\"2026-03-04\"")).IsEqualTo(new DateOnly(2026, 3, 4));
    await Assert.That(_read(CanonicalTemporalReaders.TimeOfDay, "\"05:06:07\"")).IsEqualTo(new TimeOnly(5, 6, 7));
    await Assert.That(_read(CanonicalTemporalReaders.Duration, "\"00:03:00\"")).IsEqualTo(TimeSpan.FromMinutes(3));
  }

  /// <summary>
  /// Any other token is refused with the type, the forms accepted and the token found, so a
  /// failure downstream can be classified by its type and read by an operator.
  /// </summary>
  [Test]
  [Arguments("true", "True")]
  [Arguments("null", "Null")]
  [Arguments("{}", "StartObject")]
  [Arguments("[]", "StartArray")]
  public async Task AnotherTokenIsRefusedWithTheFormsAcceptedAsync(string json, string token) {
    var instant = _readError(CanonicalTemporalReaders.Instant, json);
    var offset = _readError(CanonicalTemporalReaders.OffsetInstant, json);
    var day = _readError(CanonicalTemporalReaders.Day, json);
    var timeOfDay = _readError(CanonicalTemporalReaders.TimeOfDay, json);
    var duration = _readError(CanonicalTemporalReaders.Duration, json);

    await Assert.That(instant?.Message).IsEqualTo(
      $"A stored DateTime must be a number (microseconds) or a rendering, but the document holds {token}");
    await Assert.That(offset?.Message).Contains("DateTimeOffset", StringComparison.Ordinal);
    await Assert.That(day?.Message).Contains("DateOnly", StringComparison.Ordinal);
    await Assert.That(timeOfDay?.Message).Contains("TimeOnly", StringComparison.Ordinal);
    await Assert.That(duration?.Message).Contains("TimeSpan", StringComparison.Ordinal);
  }

  /// <summary>The serializer's converters are the readers, not a second copy of them.</summary>
  [Test]
  public async Task TheConvertersReadThroughTheSameReadersAsync() {
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes("\"2026-03-04T05:06:07.89Z\""));
    reader.Read();

    var viaConverter = new CanonicalTemporalJsonConverters.InstantConverter()
      .Read(ref reader, typeof(DateTime), JsonSerializerOptions.Default);

    await Assert.That(viaConverter).IsEqualTo(_read(CanonicalTemporalReaders.Instant, "\"2026-03-04T05:06:07.89Z\""));
  }
}
