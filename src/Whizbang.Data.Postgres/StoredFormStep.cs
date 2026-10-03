using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The JSON number a stored value is converted to, which decides the range a value must fall in and whether it
/// must be whole.
/// </summary>
/// <docs>fundamentals/perspectives/stored-form-migrations#type-changes</docs>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
  Justification = "Each member is the CLR number a stored value is converted to, and the CLR name is the one an "
    + "author reads in the generated code and in the journal's migration names.")]
public enum StoredNumber {
  /// <summary>A signed byte.</summary>
  SByte,

  /// <summary>An unsigned byte.</summary>
  Byte,

  /// <summary>A 16-bit integer.</summary>
  Int16,

  /// <summary>An unsigned 16-bit integer.</summary>
  UInt16,

  /// <summary>A 32-bit integer.</summary>
  Int32,

  /// <summary>An unsigned 32-bit integer.</summary>
  UInt32,

  /// <summary>A 64-bit integer.</summary>
  Int64,

  /// <summary>An unsigned 64-bit integer.</summary>
  UInt64,

  /// <summary>A decimal.</summary>
  Decimal,

  /// <summary>A single-precision float.</summary>
  Single,

  /// <summary>A double-precision float.</summary>
  Double,
}

/// <summary>
/// One idempotent change a stored-form migration makes to a perspective table: a conversion of the values at one
/// path of the <c>data</c> document still in an old form, a move, a removal or a default, or a change to a physical
/// column. Built by the generated code from an app's declarations, and assembled into one statement by
/// <see cref="StoredFormMigrationSql.Generated"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every step touches only what is still in the old form, so running it again changes nothing. A conversion that
/// finds a value it cannot convert samples up to ten such values and raises
/// <see cref="EnumColumnRewriteSql.BLOCKED_SQL_STATE"/> before it changes anything, naming the migration, the table,
/// the path and the values.
/// </para>
/// <para>
/// A path is the property's key from the document root, nested keys separated by dots (<c>Shipping.Line1</c>).
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations#generated-cases</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StoredFormMigrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StoredFormMigrationSqlTests.cs</tests>
public sealed class StoredFormStep {
  // A number as JSON or as text renders it. Anything else is not a number, whatever it looks like.
  private const string NUMERIC_TEXT = @"^-?[0-9]+(\.[0-9]+)?([eE][-+]?[0-9]+)?$";
  private const string WHOLE_TEXT = "^-?[0-9]+$";
  private const string END_IF = "  END IF;\n";

  private readonly Func<StepContext, string> _render;

  private StoredFormStep(Func<StepContext, string> render, (string Name, string CreateStatement)? rebuild = null) {
    _render = render;
    Rebuild = rebuild;
  }

  internal string Render(StepContext context) => _render(context);

  /// <summary>The index this step drops when it is stale, and the statement that builds it for the new type.</summary>
  internal (string Name, string CreateStatement)? Rebuild { get; }

  /// <summary>A number or a boolean at <paramref name="path"/> becomes its text; a string is left alone.</summary>
  /// <param name="path">The property's path in the document.</param>
  public static StoredFormStep ToText(string path) {
    var p = DocumentPath.Parse(path);
    return new StoredFormStep(c => _update(c, $"jsonb_set(data, {p.Array}, to_jsonb(data #>> {p.Array}), false)",
      $"jsonb_typeof(data #> {p.Array}) IN ('number', 'boolean')"));
  }

  /// <summary>
  /// A number at <paramref name="path"/> becomes the name of the enum member that has it, or the number's text when
  /// no single member has it; a string is left alone.
  /// </summary>
  /// <param name="path">The property's path in the document.</param>
  /// <param name="members">The former enum's members: each name and underlying value.</param>
  public static StoredFormStep EnumNumberToName(string path, IReadOnlyList<(string Name, string Value)> members) {
    var p = DocumentPath.Parse(path);
    var arms = _members(members);
    var mapped = new StringBuilder($"CASE data #>> {p.Array} ");
    foreach (var (name, value) in arms) {
      mapped.Append(CultureInfo.InvariantCulture, $"WHEN {SqlText.Literal(value)} THEN {SqlText.Literal(name)} ");
    }
    mapped.Append(CultureInfo.InvariantCulture, $"ELSE data #>> {p.Array} END");
    return new StoredFormStep(c => _update(c, $"jsonb_set(data, {p.Array}, to_jsonb({mapped}), false)",
      $"jsonb_typeof(data #> {p.Array}) = 'number'"));
  }

