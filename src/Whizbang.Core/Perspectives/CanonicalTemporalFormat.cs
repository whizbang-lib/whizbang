namespace Whizbang.Core.Perspectives;

/// <summary>
/// The stored form of a date, a time or a duration inside a perspective document.
/// </summary>
/// <remarks>
/// <para>
/// A number rather than a rendering, which is what lets the whole family behave like every other
/// type. The extraction reaches an immutable cast, so an index can be built over it and a range or an
/// ordering answered from one. jsonb compares numbers by value, so equality needs no rendering at
/// all, and the problems a text form brings with it, a fraction whose width varies and extremes that
/// are stored as words rather than dates, stop existing rather than being handled.
/// </para>
/// <para>
/// The cost is that those fields are no longer readable at a glance in the stored document. It was
/// taken deliberately against measurement: an eight-byte key indexes at less than half the size of
/// the twenty-seven byte text one for the same rows and the same plan, and a text column cannot be
/// range-scanned correctly while any row is still unconverted, whereas a numeric one refuses the
/// query outright instead of answering it wrongly.
/// </para>
/// <para>
/// <strong>One unit for every kind: the microsecond.</strong> An instant is microseconds since the
/// Unix epoch, a date is the same at midnight UTC, a time of day is microseconds since midnight and a
/// duration is microseconds. So a date orders against an instant with the same cast, an instant plus
/// a duration is arithmetic on the stored numbers, and a time of day is a duration from midnight.
/// The first release of this form stored a date as a day count and a duration as a tick count, and
/// nothing in a document could say which of three units a number was in; the ledger that migrated
/// those rows is described with the migration.
/// </para>
/// <para>
/// <strong>This is a compatibility contract.</strong> The moment rows exist in this form, changing
/// any of it is a migration rather than a refactor, which is why the values are pinned by exact
/// assertion rather than by round-trip alone.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalFormatTests.cs</tests>
public static class CanonicalTemporalFormat {
#pragma warning disable CA1707
  /// <summary>Microseconds in a day, which is what a date advances by.</summary>
  public const long MICROSECONDS_PER_DAY = 86_400_000_000L;
#pragma warning restore CA1707

  /// <summary>Ticks in a microsecond, which is the precision a PostgreSQL timestamp keeps.</summary>
  private const long TICKS_PER_MICROSECOND = TimeSpan.TicksPerMillisecond / 1000;

  /// <summary>Midnight on the first of January 1970, in ticks.</summary>
  private static readonly long _epochTicks = DateTime.UnixEpoch.Ticks;

  /// <summary>The first of January 1970, as a day number.</summary>
  private static readonly int _epochDayNumber = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

