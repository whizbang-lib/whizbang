// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Qualifies the legacy framework table and function names a migration writes bare with the schema
/// the EF Core driver migrates, so a service in a named schema does not create or read them in
/// <c>public</c>. Applied by the generated <c>EnsureWhizbangDatabaseInitializedAsync</c> to every
/// embedded migration before it runs and before its hash is taken; the Dapper driver substitutes
/// <c>__SCHEMA__</c> only and never calls it.
/// </summary>
/// <remarks>
/// Every migration is meant to write <c>__SCHEMA__.wh_*</c> already (migrations README rule 3), so
/// this is a safety net for the names the oldest migrations wrote bare, not the contract.
/// </remarks>
/// <docs>contributors/data-engines/writing-migrations</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationSchemaQualifierTests.cs</tests>
public static class MigrationSchemaQualifier {
  private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(5);

  /// <summary>The framework tables (and one sequence) a bare reference to which is qualified.</summary>
  public static IReadOnlyList<string> TableNames { get; } = [
    "wh_inbox",
    "wh_outbox",
    "wh_event_store",
    "wh_events", // Alias for wh_event_store
    "wh_receptor_processing",
    "wh_perspective_cursors",
    "wh_perspective_events",
    "wh_message_associations",
    "wh_request_response",
    "wh_service_instances",
    "wh_partition_assignments",
    "wh_message_deduplication",
    "wh_sequences",
    "wh_active_streams",
    "wh_event_sequence" // Sequence name
  ];

  /// <summary>
  /// The framework functions whose bare name is qualified where it is created, dropped, granted or
  /// commented on. Functions are database-wide, so two schemas in one database must each own theirs.
  /// </summary>
  public static IReadOnlyList<string> FunctionNames { get; } = [
    "register_message_associations",
    "reconcile_perspective_registry",
    "perform_maintenance"
  ];