  /// <summary>
  /// A numeric string at <paramref name="path"/> becomes a number, and for an integral target a whole number written
  /// with a fraction (<c>5.0</c>) becomes whole. A string that is not a number, a fraction where a whole number is
  /// needed, or a value outside <paramref name="number"/>'s range blocks.
  /// </summary>
  /// <param name="path">The property's path in the document.</param>
  /// <param name="number">The number the property now is.</param>
  public static StoredFormStep ToNumber(string path, StoredNumber number) {
    var p = DocumentPath.Parse(path);
    var (min, max, whole) = _range(number);
    var value = $"(CASE WHEN jsonb_typeof(data #> {p.Array}) IN ('string', 'number') AND data #>> {p.Array} ~ '{NUMERIC_TEXT}' THEN (data #>> {p.Array})::numeric END)";
    var bad = $"jsonb_typeof(data #> {p.Array}) IN ('string', 'number') AND ({value} IS NULL OR {value} < {min} OR {value} > {max}"
      + (whole ? $" OR {value} <> trunc({value}))" : ")");
    var candidate = whole
      ? $"jsonb_typeof(data #> {p.Array}) = 'string' OR (jsonb_typeof(data #> {p.Array}) = 'number' AND data #>> {p.Array} !~ '{WHOLE_TEXT}')"
      : $"jsonb_typeof(data #> {p.Array}) = 'string'";
    var converted = whole ? $"trunc((data #>> {p.Array})::numeric)" : $"(data #>> {p.Array})::numeric";
    return new StoredFormStep(c =>
      _blockOnDocument(c, p, bad, number.ToString()) + _update(c, $"jsonb_set(data, {p.Array}, to_jsonb({converted}), false)", candidate));
  }

