using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// What a canonical reader does with a stored value that is not the canonical number.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite that turns a rendering into a number is SQL, and it is complete for everything the
/// mapping walks. A reader that also accepts a rendering exists for the path the rewrite has not
/// reached yet: a document written by an older release that the extended discovery still misses, or
/// a row written during a rollout. Without it that row stops a feature until the next release, which
/// is the failure this whole design comes from.
/// </para>
/// <para>
/// That tolerance is scoped and measured. A number is always read in the canonical unit: a reader
/// never guesses whether a number is a day count, a tick count or a microsecond count, because that
/// question is answered by the stored form ledger before any reader sees the row. A rendering is
/// counted and announced, once per kind, so a non-zero count is a rewrite bug with a kind attached
/// rather than something absorbed. And anything else is refused with a message that says what was
/// found and what was expected.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public class CanonicalTemporalReaderToleranceTests {
  private static readonly DateTime _instant = new(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);

  /// <summary>Reads one JSON value through a converter, as the serializer would.</summary>
  private static T? _read<T>(JsonConverter<T> converter, string json) {
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    reader.Read();
    return converter.Read(ref reader, typeof(T), JsonSerializerOptions.Default);
  }

  private static JsonException? _readError<T>(JsonConverter<T> converter, string json) {
    try {
      _read(converter, json);
      return null;
    } catch (JsonException ex) {
      return ex;
    }
  }

  /// <summary>A number is the canonical unit, for every kind.</summary>
  [Test]
  public async Task ANumberIsReadInTheCanonicalUnitAsync() {
    var micros = CanonicalTemporalFormat.ToEpochMicroseconds(_instant);

    await Assert.That(_read(new CanonicalTemporalJsonConverters.InstantConverter(), $"{micros}"))
      .IsEqualTo(_instant);
    await Assert.That(_read(new CanonicalTemporalJsonConverters.OffsetInstantConverter(), $"{micros}"))
      .IsEqualTo(new DateTimeOffset(_instant));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.DayConverter(),
        $"{CanonicalTemporalFormat.ToEpochMicroseconds(new DateOnly(2026, 3, 4))}"))
      .IsEqualTo(new DateOnly(2026, 3, 4));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.TimeOfDayConverter(), "18367000000"))
      .IsEqualTo(new TimeOnly(5, 6, 7));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.DurationConverter(), "180000000"))
      .IsEqualTo(TimeSpan.FromMinutes(3))
      .Because("a duration is microseconds now, not ticks; a reader that took ticks here would be "
        + "ten times short and nothing would say so");
  }

  /// <summary>A rendering is read as the value it renders, for every kind.</summary>
  [Test]
  public async Task ARenderingIsReadAsTheValueItRendersAsync() {
    await Assert.That(_read(new CanonicalTemporalJsonConverters.InstantConverter(), "\"2026-03-04T05:06:07.89Z\""))
      .IsEqualTo(_instant);
    await Assert.That(_read(new CanonicalTemporalJsonConverters.InstantConverter(), "\"2026-03-04T05:06:07.89\"").Kind)
      .IsEqualTo(DateTimeKind.Utc)
      .Because("a rendering with no zone is an instant written without one, and the stored form is UTC");
    await Assert.That(_read(new CanonicalTemporalJsonConverters.OffsetInstantConverter(), "\"2026-03-04T10:36:07.89+05:30\""))
      .IsEqualTo(new DateTimeOffset(_instant));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.OffsetInstantConverter(), "\"2026-03-04T05:06:07.89\""))
      .IsEqualTo(new DateTimeOffset(_instant));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.DayConverter(), "\"2026-03-04\""))
      .IsEqualTo(new DateOnly(2026, 3, 4));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.TimeOfDayConverter(), "\"05:06:07.1234567\""))
      .IsEqualTo(new TimeOnly(5, 6, 7).Add(TimeSpan.FromTicks(1234567)));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.DurationConverter(), "\"2.05:06:07.1230000\""))
      .IsEqualTo(new TimeSpan(2, 5, 6, 7, 123));
  }

  /// <summary>The words the database uses for its extremes read as the extremes.</summary>
  [Test]
  public async Task TheRenderedExtremesReadAsTheExtremesAsync() {
    await Assert.That(_read(new CanonicalTemporalJsonConverters.InstantConverter(), "\"infinity\""))
      .IsEqualTo(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.InstantConverter(), "\"-infinity\""))
      .IsEqualTo(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.OffsetInstantConverter(), "\"infinity\""))
      .IsEqualTo(DateTimeOffset.MaxValue);
    await Assert.That(_read(new CanonicalTemporalJsonConverters.OffsetInstantConverter(), "\"-infinity\""))
      .IsEqualTo(DateTimeOffset.MinValue);
  }

  /// <summary>An empty rendering is the default value, as the transport reader has always read it.</summary>
  [Test]
  public async Task AnEmptyRenderingIsTheDefaultAsync() {
    await Assert.That(_read(new CanonicalTemporalJsonConverters.InstantConverter(), "\"\"")).IsEqualTo(default(DateTime));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.OffsetInstantConverter(), "\"\"")).IsEqualTo(default(DateTimeOffset));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.DayConverter(), "\"\"")).IsEqualTo(default(DateOnly));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.TimeOfDayConverter(), "\"\"")).IsEqualTo(default(TimeOnly));
    await Assert.That(_read(new CanonicalTemporalJsonConverters.DurationConverter(), "\"\"")).IsEqualTo(default(TimeSpan));
  }

  /// <summary>A rendering that is not one is refused, and the message says what was found.</summary>
  [Test]
  public async Task ARenderingThatDoesNotParseIsRefusedAsync() {
    var error = _readError(new CanonicalTemporalJsonConverters.InstantConverter(), "\"not a date\"");

    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Message).Contains("not a date");
    await Assert.That(error.Message).Contains("DateTime");
    await Assert.That(_readError(new CanonicalTemporalJsonConverters.DayConverter(), "\"someday\"")).IsNotNull();
    await Assert.That(_readError(new CanonicalTemporalJsonConverters.TimeOfDayConverter(), "\"noon\"")).IsNotNull();
    await Assert.That(_readError(new CanonicalTemporalJsonConverters.DurationConverter(), "\"a while\"")).IsNotNull();
    await Assert.That(_readError(new CanonicalTemporalJsonConverters.OffsetInstantConverter(), "\"whenever\"")).IsNotNull();
  }

  /// <summary>
  /// Any other token is refused with a message naming the type, the token and the two forms accepted.
  /// </summary>
  /// <remarks>
  /// The message is the operator's whole diagnosis. The serializer appends the path; the converter has
  /// to say what it found and what it would have taken, or the path is all there is.
  /// </remarks>
  [Test]
  [Arguments("true", "True")]
  [Arguments("{}", "StartObject")]
  [Arguments("[]", "StartArray")]
  public async Task AnotherTokenIsRefusedWithTheFormsAcceptedAsync(string json, string token) {
    var error = _readError(new CanonicalTemporalJsonConverters.DurationConverter(), json);

    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Message).Contains("TimeSpan");
    await Assert.That(error.Message).Contains("microseconds");
    await Assert.That(error.Message).Contains("rendering");
    await Assert.That(error.Message).Contains(token)
      .Because("naming the token found is what makes the message a diagnosis rather than a complaint");
  }

  /// <summary>
  /// Reading a rendering is counted, tagged by kind, on the perspectives meter.
  /// </summary>
  /// <remarks>
  /// This is the evidence that decides whether the tolerance can be removed: a count of zero across a
  /// release cycle means the rewrite reached everything, and a count above zero names which kind it
  /// did not reach.
  /// </remarks>
  [Test]
  [NotInParallel]
  public async Task ReadingARenderingIsCountedByKindAsync() {
    var measurements = new List<(long Value, string? Kind)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == CanonicalTemporalFallbacks.METER_NAME
          && instrument.Name == CanonicalTemporalFallbacks.INSTRUMENT_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? kind = null;
      foreach (var tag in tags) {
        if (tag.Key == "kind") {
          kind = tag.Value?.ToString();
        }
      }
      measurements.Add((value, kind));
    });
    listener.Start();
    CanonicalTemporalFallbacks.Configure(meterFactory: null, logger: null);

    _read(new CanonicalTemporalJsonConverters.InstantConverter(), "\"2026-03-04T05:06:07Z\"");
    _read(new CanonicalTemporalJsonConverters.DurationConverter(), "\"00:03:00\"");
    _read(new CanonicalTemporalJsonConverters.DurationConverter(), "180000000");

    await Assert.That(measurements.Count).IsEqualTo(2)
      .Because("two renderings were read and one number, and only a rendering is a fallback");
    await Assert.That(measurements[0].Kind).IsEqualTo(nameof(StoredTemporalKind.Instant));
    await Assert.That(measurements[1].Kind).IsEqualTo(nameof(StoredTemporalKind.Duration));
    await Assert.That(measurements.All(m => m.Value == 1)).IsTrue();
  }

  /// <summary>
  /// The first rendering of each kind is announced at Warning, and later ones are not.
  /// </summary>
  /// <remarks>
  /// Once, because the count is on the meter and a log line per row would be a log storm on exactly
  /// the deployment that most needs to read its logs. The one line says what to look at.
  /// </remarks>
  [Test]
  [NotInParallel]
  public async Task TheFirstRenderingOfEachKindIsAnnouncedOnceAsync() {
    var logger = new FakeLogger();
    CanonicalTemporalFallbacks.Configure(meterFactory: null, logger: logger);

    _read(new CanonicalTemporalJsonConverters.InstantConverter(), "\"2026-03-04T05:06:07Z\"");
    _read(new CanonicalTemporalJsonConverters.InstantConverter(), "\"2026-03-05T05:06:07Z\"");
    _read(new CanonicalTemporalJsonConverters.DayConverter(), "\"2026-03-04\"");
    _read(new CanonicalTemporalJsonConverters.DayConverter(), "\"2026-03-05\"");

    var records = logger.Collector.GetSnapshot();
    await Assert.That(records.Count).IsEqualTo(2)
      .Because("two kinds were read as renderings, and each is announced exactly once");
    await Assert.That(records.All(r => r.Level == LogLevel.Warning)).IsTrue();
    await Assert.That(records[0].Message).Contains(nameof(StoredTemporalKind.Instant));
    await Assert.That(records[0].Message).Contains(CanonicalTemporalFallbacks.INSTRUMENT_NAME);
    await Assert.That(records[1].Message).Contains(nameof(StoredTemporalKind.Day));
  }

  /// <summary>Configuring again starts the announcements over, which is what a test needs and a process never does.</summary>
  [Test]
  [NotInParallel]
  public async Task ConfiguringAgainStartsTheAnnouncementsOverAsync() {
    var first = new FakeLogger();
    CanonicalTemporalFallbacks.Configure(meterFactory: null, logger: first);
    _read(new CanonicalTemporalJsonConverters.TimeOfDayConverter(), "\"05:06:07\"");

    var second = new FakeLogger();
    CanonicalTemporalFallbacks.Configure(meterFactory: null, logger: second);
    _read(new CanonicalTemporalJsonConverters.TimeOfDayConverter(), "\"05:06:08\"");

    await Assert.That(first.Collector.Count).IsEqualTo(1);
    await Assert.That(second.Collector.Count).IsEqualTo(1);
  }

  /// <summary>A meter factory, when one is configured, is what creates the meter.</summary>
  [Test]
  [NotInParallel]
  public async Task AConfiguredMeterFactoryCreatesTheMeterAsync() {
    var factory = new RecordingMeterFactory();

    CanonicalTemporalFallbacks.Configure(factory, logger: null);

    await Assert.That(factory.Created).Contains(CanonicalTemporalFallbacks.METER_NAME);
  }

  /// <summary>The optional wrapper still hands a rendering through to the underlying reader.</summary>
  [Test]
  public async Task TheOptionalWrapperHandsARenderingThroughAsync() {
    var converter = new CanonicalTemporalJsonConverters.NullableConverter<DateTime>(
      new CanonicalTemporalJsonConverters.InstantConverter());

    await Assert.That(_read(converter, "\"2026-03-04T05:06:07.89Z\"")).IsEqualTo(_instant);
    await Assert.That(_read(converter, "null")).IsNull();
  }

  private sealed class RecordingMeterFactory : IMeterFactory {
    public List<string> Created { get; } = [];

    public Meter Create(MeterOptions options) {
      Created.Add(options.Name);
      return new Meter(options);
    }

    public void Dispose() { }
  }
}
