using System.Collections.Generic;
using System.Collections.Immutable;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The statements that rewrite a perspective's dates, times and durations into their canonical
/// stored form.
/// </summary>
/// <remarks>
/// <para>
/// A row written by an earlier release holds a rendering; the mapping now writes a number. A column
/// holding both cannot be indexed and cannot be range-scanned correctly, so this is not an
/// optimization but the thing that makes the format change safe. It belongs with the perspective's
/// other schema, ahead of the index that is built over the result, and it runs before the
/// application serves traffic, so no query can observe a half-converted column.
/// </para>
/// <para>
/// Each statement carries its own precondition: it selects on the stored type being a string, so a
/// value already converted is not selected. Re-running it is a no-op, a restored backup corrects
/// itself, and a database created by this release has nothing to convert. That is deliberately not a
/// marker column or a version row, which a restore can contradict while the data says otherwise.
/// </para>
/// <para>
/// <strong>No braces.</strong> This text is embedded into generated C# where <c>{</c> and <c>}</c>
/// are doubled for the formatter, so a <c>jsonb_set</c> path literal or a regular-expression
/// quantifier would arrive at the database mangled. The merge operator replaces the path literal and
/// repeated character classes replace the quantifier.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/CanonicalTemporalBackfillTests.cs</tests>
public static class CanonicalTemporalBackfillSql {
  /// <summary>
  /// Microseconds since the epoch for <c>DateTime.MaxValue</c>, which was stored as the word
  /// <c>infinity</c> rather than as a date.
  /// </summary>
  /// <remarks>
  /// Casting the word to a timestamp succeeds and taking its epoch overflows, so this is a case that
  /// has to be named rather than left to the general conversion.
  /// </remarks>
  private const string MAX_MICROSECONDS = "253402300799999999";

  /// <summary>Microseconds since the epoch for <c>DateTime.MinValue</c>.</summary>
  private const string MIN_MICROSECONDS = "-62135596800000000";

  /// <summary>
  /// The shape a duration was rendered in: an optional day count, the clock, and an optional
  /// fraction, with a sign that applies to the whole value.
  /// </summary>
  private const string DURATION_PATTERN =
    @"'^-?(?:(\d+)\.)?(\d\d):(\d\d):(\d\d)(?:\.(\d+))?$'";

  /// <summary>
  /// One statement per property, in the order the properties were discovered.
  /// </summary>
  /// <param name="properties">The model's temporal properties.</param>
  /// <param name="qualifiedTable">The perspective's table, already schema-qualified and quoted.</param>
  /// <returns>The statements, or none when the model holds no temporal property.</returns>
  public static IEnumerable<string> Statements(
      ImmutableArray<CanonicalTemporalProperty> properties, string qualifiedTable) {
    if (properties.IsDefaultOrEmpty) {
      yield break;
    }

    foreach (var property in properties) {
      yield return property.Kind == CanonicalTemporalKind.Duration
        ? _durationStatement(property.PropertyName, qualifiedTable)
        : _simpleStatement(property, qualifiedTable);
    }
  }

  /// <summary>
  /// The rewrite for everything whose rendering PostgreSQL can parse on its own.
  /// </summary>
  /// <remarks>
  /// The merge operator is used rather than a path assignment because a path literal needs braces,
  /// and braces do not survive the trip through generated C#.
  /// </remarks>
  private static string _simpleStatement(CanonicalTemporalProperty property, string table) {
    var key = property.PropertyName;
    var value = _conversion(property.Kind, key);

    return $"""
      UPDATE {table}
      SET data = data || jsonb_build_object('{key}', {value})
      WHERE jsonb_typeof(data -> '{key}') = 'string';
      """;
  }

  /// <summary>The expression producing the stored number from the stored rendering.</summary>
  private static string _conversion(CanonicalTemporalKind kind, string key) => kind switch {
    // An offset reduces to the instant it names, which the cast already does: a timestamptz holds
    // an instant, so the offset it was written with stops mattering at the cast rather than needing
    // to be handled.
    CanonicalTemporalKind.Instant or CanonicalTemporalKind.OffsetInstant =>
      $"""
      CASE data ->> '{key}'
            WHEN 'infinity' THEN {MAX_MICROSECONDS}
            WHEN '-infinity' THEN {MIN_MICROSECONDS}
            ELSE (EXTRACT(EPOCH FROM (data ->> '{key}')::timestamptz) * 1000000)::bigint
          END
      """,
    CanonicalTemporalKind.Day =>
      $"((data ->> '{key}')::date - DATE '1970-01-01')",
    // A time was rendered with seven fractional digits where a date got six, and the cast to a time
    // ROUNDS that seventh digit while the writer truncates it. Left alone the two disagree by a
    // microsecond, which is a difference no query would ever surface and every comparison would be
    // wrong about. The extra digits are dropped from the text first, so the cast has nothing to
    // round and both sides truncate.
    //
    // Only this type needs it. The instants are written with six digits already, which
    // PerspectiveDateFormatLockTests pins by exact assertion.
    _ =>
      $"(EXTRACT(EPOCH FROM {_truncatedToMicroseconds(key)}::time) * 1000000)::bigint",
  };

  /// <summary>
  /// The stored text with any fractional digits past the microsecond removed.
  /// </summary>
  /// <remarks>
  /// Written as six explicit digit classes rather than a quantifier, because a quantifier needs
  /// braces and braces do not survive the trip through generated C#.
  /// </remarks>
  private static string _truncatedToMicroseconds(string key) =>
    $@"regexp_replace(data ->> '{key}', '(\.\d\d\d\d\d\d)\d+$', '\1')";

  /// <summary>
  /// The rewrite for a duration, whose rendering PostgreSQL cannot parse.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The rendering separates a day count from the clock with a period, where an interval wants a
  /// space, and it carries a seventh fractional digit an interval could not hold anyway. So the
  /// components are matched and the ticks computed from them, which keeps full precision and does
  /// not depend on how an interval would round.
  /// </para>
  /// <para>
  /// The sign applies to the whole duration rather than to its first component, so it is taken from
  /// the text and multiplied through. A conversion that negated only the day count would be right
  /// for a whole number of days and wrong for everything else.
  /// </para>
  /// <para>
  /// The match is computed once in a subquery rather than repeated inline, and a row whose text does
  /// not match is left alone rather than written as null: an unrecognized rendering is a thing to
  /// look at, and the index that follows will refuse to build while it is still there.
  /// </para>
  /// </remarks>
  private static string _durationStatement(string key, string table) => $"""
    UPDATE {table} AS t
    SET data = t.data || jsonb_build_object('{key}',
          (CASE WHEN left(t.data ->> '{key}', 1) = '-' THEN -1 ELSE 1 END)::bigint * (
            coalesce(s.parts[1], '0')::bigint * 864000000000
            + s.parts[2]::bigint * 36000000000
            + s.parts[3]::bigint * 600000000
            + s.parts[4]::bigint * 10000000
            + coalesce(rpad(s.parts[5], 7, '0'), '0')::bigint
          ))
    FROM (
      SELECT id, regexp_match(data ->> '{key}', {DURATION_PATTERN}) AS parts
      FROM {table}
      WHERE jsonb_typeof(data -> '{key}') = 'string'
    ) AS s
    WHERE t.id = s.id AND s.parts IS NOT NULL;
    """;
}
