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
/// <docs>operations/infrastructure/migrations#constants</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationConstantsTests.cs</tests>
public static partial class MigrationConstants {
  /// <summary>The schema placeholders, substituted elsewhere; never constants.</summary>
  public static IReadOnlyList<string> ReservedTokens { get; } = ["__SCHEMA__", "__MIGRATION_SCHEMA__"];

  private const string RESOURCE_NAME = "Whizbang.Data.Postgres.Migrations.constants.txt";

  private static readonly Lazy<IReadOnlyList<KeyValuePair<string, string>>> _entries =
    new(_load, LazyThreadSafetyMode.ExecutionAndPublication);

  private static readonly Lazy<IReadOnlyDictionary<string, string>> _byToken =
    new(() => _entries.Value.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal), LazyThreadSafetyMode.ExecutionAndPublication);

  /// <summary>The tokens and the SQL fragments they stand for.</summary>
  public static IReadOnlyDictionary<string, string> Tokens => _byToken.Value;

  /// <summary>Replaces every token in <paramref name="sql"/>; text without a token is returned as is.</summary>
  public static string Apply(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    if (!sql.Contains("__", StringComparison.Ordinal)) {
      return sql;
    }
    foreach (var (token, value) in _entries.Value) {
      sql = sql.Replace(token, value, StringComparison.Ordinal);
    }
    return sql;
  }

  /// <summary>
  /// Tokens written in <paramref name="sql"/> that nothing defines: a typo, or a constant not yet added to the
  /// file. The schema placeholders are not reported.
  /// </summary>
  public static IReadOnlyList<string> UnknownTokens(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    var unknown = new SortedSet<string>(StringComparer.Ordinal);
    foreach (Match match in _tokenPattern().Matches(sql)) {
      var token = match.Value;
      if (!ReservedTokens.Contains(token) && !Tokens.ContainsKey(token)) {
        unknown.Add(token);
      }
    }
    return [.. unknown];
  }

  /// <summary>
  /// Parses the constants file: <c>#</c> comments and blank lines are skipped; every other line is
  /// <c>__TOKEN__ = value</c>. A reserved, malformed or duplicate token, an empty value, or a token that is a
  /// substring of another (one substitution would eat the other) is a defect in the file and throws.
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
      var separator = line.IndexOf('=', StringComparison.Ordinal);
      if (separator < 1) {
        throw new InvalidDataException($"constants.txt line {lineNumber}: expected '__TOKEN__ = value', found '{line}'.");
      }
      var token = line[..separator].Trim();
      var value = line[(separator + 1)..].Trim();
      if (!_tokenPattern().IsMatch(token) || _tokenPattern().Match(token).Value != token) {
        throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' is not an UPPER_SNAKE name between double underscores.");
      }
      if (ReservedTokens.Contains(token)) {
        throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' is the schema placeholder, not a constant.");
      }
      if (value.Length == 0) {
        throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' has no value.");
      }
      if (!seen.Add(token)) {
        throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' is defined twice.");
      }
      entries.Add(new KeyValuePair<string, string>(token, value));
    }
    foreach (var a in entries) {
      foreach (var b in entries) {
        if (a.Key != b.Key && b.Key.Contains(a.Key, StringComparison.Ordinal)) {
          throw new InvalidDataException($"constants.txt: '{a.Key}' is a substring of '{b.Key}'; substituting one would eat the other.");
        }
      }
    }
    return entries;
  }

  private static IReadOnlyList<KeyValuePair<string, string>> _load() {
    using var stream = typeof(MigrationConstants).Assembly.GetManifestResourceStream(RESOURCE_NAME)
      ?? throw new InvalidOperationException($"Embedded resource '{RESOURCE_NAME}' is missing; the migrations' constants file is part of the package.");
    using var reader = new StreamReader(stream);
    return Parse(reader.ReadToEnd());
  }

  [GeneratedRegex("__[A-Z][A-Z0-9_]*__")]
  private static partial Regex _tokenPattern();
}