  /// <summary>
  /// Prefixes every bare reference to a <see cref="TableNames">framework table</see>, and every bare
  /// <see cref="FunctionNames">framework function</see> a statement defines, drops, grants or
  /// comments on, with <paramref name="quotedSchema"/>.
  /// </summary>
  /// <param name="sql">The migration text, with its schema placeholder already substituted.</param>
  /// <param name="quotedSchema">The schema as a quoted identifier, for example <c>"inventory"</c>.</param>
  /// <returns>The migration text with the bare framework names qualified.</returns>
  /// <remarks>
  /// <para>
  /// Only SQL that names objects is rewritten. A string literal (<c>'...'</c>, <c>E'...'</c>), a quoted
  /// identifier and a comment are left exactly as written: the same letters there are a value, and a
  /// value changed under the author is a different program. A catalog guard written
  /// <c>table_name = 'wh_inbox'</c> rewritten to <c>table_name = '"svc".wh_inbox'</c> matches no row,
  /// so on every re-run it reads "not there yet"; a stored <c>source_table</c> value or an advisory-lock
  /// key changes the same way.
  /// </para>
  /// <para>
  /// A dollar-quoted string is treated as code, because that is what a function or <c>DO</c> body
  /// is and its table references must be qualified; the literals and comments inside it are protected
  /// in turn. A name already preceded by a period is left alone, and a <c>RENAME TO</c> target is never
  /// qualified because PostgreSQL requires it bare.
  /// </para>
  /// </remarks>
  /// <exception cref="ArgumentException">
  /// <paramref name="sql"/> holds one of the private-use characters the rewrite marks protected text with.
  /// </exception>
  public static string Qualify(string sql, string quotedSchema) {
    ArgumentNullException.ThrowIfNull(sql);
    ArgumentException.ThrowIfNullOrEmpty(quotedSchema);
    if (sql.Contains(MASK_OPEN, StringComparison.Ordinal) || sql.Contains(MASK_CLOSE, StringComparison.Ordinal)) {
      throw new ArgumentException(
        "The migration text holds a private-use character (U+E000 or U+E001) the qualification reserves.", nameof(sql));
    }

    var protectedText = new List<string>();
    var transformedSql = _maskNonCode(sql, protectedText);

    // First, protect RENAME TO targets by replacing them with a placeholder
    // PostgreSQL RENAME TO requires an unqualified name, so these must NOT be schema-qualified
    var renameToPlaceholders = new Dictionary<string, string>(StringComparer.Ordinal);
    transformedSql = Regex.Replace(
      transformedSql,
      @"(RENAME\s+TO\s+)(\w+)",
      m => {
        var placeholder = $"__RENAME_TARGET_{renameToPlaceholders.Count}__";
        renameToPlaceholders[placeholder] = m.Groups[2].Value;
        return m.Groups[1].Value + placeholder;
      },
      RegexOptions.IgnoreCase,
      _regexTimeout);

    foreach (var tableName in TableNames) {
      // Pattern: tableName NOT preceded by period (to avoid replacing already-qualified names)
      // Matches: "FROM wh_inbox", "ALTER TABLE wh_inbox", etc.
      // Does NOT match: "inventory.wh_inbox", "wh_inbox_id" (column name)
      transformedSql = Regex.Replace(
        transformedSql,
        $@"(?<!\.)(\b{Regex.Escape(tableName)}\b)",
        $"{quotedSchema}.$1",
        RegexOptions.IgnoreCase,
        _regexTimeout);
    }

    foreach (var (placeholder, original) in renameToPlaceholders) {
      transformedSql = transformedSql.Replace(placeholder, original, StringComparison.Ordinal);
    }

    foreach (var functionName in FunctionNames) {
      var name = Regex.Escape(functionName);

      // "CREATE [OR REPLACE] FUNCTION name(" → "CREATE [OR REPLACE] FUNCTION "schema".name("
      transformedSql = Regex.Replace(
        transformedSql,
        $@"(CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+)(?<!\.)\b{name}\b(\s*\()",
        $"$1{quotedSchema}.{functionName}$2",
        RegexOptions.IgnoreCase,
        _regexTimeout);

      // "DROP FUNCTION [IF EXISTS] name" → "DROP FUNCTION [IF EXISTS] "schema".name"
      transformedSql = Regex.Replace(
        transformedSql,
        $@"(DROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}",
        RegexOptions.IgnoreCase,
        _regexTimeout);

      // "GRANT ... ON FUNCTION name" → "GRANT ... ON FUNCTION "schema".name" (and REVOKE)
      transformedSql = Regex.Replace(
        transformedSql,
        $@"((?:GRANT|REVOKE)\s+.*\s+ON\s+FUNCTION\s+)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}",
        RegexOptions.IgnoreCase,
        _regexTimeout);

      // "COMMENT ON FUNCTION name" → "COMMENT ON FUNCTION "schema".name"
      transformedSql = Regex.Replace(
        transformedSql,
        $@"(COMMENT\s+ON\s+FUNCTION\s+)(?<!\.)\b{name}\b",
        $"$1{quotedSchema}.{functionName}",
        RegexOptions.IgnoreCase,
        _regexTimeout);
    }

    return _unmask(transformedSql, protectedText);
  }

  private const char MASK_OPEN = '\uE000';
  private const char MASK_CLOSE = '\uE001';

