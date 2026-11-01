// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Reads, from a perspective table's generated DDL, the names of the indexes and constraints it creates: the
/// objects the generator declares to the managed-object ledger.
/// </summary>
/// <remarks>
/// <para>
/// Read from the very statements the schema pass runs, rather than re-derived from the model, so the declaration
/// cannot drift from what is built: an index the DDL creates is declared, and one it only drops is not. Names are
/// returned unquoted, as PostgreSQL stores them.
/// </para>
/// <para>
/// A scan, not a regular expression (#1942). The two expressions this replaces ran under a one-second match
/// timeout, which measures the wall clock rather than the work: a generator thread that a loaded build machine
/// descheduled for a second threw <c>RegexMatchTimeoutException</c>, and the schema generator emitted nothing at
/// all. The scan reads the statement shapes the expressions read, in the same order, with nothing to time out:
/// <c>CREATE [UNIQUE] INDEX [CONCURRENTLY] [IF NOT EXISTS] name</c> and <c>ADD CONSTRAINT name</c>, case-insensitive,
/// starting at a word boundary, where a name is a double-quoted identifier or an unquoted one.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <tests>tests/Whizbang.Generators.Tests/ManagedObjectNamesTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/ManagedObjectNamesScanTests.cs</tests>
public static class ManagedObjectNames {
  /// <summary>An optional clause is tried present first, then absent, as a greedy optional group is.</summary>
  private static readonly bool[] _presentThenAbsent = [true, false];

  /// <summary>The (kind, name) pairs the DDL creates, in order of first appearance, each once.</summary>
  public static List<(string Kind, string Name)> Extract(string sql) {
    var found = new List<(string Kind, string Name)>();
    var seen = new HashSet<(string, string)>();
    foreach (var name in _names(sql, "CREATE", _createIndexAt)) {
      _add(found, seen, "index", name);
    }
    foreach (var name in _names(sql, "ADD", _addConstraintAt)) {
      _add(found, seen, "constraint", name);
    }
    return found;
  }

  private static void _add(List<(string, string)> found, HashSet<(string, string)> seen, string kind, string name) {
    var bare = name.Length > 1 && name[0] == '"' ? name[1..^1] : name;
    if (seen.Add((kind, bare))) {
      found.Add((kind, bare));
    }
  }

  /// <summary>A statement shape read at one position: the name it creates and where it ends, or null.</summary>
  private delegate (string Name, int End)? ShapeAt(string sql, int start);

  /// <summary>
  /// Every name a statement shape reads, left to right, each read starting where the previous one ended, as
  /// successive matches of an expression do.
  /// </summary>
  private static List<string> _names(string sql, string firstWord, ShapeAt shapeAt) {
    var names = new List<string>();
    var at = sql.IndexOf(firstWord, 0, StringComparison.OrdinalIgnoreCase);
    while (at >= 0) {
      var next = at + 1;
      if ((at == 0 || !_isWordCharacter(sql[at - 1])) && shapeAt(sql, at) is { } read) {
        names.Add(read.Name);
        next = read.End;
      }
      at = next < sql.Length ? sql.IndexOf(firstWord, next, StringComparison.OrdinalIgnoreCase) : -1;
    }
    return names;
  }

  /// <summary><c>CREATE [UNIQUE] INDEX [CONCURRENTLY] [IF NOT EXISTS] name</c> at <paramref name="start"/>.</summary>
  private static (string Name, int End)? _createIndexAt(string sql, int start) {
    foreach (var unique in _presentThenAbsent) {
      foreach (var concurrently in _presentThenAbsent) {
        foreach (var ifNotExists in _presentThenAbsent) {
          var at = start;
          if (_word(sql, ref at, "CREATE")
              && (!unique || _word(sql, ref at, "UNIQUE"))
              && _word(sql, ref at, "INDEX")
              && (!concurrently || _word(sql, ref at, "CONCURRENTLY"))
              && (!ifNotExists || (_word(sql, ref at, "IF") && _word(sql, ref at, "NOT") && _word(sql, ref at, "EXISTS")))
              && _name(sql, at) is { } name) {
            return (name, at + name.Length);
          }
        }
      }
    }
    return null;
  }

  /// <summary><c>ADD CONSTRAINT name</c> at <paramref name="start"/>.</summary>
  private static (string Name, int End)? _addConstraintAt(string sql, int start) {
    var at = start;
    return _word(sql, ref at, "ADD") && _word(sql, ref at, "CONSTRAINT") && _name(sql, at) is { } name
      ? (name, at + name.Length)
      : null;
  }

  /// <summary>
  /// The keyword, in any case, followed by at least one whitespace character; on success <paramref name="at"/>
  /// moves past the whitespace.
  /// </summary>
  private static bool _word(string sql, ref int at, string keyword) {
    if (at + keyword.Length >= sql.Length
        || string.Compare(sql, at, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0
        || !char.IsWhiteSpace(sql[at + keyword.Length])) {
      return false;
    }
    at += keyword.Length;
    while (at < sql.Length && char.IsWhiteSpace(sql[at])) {
      at++;
    }
    return true;
  }

  /// <summary>
  /// The name at <paramref name="at"/>: a double-quoted identifier with at least one character, quotes included,
  /// or an unquoted one (a letter or underscore, then letters, digits, underscores and dollar signs).
  /// </summary>
  private static string? _name(string sql, int at) {
    if (at >= sql.Length) {
      return null;
    }
    if (sql[at] == '"') {
      var close = sql.IndexOf('"', at + 1);
      if (close > at + 1) {
        return sql.Substring(at, close - at + 1);
      }
    }
    if (!_isIdentifierStart(sql[at])) {
      return null;
    }
    var end = at + 1;
    while (end < sql.Length && (_isIdentifierStart(sql[end]) || char.IsDigit(sql[end]) || sql[end] == '$')) {
      end++;
    }
    return sql[at..end];
  }

  private static bool _isIdentifierStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

  /// <summary>
  /// A character on the word side of a word boundary: a letter, a decimal digit, a non-spacing mark, a connector
  /// punctuation mark, or one of the two zero-width joiners, the set a <c>\b</c> tests.
  /// </summary>
  private static bool _isWordCharacter(char c) =>
    char.IsLetterOrDigit(c)
    || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.ConnectorPunctuation
    || c is '\u200C' or '\u200D';
}
