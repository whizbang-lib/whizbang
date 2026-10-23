// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The form of a migration's SQL that its change detection hashes: what a database executes, without the text that
/// changes nothing it executes.
/// </summary>
/// <remarks>
/// <para>
/// Removed: <c>--</c> line comments and <c>/* */</c> block comments (which nest, as PostgreSQL reads them); spaces and
/// tabs at the start and end of every line, and runs of them between tokens, which become one space; blank lines; and
/// the line ending convention (CRLF and a lone CR read as LF). So a comment, a license header or a blank line can be
/// added to a shipped migration without any database treating the file as changed.
/// </para>
/// <para>
/// Copied verbatim: single-quoted strings (with <c>''</c> doubling), <c>E'...'</c> strings (with backslash escapes),
/// double-quoted identifiers, and dollar-quoted bodies (<c>$$ ... $$</c>, <c>$tag$ ... $tag$</c>). A tokenizer rather
/// than a regular expression because <c>'a -- b'</c> and <c>'a -- c'</c> are different statements, and only a reader
/// that knows where a literal ends can tell. A comment inside a function body is therefore still a change, by decision:
/// the stored function source keeps matching the file.
/// </para>
/// <para>
/// The normalized text is only ever hashed. The SQL that runs is the file as written.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#what-counts-as-a-change</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationSqlNormalizerTests.cs</tests>
public static class MigrationSqlNormalizer {
  /// <summary>Returns <paramref name="sql"/> with everything that changes nothing executable removed.</summary>
  /// <param name="sql">The migration text, after any schema substitution the driver applies.</param>
  /// <returns>The normalized text.</returns>
  public static string Normalize(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    return new Reader(sql).Read();
  }

  /// <summary>The hash a migration's ledger row records: lowercase hex SHA-256 of the normalized UTF-8 text.</summary>
  /// <param name="sql">The migration text, after any schema substitution the driver applies.</param>
  /// <returns>Sixty-four lowercase hex characters.</returns>
  public static string ContentHash(string sql) => _sha256(Normalize(sql));

  /// <summary>
  /// The hash earlier releases recorded: lowercase hex SHA-256 of the raw text. Read only by the step that rewrites
  /// those rows to <see cref="ContentHash"/>.
  /// </summary>
  /// <param name="sql">The migration text, after any schema substitution the driver applies.</param>
  /// <returns>Sixty-four lowercase hex characters.</returns>
  public static string LegacyContentHash(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    return _sha256(sql);
  }

  private static string _sha256(string text) =>
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

  /// <summary>One pass over the text, writing the normalized form.</summary>
  private sealed class Reader(string sql) {
    private readonly StringBuilder _out = new(sql.Length);
    private int _i;
    // Whitespace or a comment was passed since the last content on this line.
    private bool _pendingSpace;
    // The current output line has content; a line ending then becomes pending rather than written, so a blank line or
    // the end of the text writes none.
    private bool _lineHasContent;
    private bool _pendingNewline;

    public string Read() {
      while (_i < sql.Length) {
        var c = sql[_i];
        if (c == '\n' || c == '\r') {
          _lineEnd();
        } else if (c is ' ' or '\t' or '\f' or '\v') {
          _pendingSpace = true;
          _i++;
        } else if (c == '-' && _peek(1) == '-') {
          _skipLineComment();
        } else if (c == '/' && _peek(1) == '*') {
          _skipBlockComment();
        } else if (c == '\'') {
          _copyQuoted('\'', backslashEscapes: _isEscapeStringPrefix());
        } else if (c == '"') {
          _copyQuoted('"', backslashEscapes: false);
        } else if (c == '$' && _dollarTagLength() is var tagLength and > 0) {
          _copyDollarQuoted(tagLength);
        } else {
          _content(_i, 1);
        }
      }
      return _out.ToString();
    }

    private char _peek(int offset) => _i + offset < sql.Length ? sql[_i + offset] : '\0';

    private void _lineEnd() {
      _i += sql[_i] == '\r' && _peek(1) == '\n' ? 2 : 1;
      _pendingSpace = false;
      if (_lineHasContent) {
        _pendingNewline = true;
        _lineHasContent = false;
      }
    }

    /// <summary>Writes sql[start..start+length] as content, preceded by whatever separator is pending.</summary>
    private void _content(int start, int length) {
      if (_pendingNewline) {
        _out.Append('\n');
        _pendingNewline = false;
      } else if (_pendingSpace && _lineHasContent) {
        _out.Append(' ');
      }
      _pendingSpace = false;
      _lineHasContent = true;
      _out.Append(sql, start, length);
      _i = start + length;
    }

    private void _skipLineComment() {
      while (_i < sql.Length && sql[_i] != '\n' && sql[_i] != '\r') {
        _i++;
      }
    }

    private void _skipBlockComment() {
      var depth = 0;
      while (_i < sql.Length) {
        if (sql[_i] == '/' && _peek(1) == '*') {
          depth++;
          _i += 2;
        } else if (sql[_i] == '*' && _peek(1) == '/') {
          depth--;
          _i += 2;
          if (depth == 0) {
            break;
          }
        } else {
          _i++;
        }
      }
      _pendingSpace = true;
    }

    /// <summary>An E or e directly before this quote that is not the end of an identifier opens an escape string.</summary>
    private bool _isEscapeStringPrefix() =>
      _i > 0 && sql[_i - 1] is 'E' or 'e' && (_i < 2 || !_isIdentifierChar(sql[_i - 2]));

    /// <summary>Copies a quoted literal whose quote character is doubled to escape it; unterminated runs to the end.</summary>
    private void _copyQuoted(char quote, bool backslashEscapes) {
      var start = _i;
      var j = _i + 1;
      while (j < sql.Length) {
        if (backslashEscapes && sql[j] == '\\') {
          j += 2;
        } else if (sql[j] == quote) {
          if (j + 1 < sql.Length && sql[j + 1] == quote) {
            j += 2;
          } else {
            j++;
            _content(start, j - start);
            return;
          }
        } else {
          j++;
        }
      }
      _content(start, sql.Length - start);
    }

    /// <summary>
    /// The length of the dollar-quote delimiter starting here (<c>$$</c> or <c>$tag$</c>), or zero when this dollar
    /// opens none: it follows an identifier character, or what follows it is not an identifier closed by a dollar.
    /// </summary>
    private int _dollarTagLength() {
      if (_i > 0 && _isIdentifierChar(sql[_i - 1])) {
        return 0;
      }
      var j = _i + 1;
      while (j < sql.Length && sql[j] != '$') {
        var tagChar = sql[j];
        var valid = j == _i + 1 ? char.IsLetter(tagChar) || tagChar == '_' : char.IsLetterOrDigit(tagChar) || tagChar == '_';
        if (!valid) {
          return 0;
        }
        j++;
      }
      return j < sql.Length ? j - _i + 1 : 0;
    }

    private void _copyDollarQuoted(int tagLength) {
      var start = _i;
      var close = sql.IndexOf(sql.Substring(start, tagLength), start + tagLength, StringComparison.Ordinal);
      _content(start, close < 0 ? sql.Length - start : close + tagLength - start);
    }

    private static bool _isIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
  }
}
