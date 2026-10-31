// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// One canonical spelling for a PostgreSQL column type, so a type read back from the database and the same type
/// written in DDL compare equal. The canonical spelling is the one <c>format_type(atttypid, atttypmod)</c>
/// reports: <c>TIMESTAMPTZ</c> is <c>timestamp with time zone</c>, <c>VARCHAR(100)</c> is
/// <c>character varying(100)</c>, <c>INT</c> is <c>integer</c>, <c>BOOL</c> is <c>boolean</c>.
/// </summary>
/// <remarks>
/// A length, precision or scale is kept (<c>VARCHAR(100)</c> and <c>VARCHAR(200)</c> differ), an array of any
/// declared dimensions is one <c>[]</c> as PostgreSQL stores it, a schema qualifier and identifier quotes are
/// dropped, and a type with no alias here (<c>uuid</c>, <c>jsonb</c>, <c>vector(3)</c>) is lowercased with its
/// whitespace collapsed. Canonicalizing a canonical spelling returns it unchanged.
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresColumnTypeTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresSchemaInitializerTypeCompareTests.cs</tests>
internal static class PostgresColumnType {
  private const string WITH_TIME_ZONE = " with time zone";
  private const string WITHOUT_TIME_ZONE = " without time zone";
  private const string INTEGER = "integer";
  private const string BIGINT = "bigint";
  private const string SMALLINT = "smallint";
  private const string TIMESTAMP = "timestamp";
  private const string TIME = "time";

  // Each alias maps to the head of its canonical spelling and the words after a modifier. A modifier sits
  // between the two, which only matters for the time types: TIMESTAMPTZ(3) is "timestamp(3) with time zone".
  private static readonly Dictionary<string, (string Head, string Tail)> _aliases = new(StringComparer.Ordinal) {
    ["int"] = (INTEGER, ""),
    ["int4"] = (INTEGER, ""),
    ["serial"] = (INTEGER, ""),
    ["serial4"] = (INTEGER, ""),
    ["int8"] = (BIGINT, ""),
    ["bigserial"] = (BIGINT, ""),
    ["serial8"] = (BIGINT, ""),
    ["int2"] = (SMALLINT, ""),
    ["smallserial"] = (SMALLINT, ""),
    ["serial2"] = (SMALLINT, ""),
    ["decimal"] = ("numeric", ""),
    ["float"] = ("double precision", ""),
    ["float8"] = ("double precision", ""),
    ["float4"] = ("real", ""),
    ["bool"] = ("boolean", ""),
    ["varchar"] = ("character varying", ""),
    ["char"] = ("character", ""),
    ["bpchar"] = ("character", ""),
    ["timestamptz"] = (TIMESTAMP, WITH_TIME_ZONE),
    ["timestamp with time zone"] = (TIMESTAMP, WITH_TIME_ZONE),
    ["timestamp"] = (TIMESTAMP, WITHOUT_TIME_ZONE),
    ["timestamp without time zone"] = (TIMESTAMP, WITHOUT_TIME_ZONE),
    ["timetz"] = (TIME, WITH_TIME_ZONE),
    ["time with time zone"] = (TIME, WITH_TIME_ZONE),
    ["time"] = (TIME, WITHOUT_TIME_ZONE),
    ["time without time zone"] = (TIME, WITHOUT_TIME_ZONE),
  };

  /// <summary>
  /// The canonical spelling of <paramref name="typeName"/>, a column type as DDL writes it or as
  /// <c>format_type</c> reports it.
  /// </summary>
  /// <param name="typeName">The type alone, without the column name or constraints.</param>
  /// <returns>The <c>format_type</c> spelling of the same type.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresColumnTypeTests.cs:Canonicalize_AnAliasOrItsLongForm_IsTheFormatTypeSpellingAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresColumnTypeTests.cs:Canonicalize_DifferentTypes_StayDifferentAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresColumnTypeTests.cs:Canonicalize_IsIdempotentAsync</tests>
  public static string Canonicalize(string typeName) {
    var text = string.Join(' ', typeName.Replace("\"", "").ToLowerInvariant()
      .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    // PostgreSQL ignores declared array dimensions and sizes: INT[][] and INTEGER[3] are both integer[].
    var bracket = text.IndexOf('[');
    var isArray = bracket >= 0;
    if (isArray) {
      text = text[..bracket].TrimEnd();
    }

    string? modifier = null;
    var open = text.IndexOf('(');
    if (open >= 0) {
      var close = text.IndexOf(')', open);
      if (close < 0) {
        close = text.Length;
      }
      modifier = text[(open + 1)..close].Replace(" ", "");
      text = $"{text[..open].TrimEnd()} {text[Math.Min(close + 1, text.Length)..].TrimStart()}".Trim();
    }

    text = text[(text.LastIndexOf('.') + 1)..];

    var (head, tail) = _aliases.TryGetValue(text, out var alias) ? alias : (text, "");

    // CHAR with no length is character(1), which is what format_type reports for the column it creates.
    if (modifier is null && head == "character") {
      modifier = "1";
    }

    var modifierText = modifier is null ? "" : $"({modifier})";
    return $"{head}{modifierText}{tail}{(isArray ? "[]" : "")}";
  }
}
