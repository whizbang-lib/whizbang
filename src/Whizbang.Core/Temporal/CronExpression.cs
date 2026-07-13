namespace Whizbang.Core.Temporal;

/// <summary>
/// A home-grown parser + next-fire calculator for standard 5-field cron expressions
/// (<c>minute hour day-of-month month day-of-week</c>). Deliberately NOT a third-party library: the same
/// computation is mirrored DB-side (a Postgres cron function) so the atomic claim+advance can compute
/// <c>next_fire_at</c> in SQL. Supports <c>*</c>, single values, ranges (<c>a-b</c>), steps (<c>*&#47;n</c>,
/// <c>a-b&#47;n</c>, <c>a&#47;n</c>), lists (<c>a,b,c</c>), and month/day names (<c>JAN</c>..<c>DEC</c>,
/// <c>SUN</c>..<c>SAT</c>). Day-of-month and day-of-week follow Vixie-cron OR semantics when both are
/// restricted. Next-fire is evaluated in a supplied <see cref="TimeZoneInfo"/> (DST-aware).
/// </summary>
/// <docs>fundamentals/temporal/recurrence</docs>
public sealed class CronExpression {
  // Search no further than this many years ahead before declaring an expression unsatisfiable
  // (e.g. "0 0 30 2 *" — Feb 30 never occurs).
  private const int SEARCH_HORIZON_YEARS = 5;

  private static readonly Dictionary<string, int> _monthNames = new(StringComparer.Ordinal) {
    ["JAN"] = 1,
    ["FEB"] = 2,
    ["MAR"] = 3,
    ["APR"] = 4,
    ["MAY"] = 5,
    ["JUN"] = 6,
    ["JUL"] = 7,
    ["AUG"] = 8,
    ["SEP"] = 9,
    ["OCT"] = 10,
    ["NOV"] = 11,
    ["DEC"] = 12,
  };

  private static readonly Dictionary<string, int> _dayNames = new(StringComparer.Ordinal) {
    ["SUN"] = 0,
    ["MON"] = 1,
    ["TUE"] = 2,
    ["WED"] = 3,
    ["THU"] = 4,
    ["FRI"] = 5,
    ["SAT"] = 6,
  };

  private readonly bool[] _minutes;       // [0..59]
  private readonly bool[] _hours;         // [0..23]
  private readonly bool[] _daysOfMonth;   // [1..31]
  private readonly bool[] _months;        // [1..12]
  private readonly bool[] _daysOfWeek;    // [0..6], Sunday = 0
  private readonly bool _domRestricted;
  private readonly bool _dowRestricted;

  private CronExpression(
    bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek,
    bool domRestricted, bool dowRestricted) {
    _minutes = minutes;
    _hours = hours;
    _daysOfMonth = daysOfMonth;
    _months = months;
    _daysOfWeek = daysOfWeek;
    _domRestricted = domRestricted;
    _dowRestricted = dowRestricted;
  }

  /// <summary>Parses a 5-field cron expression. Throws <see cref="FormatException"/> on malformed input.</summary>
  public static CronExpression Parse(string expression) {
    ArgumentNullException.ThrowIfNull(expression);
    var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (fields.Length != 5) {
      throw new FormatException($"Cron expression must have exactly 5 fields; got {fields.Length}: '{expression}'.");
    }

    var minutes = _parseField(fields[0], 0, 59, names: null, out _);
    var hours = _parseField(fields[1], 0, 23, names: null, out _);
    var daysOfMonth = _parseField(fields[2], 1, 31, names: null, out var domRestricted);
    var months = _parseField(fields[3], 1, 12, _monthNames, out _);
    var daysOfWeek = _parseField(fields[4], 0, 7, _dayNames, out var dowRestricted);

    // Cron allows 0 or 7 for Sunday; fold 7 onto 0 and collapse to a [0..6] array.
    var dow = new bool[7];
    for (var i = 0; i <= 6; i++) {
      dow[i] = daysOfWeek[i];
    }
    if (daysOfWeek[7]) {
      dow[0] = true;
    }

    return new CronExpression(minutes, hours, daysOfMonth, months, dow, domRestricted, dowRestricted);
  }

