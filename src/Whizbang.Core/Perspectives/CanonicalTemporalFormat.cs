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
/// <strong>This is a compatibility contract.</strong> The moment rows exist in this form, changing
/// any of it is a migration rather than a refactor, which is why the values are pinned by exact
/// assertion rather than by round-trip alone.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CanonicalTemporalFormatTests.cs</tests>
public static class CanonicalTemporalFormat {
  /// <summary>Ticks in a microsecond, which is the precision a PostgreSQL timestamp keeps.</summary>
  private const long TICKS_PER_MICROSECOND = TimeSpan.TicksPerMillisecond / 1000;

  /// <summary>Midnight on the first of January 1970, in ticks.</summary>
  private static readonly long _epochTicks = DateTime.UnixEpoch.Ticks;

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

  /// <summary>A date as days since the Unix epoch.</summary>
  /// <param name="value">The date.</param>
  /// <returns>Days since the epoch, negative before it.</returns>
  public static int ToEpochDays(DateOnly value) => value.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

  /// <summary>The date that many days after the epoch.</summary>
  /// <param name="days">Days since the epoch.</param>
  /// <returns>The date.</returns>
  public static DateOnly FromEpochDays(int days) =>
    DateOnly.FromDayNumber(days + DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber);

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

  /// <summary>A duration as its tick count.</summary>
  /// <param name="value">The duration.</param>
  /// <returns>The tick count.</returns>
  /// <remarks>
  /// Keeps full precision, unlike the instants, because a duration is never compared against a
  /// PostgreSQL interval: it is a number on both sides of the comparison. The rendering that ruled it
  /// out of the eligible set, a day count present only when non-zero followed by a trimmed fraction,
  /// simply stops existing.
  /// </remarks>
  public static long ToTicks(TimeSpan value) => value.Ticks;

  /// <summary>The duration of that many ticks.</summary>
  /// <param name="ticks">The tick count.</param>
  /// <returns>The duration.</returns>
  public static TimeSpan FromTicks(long ticks) => new(ticks);
}
