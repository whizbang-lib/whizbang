// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The literals the migrations share, defined once in <c>Migrations/constants.txt</c> (rule 12) and substituted
/// into every migration on the same path as <c>__SCHEMA__</c>: the runtime provider, the embedded-migration path a
/// generated DbContext executes, and the drift comparison against deployed function bodies. A migration writes the
/// token (<c>__EMPTY_UUID__</c>) where it would otherwise repeat the literal, so one definition serves every
/// function that copies a body forward and a mistyped copy cannot exist. Tokens are UPPER_SNAKE names between
/// double underscores; values are SQL fragments, quotes included.
/// </summary>
/// <remarks>
/// A constant may take one argument, written <c>__TOKEN__(p) = value</c>: the migration writes <c>__TOKEN__(alias)</c>
/// and every word <c>p</c> in the value is replaced by the alias, so one rule can name the columns of whichever row a
/// statement reads (#2352: <c>__DIGEST_ELIGIBLE__(es)</c>, <c>__DIGEST_ELIGIBLE__(NEW)</c>). A value may write other
/// constants; they are substituted after the argument.
/// </remarks>
/// <docs>operations/infrastructure/migrations#constants</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationConstantsTests.cs</tests>
public static partial class MigrationConstants {
  /// <summary>The schema placeholders, substituted elsewhere; never constants.</summary>
  public static IReadOnlyList<string> ReservedTokens { get; } = ["__SCHEMA__", "__MIGRATION_SCHEMA__"];

  private const string RESOURCE_NAME = "Whizbang.Data.Postgres.Migrations.constants.txt";

  private static readonly Lazy<IReadOnlyList<KeyValuePair<string, string>>> _entries =
    new(_load, LazyThreadSafetyMode.ExecutionAndPublication);

  private static readonly Lazy<IReadOnlyDictionary<string, string>> _byToken =
    new(() => _entries.Value.ToDictionary(e => _tokenOf(e.Key), e => e.Value, StringComparer.Ordinal), LazyThreadSafetyMode.ExecutionAndPublication);

  /// <summary>The tokens and the SQL fragments they stand for. A constant with an argument is listed by its token, with
  /// its value as written (the argument's name not yet replaced).</summary>
  public static IReadOnlyDictionary<string, string> Tokens => _byToken.Value;

  private static readonly Lazy<IReadOnlyDictionary<string, string>> _parameters =
    new(() => _entries.Value.Select(e => (e.Key, Parameter: _parameterOf(e.Key))).Where(e => e.Parameter is not null)
      .ToDictionary(e => _tokenOf(e.Key), e => e.Parameter!, StringComparer.Ordinal), LazyThreadSafetyMode.ExecutionAndPublication);

  /// <summary>Replaces every token in <paramref name="sql"/>; text without a token is returned as is.</summary>
  public static string Apply(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    if (!sql.Contains("__", StringComparison.Ordinal)) {
      return sql;
    }
    // Constants with an argument first, so the constants their values write are substituted with the rest.
    foreach (var (token, parameter) in _parameters.Value) {
      var value = Tokens[token];
      sql = Regex.Replace(sql, Regex.Escape(token) + @"\(\s*(?<alias>[A-Za-z_][A-Za-z0-9_]*)\s*\)",
        call => Regex.Replace(value, @"\b" + Regex.Escape(parameter) + @"\b", call.Groups["alias"].Value, RegexOptions.None, _matchTimeout),
        RegexOptions.None, _matchTimeout);
    }
    foreach (var (key, value) in _entries.Value.Where(e => _parameterOf(e.Key) is null)) {
      sql = sql.Replace(key, value, StringComparison.Ordinal);
    }
    return sql;
  }

  private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(5);

  /// <summary>The token of a definition's left side: <c>__T__</c> for both <c>__T__</c> and <c>__T__(p)</c>.</summary>
  private static string _tokenOf(string key) {
    var open = key.IndexOf('(', StringComparison.Ordinal);
    return open < 0 ? key : key[..open];
  }

  /// <summary>The argument's name of a definition's left side, or null for a constant without one.</summary>
  private static string? _parameterOf(string key) {
    var open = key.IndexOf('(', StringComparison.Ordinal);
    return open < 0 ? null : key[(open + 1)..^1];
  }