  /// <summary>
  /// A string at <paramref name="path"/> naming a member of <paramref name="enumName"/> becomes the member's number,
  /// and a numeric string its number; for a <c>[Flags]</c> enum, combined names (<c>"A, B"</c>) become the bitwise
  /// OR of their values. A name that is not a member (names match exactly), or a number outside the underlying
  /// type's range, blocks.
  /// </summary>
  /// <param name="path">The property's path in the document.</param>
  /// <param name="enumName">The enum's name, for the message.</param>
  /// <param name="underlying">The enum's underlying number.</param>
  /// <param name="members">The enum's members: each name and underlying value.</param>
  /// <param name="flags">Whether the enum is marked <c>[Flags]</c>.</param>
  public static StoredFormStep ToEnumNumber(
      string path, string enumName, StoredNumber underlying, IReadOnlyList<(string Name, string Value)> members, bool flags) {
    var p = DocumentPath.Parse(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(enumName);
    var arms = _members(members);
    var (min, max, _) = _range(underlying);
    var text = $"data #>> {p.Array}";
    var names = "ARRAY[" + string.Join(", ", arms.Select(m => SqlText.Literal(m.Name))) + "]::text[]";
    var numberInRange = $"(CASE WHEN {text} ~ '{WHOLE_TEXT}' THEN ({text})::numeric BETWEEN {min} AND {max} ELSE false END)";
    string known;
    string lookup;
    if (flags && arms.Exists(m => !long.TryParse(m.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))) {
      throw new ArgumentException($"A [Flags] enum's values must fit the bigint a combination is computed in: {enumName}.", nameof(members));
    }
    if (flags) {
      var components = $"string_to_array(replace({text}, ' ', ''), ',')";
      known = $"({text} <> '' AND {components} <@ {names})";
      var values = arms.Count == 0
        ? "(SELECT NULL::text, NULL::bigint WHERE false)"
        : "(VALUES " + string.Join(", ", arms.Select(m => $"({SqlText.Literal(m.Name)}, {long.Parse(m.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}::bigint)")) + ")";
      lookup = $"(SELECT coalesce(bit_or(wh_m.v), 0) FROM unnest({components}) AS wh_c(n) JOIN {values} AS wh_m(n, v) ON wh_m.n = wh_c.n)";
    } else {
      known = $"{text} = ANY({names})";
      var arm = new StringBuilder($"CASE {text} ");
      foreach (var (name, value) in arms) {
        arm.Append(CultureInfo.InvariantCulture, $"WHEN {SqlText.Literal(name)} THEN {value}::numeric ");
      }
      lookup = arm.Append("ELSE NULL::numeric END").ToString();
    }
    var bad = $"jsonb_typeof(data #> {p.Array}) = 'string' AND NOT {numberInRange} AND NOT {known}";
    var converted = $"CASE WHEN {text} ~ '{WHOLE_TEXT}' THEN to_jsonb(({text})::numeric) ELSE to_jsonb({lookup}) END";
    return new StoredFormStep(c =>
      _blockOnDocument(c, p, bad, enumName) + _update(c, $"jsonb_set(data, {p.Array}, {converted}, false)",
        $"jsonb_typeof(data #> {p.Array}) = 'string'"));
  }

  /// <summary>
  /// The value under <paramref name="previousName"/>, beside <paramref name="path"/>'s key, moves to
  /// <paramref name="path"/>. When a document holds both, the value at <paramref name="path"/> wins and the old key
  /// is dropped.
  /// </summary>
  /// <param name="path">The property's current path in the document.</param>
  /// <param name="previousName">The property's former key, under the same parent.</param>
  public static StoredFormStep Rename(string path, string previousName) {
    var to = DocumentPath.Parse(path);
    var from = to.Sibling(previousName);
    return new StoredFormStep(c => _update(c,
      $"CASE WHEN data #> {to.Array} IS NULL THEN jsonb_set(data #- {from.Array}, {to.Array}, data #> {from.Array}, true) ELSE data #- {from.Array} END",
      $"data #> {from.Array} IS NOT NULL"));
  }

  /// <summary>The key at <paramref name="path"/> is dropped from every document that has it.</summary>
  /// <param name="path">The removed property's path in the document.</param>
  public static StoredFormStep Remove(string path) {
    var p = DocumentPath.Parse(path);
    return new StoredFormStep(c => _update(c, $"data #- {p.Array}", $"data #> {p.Array} IS NOT NULL"));
  }

  /// <summary>
  /// A document whose parent object at <paramref name="path"/> exists but has no key for it gets
  /// <paramref name="json"/>. A key holding <c>null</c> is left alone.
  /// </summary>
  /// <param name="path">The property's path in the document.</param>
  /// <param name="json">The default, as JSON (<c>0</c>, <c>"text"</c>, <c>true</c>).</param>
  public static StoredFormStep DefaultWhenMissing(string path, string json) {
    var p = DocumentPath.Parse(path);
    ArgumentNullException.ThrowIfNull(json);
    try {
      using var parsed = JsonDocument.Parse(json);
    } catch (JsonException ex) {
      throw new ArgumentException($"'{json}' is not a JSON value.", nameof(json), ex);
    }
    return new StoredFormStep(c => _update(c,
      $"jsonb_set(data, {p.Array}, {SqlText.Literal(json)}::jsonb, true)",
      $"jsonb_typeof(data #> {p.Parent.Array}) = 'object' AND NOT ((data #> {p.Parent.Array}) ? {SqlText.Literal(p.Leaf)})"));
  }

  /// <summary>
  /// A physical column is retyped in place to <paramref name="columnType"/> when its current type differs. To a
  /// number: every value must be one in <paramref name="number"/>'s range (whole for an integral one), or the step
  /// blocks naming the column. To text: a number becomes its text, or with <paramref name="enumNames"/> the name of
  /// the enum member that has it.
  /// </summary>
  /// <param name="column">The column, unquoted.</param>
  /// <param name="columnType">The column's new type, as the schema declares it (<c>TEXT</c>, <c>BIGINT</c>).</param>
  /// <param name="number">The number the column now holds, or <see langword="null"/> for text.</param>
  /// <param name="enumNames">For a former enum column retyped to text, each member's name and value.</param>
  public static StoredFormStep RetypeColumn(
      string column, string columnType, StoredNumber? number, IReadOnlyList<(string Name, string Value)>? enumNames = null) {
    SqlText.EnsureIdentifier(column, nameof(column));
    if (string.IsNullOrWhiteSpace(columnType) || !columnType.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '(' or ')' or ',' or '_')) {
      throw new ArgumentException($"'{columnType}' is not a column type.", nameof(columnType));
    }
    var col = SqlText.Identifier(column);
    string conversion;
    string? bad = null;
    if (number is { } n) {
      var (min, max, whole) = _range(n);
      var asNumber = $"({col}::text)::numeric";
      bad = $"NOT (CASE WHEN {col}::text ~ '{NUMERIC_TEXT}' THEN {asNumber} BETWEEN {min} AND {max}"
        + (whole ? $" AND {asNumber} = trunc({asNumber})" : "") + " ELSE false END)";
      conversion = whole ? $"trunc({asNumber})::{columnType}" : $"{asNumber}::{columnType}";
    } else if (enumNames is not null) {
      var arms = new StringBuilder($"CASE {col}::text ");
      foreach (var (name, value) in _members(enumNames)) {
        arms.Append(CultureInfo.InvariantCulture, $"WHEN {SqlText.Literal(value)} THEN {SqlText.Literal(name)} ");
      }
      conversion = arms.Append(CultureInfo.InvariantCulture, $"ELSE {col}::text END").ToString();
    } else {
      conversion = $"{col}::text";
    }
    return new StoredFormStep(c => {
      var sb = new StringBuilder();
      sb.Append(CultureInfo.InvariantCulture,
        $"  IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = {SqlText.Literal(c.QualifiedTable)}::regclass AND attname = {SqlText.Literal(column)}\n");
      sb.Append(CultureInfo.InvariantCulture,
        $"             AND NOT attisdropped AND atttypid <> {SqlText.Literal(columnType)}::regtype) THEN\n");
      if (bad is not null) {
        sb.Append(CultureInfo.InvariantCulture,
          $"    SELECT string_agg(wh_v, ', ') INTO v_bad FROM (SELECT DISTINCT {col}::text AS wh_v FROM {c.QualifiedTable}\n");
        sb.Append(CultureInfo.InvariantCulture, $"      WHERE {col} IS NOT NULL AND {bad} ORDER BY 1 LIMIT 10) wh_sample;\n");
        sb.Append(_raiseBlocked(c, $"column {column}", number!.Value.ToString()));
      }
      sb.Append(CultureInfo.InvariantCulture, $"    SELECT count(*) INTO v_count FROM {c.QualifiedTable} WHERE {col} IS NOT NULL;\n");
      sb.Append(CultureInfo.InvariantCulture, $"    ALTER TABLE {c.QualifiedTable} ALTER COLUMN {col} TYPE {columnType} USING ({conversion});\n");
      sb.Append("    v_touched := v_touched + v_count;\n");
      sb.Append("    v_changed := true;\n");
      sb.Append(END_IF);
      return sb.ToString();
    });
  }

