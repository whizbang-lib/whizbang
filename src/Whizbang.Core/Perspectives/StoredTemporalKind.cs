namespace Whizbang.Core.Perspectives;

/// <summary>
/// The kinds of date, time and duration a perspective document stores in the canonical form.
/// </summary>
/// <remarks>
/// One entry per CLR type the form covers. Named by what the value means rather than by the type,
/// because the stored number is defined by the meaning: an instant and a date share a unit and an
/// origin, a time of day shares the unit and starts at midnight, a duration has no origin at all.
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public enum StoredTemporalKind {
  /// <summary>A <see cref="DateTime"/>: microseconds since the Unix epoch.</summary>
  Instant,

  /// <summary>A <see cref="DateTimeOffset"/>: the instant it names, as microseconds since the epoch.</summary>
  OffsetInstant,

  /// <summary>A <see cref="DateOnly"/>: microseconds since the epoch at midnight UTC of that date.</summary>
  Day,

  /// <summary>A <see cref="TimeOnly"/>: microseconds since midnight.</summary>
  TimeOfDay,

  /// <summary>A <see cref="TimeSpan"/>: microseconds.</summary>
  Duration,
}
