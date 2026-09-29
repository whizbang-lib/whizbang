using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The stored-format rewrite that converts an enumeration's physical column from the text form an earlier release
/// wrote (the member's name) to the underlying number the column now holds. Generated per enum column by the schema
/// generator, with the enum's own members, and run by <see cref="CanonicalTemporalRewritePhase"/> under the schema
/// lock before the indexes are built.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent: it does anything only while the column is still <c>text</c> (or <c>varchar</c>), so a column the
/// schema pass has not created yet, or one already converted, is left alone. Each member name maps to its number
/// (names match exactly, as both drivers wrote them); a value that is already a number (an undefined value's
/// <c>ToString()</c>, or a row a newer instance wrote) is kept; a null stays null. For a <c>[Flags]</c> enumeration
/// (<see cref="BuildFlags"/>) a value may also combine names as .NET writes them (<c>"A, B"</c>), and becomes the
/// bitwise OR of their values.
/// </para>
/// <para>
/// A value that is neither (for a <c>[Flags]</c> enumeration, a value with a component that is not a member name)
/// stops the conversion before anything changes, with SQLSTATE
/// <see cref="BLOCKED_SQL_STATE"/> and a message naming the table, the column and a sample of the values. The phase
/// turns that into a <see cref="StoredFormConversionBlockedException"/>, which fails startup: a column the
/// framework cannot read is not something to start on.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#enum-text-columns</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/EnumColumnRewriteTests.cs</tests>
public static class EnumColumnRewriteSql {
  /// <summary>The SQLSTATE a rewrite raises when a value cannot be converted; the phase fails startup on it.</summary>
  public const string BLOCKED_SQL_STATE = "WH980";

  /// <summary>Builds the rewrite for one enum column.</summary>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="table">The table, unquoted.</param>
  /// <param name="column">The column, unquoted.</param>
  /// <param name="enumName">The enumeration's name, for the message.</param>
  /// <param name="columnType">The column's numeric type (<c>INTEGER</c>, <c>SMALLINT</c>, <c>BIGINT</c>, <c>NUMERIC</c>).</param>
  /// <param name="members">Each member's name and underlying value.</param>
  public static string Build(
      string schema, string table, string column, string enumName, string columnType,
      IReadOnlyList<(string Name, string Value)> members) =>
    _build(schema, table, column, enumName, columnType, members, flags: false);

  /// <summary>
  /// Builds the rewrite for one column of a <c>[Flags]</c> enumeration. Besides a single name, a value may be a
  /// combination in the form .NET writes it (<c>"A, B"</c>), which becomes the bitwise OR of the named members'
  /// values. Only a component that is not a member name blocks the conversion.
  /// </summary>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="table">The table, unquoted.</param>
  /// <param name="column">The column, unquoted.</param>
  /// <param name="enumName">The enumeration's name, for the message.</param>
  /// <param name="columnType">The column's numeric type (<c>INTEGER</c>, <c>SMALLINT</c>, <c>BIGINT</c>, <c>NUMERIC</c>).</param>
  /// <param name="members">Each member's name and underlying value.</param>
  public static string BuildFlags(
      string schema, string table, string column, string enumName, string columnType,
      IReadOnlyList<(string Name, string Value)> members) =>
    _build(schema, table, column, enumName, columnType, members, flags: true);

  private static string _build(
      string schema, string table, string column, string enumName, string columnType,
      IReadOnlyList<(string Name, string Value)> members, bool flags) {
    _ensureIdentifier(schema, nameof(schema));
    _ensureIdentifier(table, nameof(table));
    _ensureIdentifier(column, nameof(column));
    _ensureIdentifier(enumName, nameof(enumName));
    _ensureIdentifier(columnType, nameof(columnType));
    ArgumentNullException.ThrowIfNull(members);
    foreach (var (name, value) in members) {
      _ensureIdentifier(name, nameof(members));
      if (value.Length == 0 || !value.TrimStart('-').All(char.IsAsciiDigit)) {
        throw new ArgumentException($"'{value}' is not an integral enum value.", nameof(members));
      }
    }

    var qualified = $"\"{schema}\".\"{table}\"";
    var (unreadable, conversion, expected) = flags
      ? _flagsForm(column, columnType, members)
      : _namesForm(column, columnType, members);

    return $"""
      DO $wh_enum$
      DECLARE wh_bad text;
      BEGIN
        IF EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = '{schema}' AND table_name = '{table}' AND column_name = '{column}'
                     AND data_type IN ('text', 'character varying')) THEN
          SELECT string_agg(v, ', ') INTO wh_bad FROM (
            SELECT DISTINCT {column} AS v FROM {qualified}
            WHERE {column} IS NOT NULL AND {column} !~ '^-?[0-9]+$' AND {unreadable}
            ORDER BY 1 LIMIT 10) wh_sample;
          IF wh_bad IS NOT NULL THEN
            RAISE EXCEPTION USING ERRCODE = '{BLOCKED_SQL_STATE}', MESSAGE = format(
              'Column %s of %s holds values that are neither {expected} of %s nor a number, so it cannot be converted to the number the enumeration is now stored as: %s. Correct or clear those values, then restart.',
              '{column}', '{schema}.{table}', '{enumName}', wh_bad);
          END IF;
          BEGIN
            ALTER TABLE {qualified} ALTER COLUMN {column} TYPE {columnType}
              USING ({conversion});
          EXCEPTION WHEN OTHERS THEN
            RAISE EXCEPTION USING ERRCODE = '{BLOCKED_SQL_STATE}', MESSAGE = format(
              'Column %s of %s could not be converted to %s: %s', '{column}', '{schema}.{table}', '{columnType}', SQLERRM);
          END;
          RAISE NOTICE '%: column % converted from enumeration names to numbers', '{table}', '{column}';
        END IF;
      END $wh_enum$;
      """;
  }