  /// <summary>
  /// An instant as microseconds since the Unix epoch.
  /// </summary>
  /// <param name="value">The instant. A value that is not UTC is converted to UTC first.</param>
  /// <returns>Microseconds since the epoch, negative before it.</returns>
  /// <remarks>
  /// Truncates below a microsecond, matching what a timestamp can hold. That is not a loss being
  /// accepted so much as one already present: a value finer than a microsecond cannot survive a round
  /// trip through the database either way, and truncating on both sides is what makes the two agree
  /// rather than differ by a digit nobody can see.
  /// </remarks>
  public static long ToEpochMicroseconds(DateTime value) {
    var utc = value.Kind switch {
      DateTimeKind.Utc => value,
      DateTimeKind.Local => value.ToUniversalTime(),
      _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    return (utc.Ticks - _epochTicks) / TICKS_PER_MICROSECOND;
  }

  /// <summary>
  /// An offset reduced to the instant it names.
  /// </summary>
  /// <param name="value">The value, whatever offset it carries.</param>
  /// <returns>Microseconds since the epoch.</returns>
  /// <remarks>
  /// This is what makes the type storable at all. Rendered, it preserved the offset it was written
  /// with, so one instant had many stored texts and no query could produce them all; reduced to the
  /// instant it compares the way equality does, which compares instants. A model that needs the
  /// original offset back keeps it in a field of its own.
  /// </remarks>
  public static long ToEpochMicroseconds(DateTimeOffset value) =>
    (value.UtcTicks - _epochTicks) / TICKS_PER_MICROSECOND;

  /// <summary>A date as microseconds since the Unix epoch, at midnight UTC of that date.</summary>
  /// <param name="value">The date.</param>
  /// <returns>Microseconds since the epoch, negative before it.</returns>
  /// <remarks>
  /// The same number an instant at that midnight has, which is what lets a date be compared with an
  /// instant at all. A day count could not: a day count and a microsecond count are both integers,
  /// and nothing in a document says which one a number is.
  /// </remarks>
  public static long ToEpochMicroseconds(DateOnly value) =>
    (value.DayNumber - _epochDayNumber) * MICROSECONDS_PER_DAY;

  /// <summary>The instant that many microseconds after the epoch.</summary>
  /// <param name="microseconds">Microseconds since the epoch.</param>
  /// <returns>The instant, as UTC.</returns>
  public static DateTime FromEpochMicroseconds(long microseconds) =>
    new((microseconds * TICKS_PER_MICROSECOND) + _epochTicks, DateTimeKind.Utc);

  /// <summary>The same instant, as an offset of zero.</summary>
  /// <param name="microseconds">Microseconds since the epoch.</param>
  /// <returns>The instant, with no offset.</returns>
  public static DateTimeOffset OffsetFromEpochMicroseconds(long microseconds) =>
    new(FromEpochMicroseconds(microseconds), TimeSpan.Zero);

  /// <summary>The date that a number of microseconds since the epoch falls on.</summary>
  /// <param name="microseconds">Microseconds since the epoch.</param>
  /// <returns>The date containing that instant.</returns>
  /// <remarks>
  /// The writer only ever stores midnight, so this is about what a reader does with a number it did
  /// not write, an instant compared against a date column, say. Floored rather than truncated, so a
  /// value late on the last day of 1969 stays in 1969 instead of rounding toward zero into 1970.
  /// </remarks>
  public static DateOnly DayFromEpochMicroseconds(long microseconds) {
    var days = Math.DivRem(microseconds, MICROSECONDS_PER_DAY, out var remainder);
    if (remainder < 0) {
      days--;
    }

    return DateOnly.FromDayNumber((int)days + _epochDayNumber);
  }

  /// <summary>A time of day as microseconds since midnight.</summary>
  /// <param name="value">The time of day.</param>
  /// <returns>Microseconds since midnight.</returns>
  /// <remarks>
  /// Truncates below a microsecond for the same reason a date does. This is also the type whose
  /// rendering carried seven fractional digits where a date carried six, so a stored value could not
  /// be matched by a parameter at all; as a number the discrepancy has nowhere to live.
  /// </remarks>
  public static long ToMicrosecondsOfDay(TimeOnly value) => value.Ticks / TICKS_PER_MICROSECOND;

  /// <summary>The time of day that many microseconds after midnight.</summary>
  /// <param name="microseconds">Microseconds since midnight.</param>
  /// <returns>The time of day.</returns>
  public static TimeOnly FromMicrosecondsOfDay(long microseconds) =>
    new(microseconds * TICKS_PER_MICROSECOND);

  /// <summary>A duration as microseconds.</summary>
  /// <param name="value">The duration.</param>
  /// <returns>Microseconds, negative for a negative duration.</returns>
  /// <remarks>
  /// Truncates the seventh fractional digit, as an instant does. Stored as ticks a duration kept a
  /// digit no other kind had and could not be added to an instant without a conversion nobody would
  /// remember to write in SQL; a microsecond is what a PostgreSQL interval holds, so the digit given
  /// up is one the database could never have compared against anyway.
  /// </remarks>
  public static long ToMicroseconds(TimeSpan value) => value.Ticks / TICKS_PER_MICROSECOND;

  /// <summary>The duration of that many microseconds.</summary>
  /// <param name="microseconds">Microseconds.</param>
  /// <returns>The duration.</returns>
  public static TimeSpan DurationFromMicroseconds(long microseconds) =>
    new(microseconds * TICKS_PER_MICROSECOND);
}
