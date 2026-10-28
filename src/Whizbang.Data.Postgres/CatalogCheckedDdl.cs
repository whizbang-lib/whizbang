// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Rewrites idempotent DDL so it takes no table lock when the catalog says there is nothing to do (#1636).
/// </summary>
/// <remarks>
/// <para>
/// <c>ALTER TABLE ... ADD COLUMN IF NOT EXISTS</c> takes ACCESS EXCLUSIVE on its table before it looks for the column,
/// and <c>CREATE INDEX IF NOT EXISTS</c> takes SHARE before it looks for the index. Neither has anything to do on every
/// run after the first, and both still queue behind every transaction using the table, and every transaction after
/// them queues behind the queued lock. A migration re-run (the redefinition closure, a re-applied bootstrap, the core
/// tables' ensure) therefore stalled the running fleet, and with a second lock held in the same transaction it
/// deadlocked the service's own electors and assigner on the instance and role tables.
/// </para>
/// <para>
/// Each such top-level statement becomes a <c>DO</c> block that asks the catalog first and runs the statement, as
/// written, only when a column or the index is missing. The catalog read takes no lock on the table:
/// <c>to_regclass</c> resolves a name without one. So the case with nothing to do takes no lock at all, and the case
/// with something to do does exactly what the statement did. A missing table still fails as the statement fails,
/// because the statement is what runs.
/// </para>
/// <para>
/// Only these two shapes are rewritten, and only when every clause of the ALTER is an <c>ADD [COLUMN] IF NOT EXISTS</c>.
/// Everything else in the script is returned as written, character for character: comments, other statements, and
/// anything inside a function body, a <c>DO</c> block, a string or a quoted identifier, which are not statements. The
/// rewrite is applied to the SQL as it is executed, never to what is hashed, so no ledger row changes.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#idempotent-ddl-takes-no-lock</docs>
/// <tests>tests/Whizbang.Core.Tests/Schema/CatalogCheckedDdlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/CatalogCheckedDdlPostgresTests.cs</tests>
public static class CatalogCheckedDdl {
  private const string TAG = "$whizbang_catalog_checked$";

  /// <summary>The keyword that ends both forms of an <c>IF [NOT] EXISTS</c> clause.</summary>
  private const string EXISTS = "EXISTS";

  /// <summary>Returns <paramref name="sql"/> with its idempotent DDL catalog-checked.</summary>
  /// <param name="sql">A script of one or more statements, as it is about to be executed.</param>
  /// <returns>The script to execute; the same instance when nothing in it is rewritten.</returns>
  public static string Guard(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    StringBuilder? output = null;
    var copied = 0;
    foreach (var statement in SqlStatementReader.Read(sql)) {
      if (_guarded(sql, statement) is not { } guarded) {
        continue;
      }
      output ??= new StringBuilder(sql.Length + 512);
      output.Append(sql, copied, statement.Start - copied).Append(guarded);
      copied = statement.End;
    }
    return output is null ? sql : output.Append(sql, copied, sql.Length - copied).ToString();
  }

  /// <summary>The guarded form of one statement, or null when it is not one this rewrites.</summary>
  private static string? _guarded(string sql, SqlStatement statement) {
    var condition = _alterAddColumnsCondition(statement.Tokens) ?? _createIndexCondition(statement.Tokens);
    if (condition is null) {
      return null;
    }
    var text = sql[statement.Start..statement.CodeEnd];
    if (text.Contains(TAG, StringComparison.Ordinal)) {
      return null;
    }
    return "DO " + TAG + "\n"
      + "BEGIN\n"
      + "  IF " + condition + " THEN\n"
      + "    " + text + ";\n"
      + "  END IF;\n"
      + "END\n"
      + TAG + ";";
  }

  /// <summary>
  /// <c>ALTER TABLE [IF EXISTS] [ONLY] name ADD [COLUMN] IF NOT EXISTS column ... [, ...]</c>: true while any of the
  /// columns is missing.
  /// </summary>
  private static string? _alterAddColumnsCondition(IReadOnlyList<SqlToken> tokens) {
    var reader = new TokenCursor(tokens);
    if (!reader.TakeWords("ALTER", "TABLE")) {
      return null;
    }
    _ = reader.TakeWords("IF", EXISTS);
    _ = reader.TakeWords("ONLY");
    if (reader.TakeRelation() is not { } relation) {
      return null;
    }
    var columns = new List<string>();
    do {
      if (!reader.TakeWords("ADD") || (!reader.TakeWords("COLUMN", "IF", "NOT", EXISTS) && !reader.TakeWords("IF", "NOT", EXISTS))
          || reader.TakeIdentifier() is not { } column || !reader.SkipToClauseEnd()) {
        return null;
      }
      columns.Add(column);
    } while (reader.TakeComma());
    var names = string.Join(", ", columns.Select(_literal));
    return "EXISTS (SELECT 1 FROM unnest(ARRAY[" + names + "]::text[]) AS wanted(name) WHERE NOT EXISTS ("
      + "SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = to_regclass(" + _literal(relation) + ") "
      + "AND a.attname = wanted.name AND a.attnum > 0 AND NOT a.attisdropped))";
  }