  // A protected run becomes MASK_OPEN + its index + MASK_CLOSE. Neither mark is a word character, so
  // the word boundaries and the "not after a period" test of the patterns see the same edges they
  // would have seen at the protected text's own quotes or comment markers.
  private static string _unmask(string masked, List<string> protectedText) =>
    Regex.Replace(
      masked,
      $"{MASK_OPEN}(\\d+){MASK_CLOSE}",
      m => protectedText[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)],
      RegexOptions.None,
      _regexTimeout);

  private static string _mask(string text, List<string> protectedText) {
    protectedText.Add(text);
    return string.Create(CultureInfo.InvariantCulture, $"{MASK_OPEN}{protectedText.Count - 1}{MASK_CLOSE}");
  }

  // Walks the text the way PostgreSQL's lexer does and replaces every literal, quoted identifier and
  // comment with a mark, leaving code (including the content of every dollar-quoted string) visible.
  private static string _maskNonCode(string sql, List<string> protectedText) {
    var output = new StringBuilder(sql.Length);
    var i = 0;
    while (i < sql.Length) {
      var end = _nonCodeEnd(sql, i);
      if (end > i) {
        output.Append(_mask(sql[i..end], protectedText));
        i = end;
      } else if (_dollarTagAt(sql, i) is { } tag) {
        i = _appendDollarQuoted(sql, i, tag, output, protectedText);
      } else {
        output.Append(sql[i]);
        i++;
      }
    }
    return output.ToString();
  }

  // The index just past the literal, quoted identifier or comment that starts at start, or start
  // itself when none does.
  private static int _nonCodeEnd(string sql, int start) {
    var c = sql[start];
    var next = start + 1 < sql.Length ? sql[start + 1] : '\0';
    if (c == '-' && next == '-') {
      var end = sql.IndexOf('\n', start);
      return end < 0 ? sql.Length : end;
    }
    if (c == '/' && next == '*') {
      return _blockCommentEnd(sql, start);
    }
    if (c == '\'') {
      return _quotedEnd(sql, start, '\'', _isEscapeString(sql, start));
    }
    return c == '"' ? _quotedEnd(sql, start, '"', backslashEscapes: false) : start;
  }

  // E'...' (or e'...') where the E is not the end of a longer identifier.
  private static bool _isEscapeString(string sql, int quote) =>
    quote > 0 && (sql[quote - 1] == 'E' || sql[quote - 1] == 'e') && (quote < 2 || !_isIdentifierChar(sql[quote - 2]));

  // The dollar-quote tag that opens at i, if one does: a "$" that is not part of an identifier.
  private static string? _dollarTagAt(string sql, int i) =>
    sql[i] == '$' && (i == 0 || !_isIdentifierChar(sql[i - 1])) ? _dollarTag(sql, i) : null;

  // Appends a dollar-quoted string with its body treated as code, and returns the index just past it.
  // An unclosed one runs to the end.
  private static int _appendDollarQuoted(
      string sql, int start, string tag, StringBuilder output, List<string> protectedText) {
    var bodyStart = start + tag.Length;
    var close = sql.IndexOf(tag, bodyStart, StringComparison.Ordinal);
    output.Append(tag);
    if (close < 0) {
      output.Append(_maskNonCode(sql[bodyStart..], protectedText));
      return sql.Length;
    }
    output.Append(_maskNonCode(sql[bodyStart..close], protectedText)).Append(tag);
    return close + tag.Length;
  }

  // Block comments nest in PostgreSQL. An unclosed one runs to the end.
  private static int _blockCommentEnd(string sql, int start) {
    var depth = 0;
    var i = start;
    while (i < sql.Length) {
      if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') {
        depth++;
        i += 2;
      } else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') {
        i += 2;
        if (--depth == 0) {
          return i;
        }
      } else {
        i++;
      }
    }
    return sql.Length;
  }

  // The index just past the closing quote. A doubled quote is one character of content; in an escape
  // string (E'...') so is a backslash and whatever follows it. An unclosed one runs to the end.
  private static int _quotedEnd(string sql, int start, char quote, bool backslashEscapes) {
    var i = start + 1;
    while (i < sql.Length) {
      if (backslashEscapes && sql[i] == '\\') {
        i += 2;
      } else if (sql[i] != quote) {
        i++;
      } else if (i + 1 < sql.Length && sql[i + 1] == quote) {
        i += 2;
      } else {
        return i + 1;
      }
    }
    return sql.Length;
  }

  // "$$" or "$tag$", where a tag is an identifier that does not start with a digit ("$1" is a parameter).
  private static string? _dollarTag(string sql, int start) {
    var i = start + 1;
    if (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_')) {
      while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) {
        i++;
      }
    }
    return i < sql.Length && sql[i] == '$' ? sql[start..(i + 1)] : null;
  }

  private static bool _isIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