  /// <summary>
  /// The next fire time strictly after <paramref name="after"/>, evaluated in <paramref name="timeZone"/>,
  /// or <c>null</c> if no match occurs within the search horizon (e.g. an impossible expression).
  /// </summary>
  public DateTimeOffset? NextFireAfter(DateTimeOffset after, TimeZoneInfo timeZone) {
    ArgumentNullException.ThrowIfNull(timeZone);

    var local = TimeZoneInfo.ConvertTime(after, timeZone);
    // Truncate to the minute and step strictly forward.
    var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified)
      .AddMinutes(1);
    var limit = t.AddYears(SEARCH_HORIZON_YEARS);

    while (t < limit) {
      if (!_months[t.Month]) {
        t = _startOfNextMonth(t);
        continue;
      }
      if (!_dayMatches(t)) {
        t = t.Date.AddDays(1);
        continue;
      }
      if (!_hours[t.Hour]) {
        t = t.Date.AddHours(t.Hour + 1);   // advance to the next hour boundary (minutes reset to 0)
        continue;
      }
      if (!_minutes[t.Minute]) {
        t = t.AddMinutes(1);
        continue;
      }
      // All fields match this local wall-clock minute. A local time skipped by spring-forward has no
      // instant — keep searching. Ambiguous (fall-back) times resolve via the zone's standard offset.
      if (timeZone.IsInvalidTime(t)) {
        t = t.AddMinutes(1);
        continue;
      }
      return new DateTimeOffset(t, timeZone.GetUtcOffset(t));
    }
    return null;
  }

  private bool _dayMatches(DateTime t) {
    var domOk = _daysOfMonth[t.Day];
    var dowOk = _daysOfWeek[(int)t.DayOfWeek];
    if (_domRestricted && _dowRestricted) {
      return domOk || dowOk;   // Vixie-cron OR semantics
    }
    if (_domRestricted) {
      return domOk;
    }
    if (_dowRestricted) {
      return dowOk;
    }
    return true;
  }

  private static DateTime _startOfNextMonth(DateTime t) {
    var year = t.Year;
    var month = t.Month;
    if (month == 12) {
      year++;
      month = 1;
    } else {
      month++;
    }
    return new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
  }

  private static bool[] _parseField(string field, int min, int max, IReadOnlyDictionary<string, int>? names, out bool restricted) {
    restricted = !string.Equals(field, "*", StringComparison.Ordinal);
    var set = new bool[max + 1];
    foreach (var term in field.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
      _applyTerm(term, min, max, names, set);
    }
    if (Array.IndexOf(set, true) < 0) {
      throw new FormatException($"Cron field '{field}' matches no values.");
    }
    return set;
  }

  private static void _applyTerm(string term, int min, int max, IReadOnlyDictionary<string, int>? names, bool[] set) {
    var step = 1;
    var rangePart = term;
    var slash = term.IndexOf('/', StringComparison.Ordinal);
    if (slash >= 0) {
      rangePart = term[..slash];
      var stepText = term[(slash + 1)..];
      if (!int.TryParse(stepText, out step) || step < 1) {
        throw new FormatException($"Invalid step in cron term '{term}'.");
      }
    }

    int lo, hi;
    if (rangePart == "*") {
      lo = min;
      hi = max;
    } else {
      var dash = rangePart.IndexOf('-', StringComparison.Ordinal);
      if (dash > 0) {
        lo = _parseValue(rangePart[..dash], names, term);
        hi = _parseValue(rangePart[(dash + 1)..], names, term);
      } else {
        lo = _parseValue(rangePart, names, term);
        // Vixie-cron: "a/step" means a..max/step; a bare "a" means just a.
        hi = slash >= 0 ? max : lo;
      }
    }

    if (lo < min || hi > max || lo > hi) {
      throw new FormatException($"Cron term '{term}' out of range [{min}-{max}].");
    }
    for (var v = lo; v <= hi; v += step) {
      set[v] = true;
    }
  }

  private static int _parseValue(string token, IReadOnlyDictionary<string, int>? names, string term) {
    if (names is not null && names.TryGetValue(token.ToUpperInvariant(), out var named)) {
      return named;
    }
    if (!int.TryParse(token, out var value)) {
      throw new FormatException($"Invalid cron value '{token}' in term '{term}'.");
    }
    return value;
  }
}
