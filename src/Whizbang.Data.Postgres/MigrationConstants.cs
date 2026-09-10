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
    return [.. _tokenPattern().Matches(sql)
      .Select(match => match.Value)
      .Where(token => !ReservedTokens.Contains(token) && !Tokens.ContainsKey(token))
      .Distinct(StringComparer.Ordinal)
      .Order(StringComparer.Ordinal)];
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
      var entry = _parseDefinition(lineNumber, line);
      if (!seen.Add(entry.Key)) {
        throw new InvalidDataException($"constants.txt line {lineNumber}: '{entry.Key}' is defined twice.");
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
    var token = line[..separator].Trim();
    var value = line[(separator + 1)..].Trim();
    if (_tokenPattern().Match(token).Value != token) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' is not an UPPER_SNAKE name between double underscores.");
    }
    if (ReservedTokens.Contains(token)) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' is the schema placeholder, not a constant.");
    }
    if (value.Length == 0) {
      throw new InvalidDataException($"constants.txt line {lineNumber}: '{token}' has no value.");
    }
    return new KeyValuePair<string, string>(token, value);
  }

  /// <summary>A token that is a substring of another would be eaten by the other's substitution.</summary>
  private static void _rejectOverlaps(IReadOnlyList<KeyValuePair<string, string>> entries) {
    var overlap = entries
      .SelectMany(a => entries
        .Where(b => a.Key != b.Key && b.Key.Contains(a.Key, StringComparison.Ordinal))
        .Select(b => (Inner: a.Key, Outer: b.Key)))
      .FirstOrDefault();
    if (overlap != default) {
      throw new InvalidDataException($"constants.txt: '{overlap.Inner}' is a substring of '{overlap.Outer}'; substituting one would eat the other.");
    }
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