  /// <summary>
  /// <c>CREATE [UNIQUE] INDEX IF NOT EXISTS name ON [ONLY] table ...</c>: true while no relation of that name is in
  /// the table's schema, which is what the statement's own IF NOT EXISTS asks.
  /// </summary>
  private static string? _createIndexCondition(IReadOnlyList<SqlToken> tokens) {
    var reader = new TokenCursor(tokens);
    if (!reader.TakeWords("CREATE")) {
      return null;
    }
    _ = reader.TakeWords("UNIQUE");
    if (!reader.TakeWords("INDEX", "IF", "NOT", EXISTS) || reader.TakeIdentifier() is not { } index
        || !reader.TakeWords("ON")) {
      return null;
    }
    _ = reader.TakeWords("ONLY");
    if (reader.TakeRelation() is not { } relation) {
      return null;
    }
    return "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class AS i WHERE i.relname = " + _literal(index) + " "
      + "AND i.relnamespace = (SELECT c.relnamespace FROM pg_catalog.pg_class AS c "
      + "WHERE c.oid = to_regclass(" + _literal(relation) + ")))";
  }

  private static string _literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

  /// <summary>Reads a statement's tokens from the front.</summary>
  private sealed class TokenCursor(IReadOnlyList<SqlToken> tokens) {
    private int _at;

    /// <summary>Takes the words when the next tokens are exactly these keywords, in order; takes nothing otherwise.</summary>
    public bool TakeWords(params string[] words) {
      for (var i = 0; i < words.Length; i++) {
        if (_at + i >= tokens.Count || !tokens[_at + i].IsWord(words[i])) {
          return false;
        }
      }
      _at += words.Length;
      return true;
    }

    /// <summary>A name, as the server reads it: a quoted identifier as written, an unquoted one in lower case.</summary>
    public string? TakeIdentifier() {
      if (_at >= tokens.Count || tokens[_at].Kind is not (SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier)) {
        return null;
      }
      return tokens[_at++].Name;
    }

    /// <summary>A possibly qualified relation name, as written, for <c>to_regclass</c>, which reads it as the statement does.</summary>
    public string? TakeRelation() {
      if (TakeIdentifier() is null) {
        return null;
      }
      var first = tokens[_at - 1];
      var last = first;
      if (_at < tokens.Count && tokens[_at].IsPunctuation('.')) {
        _at++;
        if (TakeIdentifier() is null) {
          return null;
        }
        last = tokens[_at - 1];
      }
      return first.Sql[first.Start..last.End];
    }

    /// <summary>Skips a column definition: at least one token, up to a comma at depth zero or the end.</summary>
    public bool SkipToClauseEnd() {
      var start = _at;
      var depth = 0;
      while (_at < tokens.Count && (depth > 0 || !tokens[_at].IsPunctuation(','))) {
        depth += tokens[_at].IsPunctuation('(') ? 1 : 0;
        depth -= tokens[_at].IsPunctuation(')') ? 1 : 0;
        _at++;
      }
      return _at > start;
    }

    /// <summary>Takes a comma between two clauses.</summary>
    public bool TakeComma() {
      if (_at < tokens.Count && tokens[_at].IsPunctuation(',')) {
        _at++;
        return true;
      }
      return false;
    }
  }
}

/// <summary>What a token is.</summary>
internal enum SqlTokenKind {
  /// <summary>A keyword or an unquoted identifier.</summary>
  Word,

  /// <summary>A double-quoted identifier.</summary>
  QuotedIdentifier,

  /// <summary>A string literal or a dollar-quoted body.</summary>
  Literal,

  /// <summary>Any other single character.</summary>
  Punctuation,
}

/// <summary>One token of a script, by position.</summary>
internal readonly record struct SqlToken(string Sql, SqlTokenKind Kind, int Start, int End) {
  /// <summary>Whether this is the keyword <paramref name="word"/>, in any case.</summary>
  public bool IsWord(string word) =>
    Kind == SqlTokenKind.Word && Sql.AsSpan(Start, End - Start).Equals(word, StringComparison.OrdinalIgnoreCase);

  /// <summary>Whether this is the single character <paramref name="c"/>.</summary>
  public bool IsPunctuation(char c) => Kind == SqlTokenKind.Punctuation && Sql[Start] == c;

  /// <summary>The name an identifier token denotes: folded to lower case unquoted, as written (quotes undone) quoted.</summary>
  public string Name => Kind == SqlTokenKind.QuotedIdentifier
    ? Sql[(Start + 1)..(End - 1)].Replace("\"\"", "\"", StringComparison.Ordinal)
    : Sql[Start..End].ToLower(CultureInfo.InvariantCulture);
}