  /// <summary>
  /// A physical column is renamed from <paramref name="from"/> to <paramref name="to"/> when the old column exists
  /// and the new one does not.
  /// </summary>
  /// <param name="from">The former column, unquoted.</param>
  /// <param name="to">The column now, unquoted.</param>
  public static StoredFormStep RenameColumn(string from, string to) {
    SqlText.EnsureIdentifier(from, nameof(from));
    SqlText.EnsureIdentifier(to, nameof(to));
    return new StoredFormStep(c => {
      string exists(string column) =>
        $"EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = {SqlText.Literal(c.QualifiedTable)}::regclass AND attname = {SqlText.Literal(column)} AND NOT attisdropped)";
      return $"  IF {exists(from)} AND NOT {exists(to)} THEN\n"
        + $"    ALTER TABLE {c.QualifiedTable} RENAME COLUMN {SqlText.Identifier(from)} TO {SqlText.Identifier(to)};\n"
        + "    v_changed := true;\n"
        + END_IF;
    });
  }

  /// <summary>
  /// Drops the index named <paramref name="indexName"/> when it casts the extraction of <paramref name="path"/> to a
  /// type other than <paramref name="storeType"/>, the one the property's values now take, so that neither the
  /// conversion nor a later write is refused by an index that casts to the old type. The index is built again for the
  /// new type with <paramref name="createStatement"/>, concurrently, once the pass has committed
  /// (<see cref="StoredFormIndexRebuild"/>).
  /// </summary>
  /// <remarks>
  /// Only an index the schema built is touched, found the way the schema finds its own: by the name it gave it, and by
  /// a definition over the key's extraction. An index under any other name, an index under that name over anything
  /// else, and a constraint's index are left alone. The step goes before the conversion, because an index over the
  /// old cast refuses the converted values (an enum's number cast from its new name, say).
  /// </remarks>
  /// <param name="path">The property's key at the document root.</param>
  /// <param name="storeType">The type the index casts the extraction to now (<c>integer</c>), or <see langword="null"/> for text.</param>
  /// <param name="indexName">The index's name, as the schema derives it.</param>
  /// <param name="createStatement">The schema's <c>CREATE INDEX IF NOT EXISTS</c> statement for the index.</param>
  public static StoredFormStep ReplaceIndex(string path, string? storeType, string indexName, string createStatement) {
    var p = DocumentPath.Parse(path);
    if (p.Segments.Count != 1) {
      throw new ArgumentException($"'{path}' is not a key at the document root, the only place an index extracts from.", nameof(path));
    }
    if (storeType is not null && (storeType.Length == 0 || !storeType.All(ch => ch is (>= 'a' and <= 'z') or ' '))) {
      throw new ArgumentException($"'{storeType}' is not a type an index casts to.", nameof(storeType));
    }
    ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
    ArgumentException.ThrowIfNullOrWhiteSpace(createStatement);
    if (StoredFormIndexRebuild.Concurrently(createStatement) is null) {
      throw new ArgumentException("The statement is not a CREATE [UNIQUE] INDEX IF NOT EXISTS statement.", nameof(createStatement));
    }

    var extraction = $"(data ->> '{p.Leaf}'::text)";
    var cast = "\\(data ->> '" + p.Leaf + "'::text\\)\\)::([a-z][a-z ]*[a-z])";
    var expected = storeType is null ? "NULL" : SqlText.Literal(storeType);
    return new StoredFormStep(c =>
      "  SELECT i.indexrelid::regclass::text, pg_get_indexdef(i.indexrelid) INTO v_index, v_definition\n"
      + "  FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid\n"
      + $"  WHERE i.indrelid = {SqlText.Literal(c.QualifiedTable)}::regclass AND ic.relname = left({SqlText.Literal(indexName)}, 63)\n"
      + "    AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid);\n"
      + $"  IF strpos(v_definition, {SqlText.Literal(extraction)}) > 0\n"
      + $"     AND substring(v_definition from {SqlText.Literal(cast)}) IS DISTINCT FROM {expected} THEN\n"
      + "    EXECUTE 'DROP INDEX ' || v_index;\n"
      + "    RAISE NOTICE USING MESSAGE = format('%s: stored-form migration %s: dropped index %s, which casts %s to another type; it is built again for the new type',\n"
      + $"      {SqlText.Literal(c.TableName)}, {SqlText.Literal(c.MigrationName)}, v_index, {SqlText.Literal(p.JsonPath)});\n"
      + END_IF,
      (indexName, createStatement));
  }

