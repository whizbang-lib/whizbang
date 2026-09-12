using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The stored form of a date, a time or a duration in a perspective document.
/// </summary>
/// <remarks>
/// <para>
/// A number rather than a rendering, which is what makes the whole family behave like every other
/// type. The extraction reaches an immutable cast, so an index can be built over it and a range or an
/// ordering can be answered from one; jsonb compares numbers by value, so equality needs no rendering
/// at all and the trailing-zero and infinity problems of a text form simply do not arise.
/// </para>
/// <para>
/// The cost is a document a person can no longer read at a glance for those fields, accepted
/// deliberately: an eight-byte key indexes at less than half the size of the twenty-seven byte text
/// one, and the text form cannot be range-scanned correctly while any row is unconverted.
/// </para>
/// <para>
/// These are exact values on purpose. The stored form is a compatibility contract the moment rows
/// exist in it, so a change here is a migration rather than a refactor, and the test should say so by
/// failing.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public class CanonicalTemporalFormatTests {
  /// <summary>
  /// A date is microseconds since the Unix epoch, which is the precision PostgreSQL keeps.
  /// </summary>
  [Test]
  [Arguments("2026-03-04T05:06:07Z", 1772600767000000L)]
  [Arguments("1970-01-01T00:00:00Z", 0L)]
  [Arguments("1969-12-31T23:59:59Z", -1000000L)]
  public async Task ADateTimeIsMicrosecondsSinceTheEpochAsync(string iso, long expected) {
    var value = DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture,
      System.Globalization.DateTimeStyles.AdjustToUniversal
      | System.Globalization.DateTimeStyles.AssumeUniversal);

    await Assert.That(CanonicalTemporalFormat.ToEpochMicroseconds(value)).IsEqualTo(expected);
  }

  /// <summary>
  /// A value finer than a microsecond truncates, matching what a timestamp can hold, so the two sides
  /// of a comparison agree by construction.
  /// </summary>
  [Test]
  public async Task FinerThanAMicrosecondTruncatesAsync() {
    var second = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    await Assert.That(CanonicalTemporalFormat.ToEpochMicroseconds(second.AddTicks(1_234_567)))
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(second.AddTicks(1_234_560)))
      .Because("a PostgreSQL timestamp is microseconds, so a seventh fractional digit cannot survive "
        + "a round trip either way and truncating on both sides is what makes them agree");
  }

  /// <summary>The round trip is lossless at the precision it keeps.</summary>
  [Test]
  [Arguments("2026-03-04T05:06:07Z")]
  [Arguments("2026-03-04T05:06:07.123456Z")]
  [Arguments("0001-01-01T00:00:00Z")]
  [Arguments("9999-12-31T23:59:59.999999Z")]
  public async Task ADateTimeRoundTripsAsync(string iso) {
    var value = DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture,
      System.Globalization.DateTimeStyles.AdjustToUniversal
      | System.Globalization.DateTimeStyles.AssumeUniversal);

    var restored = CanonicalTemporalFormat.FromEpochMicroseconds(
      CanonicalTemporalFormat.ToEpochMicroseconds(value));

    await Assert.That(restored).IsEqualTo(value);
    await Assert.That(restored.Kind).IsEqualTo(DateTimeKind.Utc)
      .Because("the stored form is an instant, so what comes back is one");
  }

  /// <summary>
  /// The extremes are ordinary numbers, which is the whole reason the infinity handling disappears.
  /// </summary>
  /// <remarks>
  /// Stored as a rendering, the extremes of the range became the words infinity and -infinity, which
  /// no formatting could produce and which needed their own branch in the emission. In microseconds
  /// they are simply large and small integers, well inside what an eight-byte integer holds.
  /// </remarks>
  [Test]
  public async Task TheExtremesAreOrdinaryNumbersAsync() {
    var max = CanonicalTemporalFormat.ToEpochMicroseconds(
      DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
    var min = CanonicalTemporalFormat.ToEpochMicroseconds(
      DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));

    await Assert.That(max).IsGreaterThan(0L);
    await Assert.That(min).IsLessThan(0L);
    await Assert.That(max).IsLessThan(long.MaxValue);
    await Assert.That(min).IsGreaterThan(long.MinValue);
  }

  /// <summary>
  /// An offset normalizes to its instant, so two values equal in .NET are equal in the document.
  /// </summary>
  /// <remarks>
  /// This is what makes a DateTimeOffset storable at all. Its rendering preserved the offset it was
  /// written with, so one instant had many stored texts and no query could produce them all; reduced
  /// to the instant it compares the way equality does. The offset itself is kept separately where a
  /// model needs it back.
  /// </remarks>
  [Test]
  public async Task AnOffsetNormalizesToItsInstantAsync() {
    var utc = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    var ahead = utc.ToOffset(TimeSpan.FromMinutes(330));
    var behind = utc.ToOffset(TimeSpan.FromHours(-8));

    var stored = CanonicalTemporalFormat.ToEpochMicroseconds(utc);

    await Assert.That(CanonicalTemporalFormat.ToEpochMicroseconds(ahead)).IsEqualTo(stored);
    await Assert.That(CanonicalTemporalFormat.ToEpochMicroseconds(behind)).IsEqualTo(stored)
      .Because("equality compares instants, so the stored form has to as well");
  }

  /// <summary>A date without a time is days since the epoch, which sorts and ranges the same way.</summary>
  [Test]
  [Arguments("2026-03-04", 20516)]
  [Arguments("1970-01-01", 0)]
  [Arguments("1969-12-31", -1)]
  public async Task ADateOnlyIsDaysSinceTheEpochAsync(string iso, int expected) {
    var value = DateOnly.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    await Assert.That(CanonicalTemporalFormat.ToEpochDays(value)).IsEqualTo(expected);
    await Assert.That(CanonicalTemporalFormat.FromEpochDays(expected)).IsEqualTo(value);
  }

  /// <summary>A time of day is microseconds since midnight.</summary>
  [Test]
  public async Task ATimeOnlyIsMicrosecondsSinceMidnightAsync() {
    var value = new TimeOnly(5, 6, 7);

    await Assert.That(CanonicalTemporalFormat.ToMicrosecondsOfDay(value)).IsEqualTo(18367000000L);
    await Assert.That(CanonicalTemporalFormat.FromMicrosecondsOfDay(18367000000L)).IsEqualTo(value);
  }

  /// <summary>
  /// A duration is its tick count, which needs no truncation because nothing renders it.
  /// </summary>
  /// <remarks>
  /// Unlike the others this keeps full .NET precision, because a duration is not compared against a
  /// PostgreSQL interval: it is a number on both sides. The awkward rendering that ruled it out of
  /// the eligible set, a day count only sometimes present and a trimmed fraction, stops existing.
  /// </remarks>
  [Test]
  public async Task ATimeSpanIsItsTickCountAsync() {
    var value = new TimeSpan(2, 5, 6, 7, 123);

    await Assert.That(CanonicalTemporalFormat.ToTicks(value)).IsEqualTo(value.Ticks);
    await Assert.That(CanonicalTemporalFormat.FromTicks(value.Ticks)).IsEqualTo(value);
  }

  /// <summary>
  /// Ordering by the stored number is ordering by time, which is what the rendering could not do.
  /// </summary>
  /// <remarks>
  /// The stored text sorted by fraction width before it sorted by time, so it could not serve as a
  /// btree key. This is the property that replaces it, and it is the reason the format changed at
  /// all.
  /// </remarks>
  [Test]
  public async Task TheStoredNumbersSortChronologicallyAsync() {
    var origin = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    var instants = new[] {
      origin.AddTicks(1_000_000),
      origin,
      origin.AddTicks(1_234_560),
      origin.AddSeconds(1),
    };

    var byNumber = instants
      .OrderBy(CanonicalTemporalFormat.ToEpochMicroseconds)
      .Select(d => d.Ticks);

    await Assert.That(string.Join(",", byNumber))
      .IsEqualTo(string.Join(",", instants.OrderBy(d => d).Select(d => d.Ticks)));
  }

  /// <summary>
  /// A local time is converted to the instant it names, not stored as its wall clock.
  /// </summary>
  /// <remarks>
  /// The stored form is an instant, so a value carrying a zone has to be reduced to one. Storing the
  /// wall clock would make the same moment sort differently depending on which machine wrote it.
  /// </remarks>
  [Test]
  public async Task ALocalTimeIsConvertedToItsInstantAsync() {
    var local = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local);

    await Assert.That(CanonicalTemporalFormat.ToEpochMicroseconds(local))
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(local.ToUniversalTime()))
      .Because("the stored form is an instant, so a local value is the same instant written from a "
        + "zone and has to reduce to it");
  }

  /// <summary>
  /// A value with no kind is assumed to be UTC rather than local.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is a decision and not an obvious one, which is why it is pinned. An unspecified
  /// <c>DateTime</c> is what a model most often carries: parsed from text without a zone, or
  /// defaulted. Treating it as local would make the stored instant depend on the machine that wrote
  /// the row, so the same value would land differently in development and in production and neither
  /// would be wrong enough to notice.
  /// </para>
  /// <para>
  /// Assuming UTC is stable and matches what the rest of the framework does with an unzoned stamp.
  /// If this assertion ever changes, every row written under the old assumption is off by an offset.
  /// </para>
  /// </remarks>
  [Test]
  public async Task AnUnspecifiedKindIsAssumedUtcAsync() {
    var unspecified = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);
    var sameAsUtc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    await Assert.That(CanonicalTemporalFormat.ToEpochMicroseconds(unspecified))
      .IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(sameAsUtc))
      .Because("treating an unzoned value as local would make the stored instant depend on the "
        + "machine that wrote the row, which is a difference nothing would surface");
  }
}