  // A plain enumeration: a value is one member name, mapped to its number by a CASE over the names.
  private static (string Unreadable, string Conversion, string Expected) _namesForm(
      string column, string columnType, IReadOnlyList<(string Name, string Value)> members) {
    var names = members.Count == 0 ? "NULL" : string.Join(", ", members.Select(m => $"'{m.Name}'"));
    var arms = new StringBuilder();
    foreach (var (name, value) in members) {
      arms.Append("WHEN '").Append(name).Append("' THEN ").Append(value).Append(' ');
    }
    return ($"{column} NOT IN ({names})", $"CASE {column} {arms}ELSE {column}::{columnType} END", "a member name");
  }

  // A [Flags] enumeration: a value is one or more member names separated by commas (.NET writes "A, B"), and
  // becomes the bitwise OR of their values. A member name never contains a space, so the spaces are dropped
  // before the value is split. The OR is taken in bigint; a numeric (ulong-backed) column carries its members as
  // their two's-complement bigint and the result is moved back into the unsigned range. Each value is
  // parenthesized before the cast: `-9223372036854775808::bigint` casts the positive literal first and overflows. ALTER ... USING cannot
  // hold a subquery, so each member is tested against the split value in turn rather than joined.
  private static (string Unreadable, string Conversion, string Expected) _flagsForm(
      string column, string columnType, IReadOnlyList<(string Name, string Value)> members) {
    var components = $"string_to_array(replace({column}, ' ', ''), ',')";
    var names = $"ARRAY[{string.Join(", ", members.Select(m => $"'{m.Name}'"))}]::text[]";
    var numeric = string.Equals(columnType, "NUMERIC", StringComparison.OrdinalIgnoreCase);
    var or = new StringBuilder("(0::bigint");
    foreach (var (name, value) in members) {
      var bits = numeric
        ? unchecked((long)ulong.Parse(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)
        : value;
      or.Append(" | (CASE WHEN '").Append(name).Append("' = ANY(").Append(components).Append(") THEN ")
        .Append('(').Append(bits).Append(")::bigint ELSE 0::bigint END)");
    }
    or.Append(')');
    var combined = numeric
      ? $"({or}::numeric + CASE WHEN {or} < 0 THEN 18446744073709551616 ELSE 0 END)"
      : $"{or}::{columnType}";
    var conversion =
      $"CASE WHEN {column} IS NULL THEN NULL WHEN {column} ~ '^-?[0-9]+$' THEN {column}::{columnType} ELSE {combined} END";
    return ($"NOT ({components} <@ {names})", conversion, "member names (alone or combined)");
  }

  // Everything here is compile-time model metadata, but it is embedded in SQL, so anything that is not a plain
  // identifier is refused rather than trusted.
  private static void _ensureIdentifier(string value, string parameter) {
    if (string.IsNullOrWhiteSpace(value) || !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) {
      throw new ArgumentException($"'{value}' is not a plain identifier.", parameter);
    }
  }
}

/// <summary>
/// A stored-format rewrite found values it cannot convert. Thrown by <see cref="CanonicalTemporalRewritePhase"/>
/// after it has committed every rewrite that could run, and not caught by the schema initializer, so startup stops
/// with the table, the column and the offending values instead of running on a column it cannot read.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#enum-text-columns</docs>
public sealed class StoredFormConversionBlockedException : Exception {
  /// <summary>Creates the exception from each blocked rewrite's message.</summary>
  public StoredFormConversionBlockedException(IReadOnlyList<string> failures)
    : base("The stored-format rewrite could not convert every column, so startup is stopped. " + string.Join(" ", failures)) {
    Failures = failures;
  }

  /// <summary>Creates the exception with a message.</summary>
  public StoredFormConversionBlockedException(string message) : base(message) {
    Failures = [message];
  }

  /// <summary>Creates the exception with a message and a cause.</summary>
  public StoredFormConversionBlockedException(string message, Exception innerException) : base(message, innerException) {
    Failures = [message];
  }

  /// <summary>Creates the exception with no detail.</summary>
  public StoredFormConversionBlockedException() : this("The stored-format rewrite could not convert every column.") { }

  /// <summary>Each blocked rewrite's message.</summary>
  public IReadOnlyList<string> Failures { get; }
}