  /// <summary>
  /// Brings forward the retries of the streams of <paramref name="perspectiveNames"/> parked on a stored document no
  /// reader could take, once the migration has changed something: their failed work rows fall due now instead of at
  /// the end of their backoff. List it last, after the steps that convert.
  /// </summary>
  /// <remarks>
  /// A parked row is one the perspective worker reported as a serialization failure: the failed flag set, the reason
  /// <see cref="Whizbang.Core.Messaging.MessageFailureReason.SerializationError"/>, no lease, and a retry scheduled
  /// in the future. Its failure count is kept, so a stream the migration did not fix still reaches the dead-letter
  /// threshold. Nothing happens on a pass that changed nothing, or where the work table does not exist.
  /// </remarks>
  /// <param name="perspectiveNames">The perspectives that store into the migration's table, by their CLR type names.</param>
  public static StoredFormStep RetryParkedStreams(params string[] perspectiveNames) {
    ArgumentNullException.ThrowIfNull(perspectiveNames);
    if (perspectiveNames.Length == 0 || Array.Exists(perspectiveNames, string.IsNullOrWhiteSpace)) {
      throw new ArgumentException("Name at least one perspective, and no blank name.", nameof(perspectiveNames));
    }
    var names = "ARRAY[" + string.Join(", ", perspectiveNames.Select(SqlText.Literal)) + "]::text[]";
    const int FAILED = (int)Whizbang.Core.Messaging.MessageProcessingStatus.Failed;
    const int UNREADABLE = (int)Whizbang.Core.Messaging.MessageFailureReason.SerializationError;
    return new StoredFormStep(c => {
      var work = $"{SqlText.Identifier(c.Schema)}.wh_perspective_events";
      return $"  IF v_changed AND to_regclass({SqlText.Literal(work)}) IS NOT NULL THEN\n"
        + $"    UPDATE {work} SET scheduled_for = NOW()\n"
        + $"    WHERE perspective_name = ANY({names}) AND (status & {FAILED}) <> 0 AND failure_reason = {UNREADABLE}\n"
        + "      AND instance_id IS NULL AND scheduled_for > NOW();\n"
        + "    GET DIAGNOSTICS v_count = ROW_COUNT;\n"
        + "    IF v_count > 0 THEN\n"
        + "      RAISE NOTICE USING MESSAGE = format('%s: stored-form migration %s: brought forward the retries of %s parked row(s)',\n"
        + $"        {SqlText.Literal(c.TableName)}, {SqlText.Literal(c.MigrationName)}, v_count);\n"
        + "    END IF;\n"
        + END_IF;
    });
  }