/// <summary>One top-level statement: where its code starts and ends, where it ends with its semicolon, and its tokens.</summary>
internal sealed record SqlStatement(int Start, int CodeEnd, int End, IReadOnlyList<SqlToken> Tokens);

/// <summary>
/// Splits a script into its top-level statements. Comments (nested block comments included), string literals (with
/// <c>''</c> doubling, and backslash escapes in <c>E'...'</c>), quoted identifiers and dollar-quoted bodies are read
/// to their end, so a semicolon inside any of them ends nothing; an unterminated one runs to the end of the text.
/// </summary>
internal static class SqlStatementReader {
  /// <summary>The statements of <paramref name="sql"/> that have at least one token, in order.</summary>
  public static IEnumerable<SqlStatement> Read(string sql) {
    var tokens = new List<SqlToken>();
    var start = -1;
    var codeEnd = 0;
    var i = 0;
    while (i < sql.Length) {
      var skipped = _triviaEnd(sql, i);
      if (skipped >= 0) {
        i = skipped;
      } else if (sql[i] == ';') {
        if (start >= 0) {
          yield return new SqlStatement(start, codeEnd, i + 1, tokens);
        }
        tokens = [];
        start = -1;
        i++;
      } else {
        var token = _token(sql, i);
        tokens.Add(token);
        start = start < 0 ? token.Start : start;
        codeEnd = token.End;
        i = token.End;
      }
    }
    if (start >= 0) {
      yield return new SqlStatement(start, codeEnd, sql.Length, tokens);
    }
  }

  private static char _at(string sql, int i) => i < sql.Length ? sql[i] : '\0';

  /// <summary>Where the whitespace or comment at <paramref name="i"/> ends; -1 when what is there is neither.</summary>
  private static int _triviaEnd(string sql, int i) {
    var c = sql[i];
    if (char.IsWhiteSpace(c)) {
      return i + 1;
    }
    if (c == '-' && _at(sql, i + 1) == '-') {
      return _lineEnd(sql, i);
    }
    if (c == '/' && _at(sql, i + 1) == '*') {
      return _blockCommentEnd(sql, i);
    }
    return -1;
  }

  private static SqlToken _token(string sql, int i) {
    var c = sql[i];
    if (c == '\'') {
      return new SqlToken(sql, SqlTokenKind.Literal, i, _quotedEnd(sql, i, '\'', backslashEscapes: false));
    }
    if (c == '"') {
      return new SqlToken(sql, SqlTokenKind.QuotedIdentifier, i, _quotedEnd(sql, i, '"', backslashEscapes: false));
    }
    if (c is 'E' or 'e' && _at(sql, i + 1) == '\'') {
      return new SqlToken(sql, SqlTokenKind.Literal, i, _quotedEnd(sql, i + 1, '\'', backslashEscapes: true));
    }
    if (c == '$' && _dollarTag(sql, i) is { } tag) {
      var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
      return new SqlToken(sql, SqlTokenKind.Literal, i, close < 0 ? sql.Length : close + tag.Length);
    }
    if (_isWordStart(c)) {
      var end = i + 1;
      while (end < sql.Length && _isWordPart(sql[end])) {
        end++;
      }
      return new SqlToken(sql, SqlTokenKind.Word, i, end);
    }
    return new SqlToken(sql, SqlTokenKind.Punctuation, i, i + 1);
  }

  private static int _lineEnd(string sql, int i) {
    var end = sql.IndexOf('\n', i);
    return end < 0 ? sql.Length : end;
  }

  private static int _blockCommentEnd(string sql, int i) {
    var depth = 0;
    while (i < sql.Length) {
      if (sql[i] == '/' && _at(sql, i + 1) == '*') {
        depth++;
        i += 2;
      } else if (sql[i] == '*' && _at(sql, i + 1) == '/') {
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

  /// <summary>The index just past the quote that closes the one at <paramref name="open"/>; the end when none does.</summary>
  private static int _quotedEnd(string sql, int open, char quote, bool backslashEscapes) {
    var j = open + 1;
    while (j < sql.Length) {
      if (backslashEscapes && sql[j] == '\\') {
        j += 2;
      } else if (sql[j] != quote) {
        j++;
      } else if (_at(sql, j + 1) == quote) {
        j += 2;
      } else {
        return j + 1;
      }
    }
    return sql.Length;
  }

  /// <summary>The dollar-quote delimiter starting at <paramref name="i"/> (<c>$$</c> or <c>$tag$</c>), or null.</summary>
  private static string? _dollarTag(string sql, int i) {
    var j = i + 1;
    while (j < sql.Length && sql[j] != '$') {
      if (!(j == i + 1 ? _isWordStart(sql[j]) : _isWordPart(sql[j]))) {
        return null;
      }
      j++;
    }
    return j < sql.Length ? sql[i..(j + 1)] : null;
  }

  private static bool _isWordStart(char c) => char.IsLetter(c) || c == '_';

  private static bool _isWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