  /// <summary>
  /// Tokens written in <paramref name="sql"/> that nothing defines: a typo, or a constant not yet added to the
  /// file. The schema placeholders are not reported.
  /// </summary>
  public static IReadOnlyList<string> UnknownTokens(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    return [.. _tokenPattern().Matches(sql)
      .Select(match => match.Value)
      .Where(token => !ReservedTokens.Contains(token) && !Tokens.ContainsKey(token))
      .Distinct(StringComparer.Ordinal)
      .Order(StringComparer.Ordinal)];
  }

  /// <summary>
  /// Parses the constants file: <c>#</c> comments and blank lines are skipped; every other line is
  /// <c>__TOKEN__ = value</c>. A reserved, malformed or duplicate token, an empty value, a value with a brace (the EF
  /// Core path cannot carry one), or a token that is a substring of another (one substitution would eat the other) is
  /// a defect in the file and throws.
  /// </summary>
  internal static IReadOnlyList<KeyValuePair<string, string>> Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var entries = new List<KeyValuePair<string, string>>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var lineNumber = 0;
    foreach (var rawLine in text.Split('\n')) {
      lineNumber++;
      var line = rawLine.Trim();
      if (line.Length == 0 || line.StartsWith('#')) {
        continue;
      }
      var entry = _parseDefinition(lineNumber, line);
      if (!seen.Add(_tokenOf(entry.Key))) {
        throw new InvalidDataException($"constants.txt line {lineNumber}: '{_tokenOf(entry.Key)}' is defined twice.");
      }
      entries.Add(entry);
    }
    _rejectOverlaps(entries);
    return entries;
  }

  /// <summary>One <c>__TOKEN__ = value</c> line; only the first '=' separates the two.</summary>
  private static KeyValuePair<string, string> _parseDefinition(int lineNumber, string line) {
    var separator = line.IndexOf('=', StringComparison.Ordinal);
    if (separator < 1) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: expected '__TOKEN__ = value', found '{line}'.");
    }
    var key = line[..separator].Trim();
    var value = line[(separator + 1)..].Trim();
    var token = _tokenOf(key);
    if (_tokenPattern().Match(token).Value != token) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' is not an UPPER_SNAKE name between double underscores.");
    }
    if (key.Length != token.Length && !_parameterPattern().IsMatch(key[token.Length..])) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: '{key}' takes one argument, written '{token}(name)'.");
    }
    if (ReservedTokens.Contains(token)) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' is the schema placeholder, not a constant.");
    }
    if (value.Length == 0) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' has no value.");
    }
    // The EF Core schema path doubles every brace in a migration for ExecuteSqlRawAsync and substitutes the constants
    // after, so a brace in a value would reach the format parser unescaped and fail schema setup. Write a path as
    // ARRAY['a','b'] rather than '{a,b}'.
    if (value.AsSpan().IndexOfAny('{', '}') >= 0) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' has a brace in its value; the EF Core schema path cannot carry one. Write a JSON path as ARRAY['a','b'].");
    }
    return new KeyValuePair<string, string>(key, value);
  }

  /// <summary>A token that is a substring of another would be eaten by the other's substitution.</summary>
  private static void _rejectOverlaps(IReadOnlyList<KeyValuePair<string, string>> entries) {
    var tokens = entries.Select(e => _tokenOf(e.Key)).ToList();
    var overlap = tokens
      .SelectMany(a => tokens
        .Where(b => a != b && b.Contains(a, StringComparison.Ordinal))
        .Select(b => (Inner: a, Outer: b)))
      .FirstOrDefault();
    if (overlap != default) {
      throw new InvalidDataException($"constants.txt: '{overlap.Inner}' is a substring of '{overlap.Outer}'; substituting one would eat the other.");
    }
  }

  private static IReadOnlyList<KeyValuePair<string, string>> _load() =>
    Parse(EmbeddedText.Read(typeof(MigrationConstants).Assembly, RESOURCE_NAME,
      $"Embedded resource '{RESOURCE_NAME}' is missing; the migrations' constants file is part of the package."));

  [GeneratedRegex("__[A-Z][A-Z0-9_]*__")]
  private static partial Regex _tokenPattern();

  [GeneratedRegex(@"^\([a-z][a-z0-9_]*\)$")]
  private static partial Regex _parameterPattern();
}