  // An UPDATE of the rows still in the old form, counted.
  private static string _update(StepContext c, string newData, string candidate) =>
    $"  UPDATE {c.QualifiedTable} SET data = {newData}\n"
    + $"  WHERE {candidate};\n"
    + "  GET DIAGNOSTICS v_count = ROW_COUNT;\n"
    + "  v_touched := v_touched + v_count;\n"
    + "  v_changed := v_changed OR v_count > 0;\n";

  // Samples the values that cannot be converted and stops before anything changes.
  private static string _blockOnDocument(StepContext c, DocumentPath p, string bad, string target) =>
    $"  SELECT string_agg(wh_v, ', ') INTO v_bad FROM (SELECT DISTINCT (data #> {p.Array})::text AS wh_v FROM {c.QualifiedTable}\n"
    + $"    WHERE {bad} ORDER BY 1 LIMIT 10) wh_sample;\n"
    + _raiseBlocked(c, p.JsonPath, target);

  private static string _raiseBlocked(StepContext c, string where, string target) =>
    "  IF v_bad IS NOT NULL THEN\n"
    + $"    RAISE EXCEPTION USING ERRCODE = '{EnumColumnRewriteSql.BLOCKED_SQL_STATE}', MESSAGE = format(\n"
    + "      'Stored-form migration %s cannot convert %s at %s: %s. Those values cannot be read as %s. '\n"
    + "      'Correct or clear them, or declare a custom migration that does, then restart.',\n"
    + $"      {SqlText.Literal(c.MigrationName)}, {SqlText.Literal(c.DisplayTable)}, {SqlText.Literal(where)}, v_bad, {SqlText.Literal(target)});\n"
    + END_IF;

  private static List<(string Name, string Value)> _members(IReadOnlyList<(string Name, string Value)> members) {
    ArgumentNullException.ThrowIfNull(members);
    foreach (var (name, value) in members) {
      SqlText.EnsureIdentifier(name, nameof(members));
      if (value.Length == 0 || !value.TrimStart('-').All(char.IsAsciiDigit)) {
        throw new ArgumentException($"'{value}' is not an integral enum value.", nameof(members));
      }
    }
    return [.. members];
  }

  // The range a value of each number must fall in, and whether it must be whole. The float bounds are the largest
  // finite values; a larger one would be read as infinity or refused.
  private static (string Min, string Max, bool Whole) _range(StoredNumber number) => number switch {
    StoredNumber.SByte => ("-128", "127", true),
    StoredNumber.Byte => ("0", "255", true),
    StoredNumber.Int16 => ("-32768", "32767", true),
    StoredNumber.UInt16 => ("0", "65535", true),
    StoredNumber.Int32 => ("-2147483648", "2147483647", true),
    StoredNumber.UInt32 => ("0", "4294967295", true),
    StoredNumber.Int64 => ("-9223372036854775808", "9223372036854775807", true),
    StoredNumber.UInt64 => ("0", "18446744073709551615", true),
    StoredNumber.Decimal => ("-79228162514264337593543950335", "79228162514264337593543950335", false),
    StoredNumber.Single => ("-3.4028234663852886E+38", "3.4028234663852886E+38", false),
    StoredNumber.Double => ("-1.7976931348623157E+308", "1.7976931348623157E+308", false),
    _ => throw new ArgumentOutOfRangeException(nameof(number), number, "Not a stored number."),
  };
}

/// <summary>What a step needs to know about the migration it is part of.</summary>
internal readonly record struct StepContext(string MigrationName, string Schema, string TableName) {
  /// <summary>The schema-qualified table, each part quoted.</summary>
  public string QualifiedTable => $"{SqlText.Identifier(Schema)}.{SqlText.Identifier(TableName)}";

  /// <summary>The table as a message names it: <c>schema.table</c>.</summary>
  public string DisplayTable => $"{Schema}.{TableName}";
}

/// <summary>A path into the <c>data</c> document: its keys from the root.</summary>
internal readonly record struct DocumentPath(IReadOnlyList<string> Segments) {
  public static DocumentPath Parse(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    var segments = path.Split('.');
    foreach (var segment in segments) {
      SqlText.EnsureIdentifier(segment, nameof(path));
    }
    return new DocumentPath(segments);
  }

  /// <summary>The path as a PostgreSQL text array, for <c>#&gt;</c>, <c>#-</c> and <c>jsonb_set</c>.</summary>
  public string Array => "ARRAY[" + string.Join(", ", Segments.Select(SqlText.Literal)) + "]::text[]";

  /// <summary>The path as it is named in a message: <c>$.Shipping.Line1</c>.</summary>
  public string JsonPath => "$." + string.Join(".", Segments);

  public string Leaf => Segments[^1];

  public DocumentPath Parent => new([.. Segments.Take(Segments.Count - 1)]);

  public DocumentPath Sibling(string name) {
    SqlText.EnsureIdentifier(name, nameof(name));
    return new DocumentPath([.. Segments.Take(Segments.Count - 1), name]);
  }
}

/// <summary>Quoting for the SQL the stored-form migrations assemble.</summary>
internal static class SqlText {
  public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

  public static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

  // Property keys, member names and columns are compile-time model metadata, but they are embedded in SQL, so
  // anything that is not a plain identifier is refused rather than trusted.
  public static void EnsureIdentifier(string value, string parameter) {
    if (string.IsNullOrWhiteSpace(value) || !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) {
      throw new ArgumentException($"'{value}' is not a plain identifier.", parameter);
    }
  }
}
