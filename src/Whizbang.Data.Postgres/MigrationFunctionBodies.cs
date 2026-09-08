using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Whizbang.Data.Postgres;

/// <summary>
/// One <c>CREATE [OR REPLACE] FUNCTION</c> found in a migration: the bare lower-cased function name and
/// its dollar-quoted body, whitespace-normalized so it can be compared with what the database holds in
/// <c>pg_proc.prosrc</c>.
/// </summary>
/// <param name="Name">Bare lower-cased function name (schema qualifier stripped).</param>
/// <param name="NormalizedBody">The body between the dollar quotes, passed through <see cref="MigrationFunctionBodies.Normalize"/>.</param>
/// <docs>operations/infrastructure/migrations</docs>
public sealed record MigrationFunctionBody(string Name, string NormalizedBody);

/// <summary>
/// The last-word definition of one function across the ordered migration set: the file that defines it
/// last and the normalized body that file gives it.
/// </summary>
/// <param name="FileName">The migration file whose definition is the last word.</param>
/// <param name="NormalizedBody">That definition's body, normalized.</param>
/// <docs>operations/infrastructure/migrations</docs>
public sealed record MigrationLastWord(string FileName, string NormalizedBody);

/// <summary>
/// Extracts function bodies from migration SQL so the schema initializer can compare the DATABASE against
/// the FILES. The ledger's content hashes only describe the files: a database whose function was reverted
/// to an earlier definition by a replay that predates the redefinition closure reports "hash unchanged" on
/// every startup while the function stays generations old. Comparing each framework function's deployed
/// body with its last-word migration body is what detects that state, and re-running the last-word file
/// is what heals it.
/// </summary>
/// <remarks>
/// Only dollar-quoted bodies (<c>$$ ... $$</c> or <c>$tag$ ... $tag$</c>) are extracted; a function whose
/// <c>CREATE</c> sits inside dynamic SQL (a <c>format(...)</c> with <c>%I</c> placeholders) is skipped, so
/// a placeholder body can never be mistaken for a stale one. Over-skipping only narrows the check.
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
public static partial class MigrationFunctionBodies {
  [GeneratedRegex(
    @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:__SCHEMA__\s*\.\s*|""?[A-Za-z_]\w*""?\s*\.\s*)?""?(?<name>[A-Za-z_]\w*)""?\s*\(",
    RegexOptions.IgnoreCase)]
  private static partial Regex _createFunction();

  [GeneratedRegex(@"\bAS\s+(?<tag>\$[A-Za-z_]*\$)", RegexOptions.IgnoreCase)]
  private static partial Regex _bodyOpener();

  [GeneratedRegex(@"\s+")]
  private static partial Regex _whitespaceRun();

  /// <summary>
  /// A retirement: <c>DROP FUNCTION [IF EXISTS] [schema.]name</c> or <c>drop_all_overloads('name')</c>.
  /// Applied in statement order with the definitions, so a drop that precedes a recreation in the same
  /// file (the signature-change pattern) is superseded by that recreation, while a drop with no later
  /// definition retires the function: it is neither stale nor missing.
  /// </summary>
  [GeneratedRegex(
    @"DROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?(?:__SCHEMA__\s*\.\s*|""?[A-Za-z_]\w*""?\s*\.\s*)?""?(?<name>[A-Za-z_]\w*)""?|drop_all_overloads\s*\(\s*'(?<name2>[A-Za-z_]\w*)'\s*\)",
    RegexOptions.IgnoreCase)]
  private static partial Regex _dropFunction();

  /// <summary>
  /// Collapses every run of whitespace to a single space and trims, so two renderings of the same body
  /// (different indentation, line endings, or a trailing newline) compare equal.
  /// </summary>
  public static string Normalize(string body) {
    ArgumentNullException.ThrowIfNull(body);
    return _whitespaceRun().Replace(body, " ").Trim();
  }

  /// <summary>
  /// Extracts every dollar-quoted function body in <paramref name="sql"/>, in order of appearance. A
  /// function defined more than once in the same text yields one entry per definition; callers wanting the
  /// last word take the last one.
  /// </summary>
  public static IReadOnlyList<MigrationFunctionBody> Extract(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    var positioned = ExtractWithPositions(sql);
    var results = new List<MigrationFunctionBody>(positioned.Count);
    foreach (var (_, body) in positioned) {
      results.Add(body);
    }
    return results;
  }

  /// <summary>
  /// <see cref="Extract"/> with each definition's character offset, so retirements found in the same
  /// file can be ordered against the definitions.
  /// </summary>
  public static IReadOnlyList<(int Position, MigrationFunctionBody Body)> ExtractWithPositions(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    var results = new List<(int, MigrationFunctionBody)>();
    var creates = _createFunction().Matches(sql);
    for (var i = 0; i < creates.Count; i++) {
      var create = creates[i];
      var nextCreateStart = i + 1 < creates.Count ? creates[i + 1].Index : sql.Length;
      var headerEnd = create.Index + create.Length;

      var opener = _bodyOpener().Match(sql, headerEnd);
      if (!opener.Success || opener.Index >= nextCreateStart) {
        continue; // no dollar-quoted body before the next definition (single-quoted or dynamic SQL)
      }

      var header = sql.Substring(create.Index, opener.Index - create.Index);
      if (header.Contains('%', StringComparison.Ordinal)) {
        continue; // a format() template, not a definition the database will hold verbatim
      }

      var tag = opener.Groups["tag"].Value;
      var bodyStart = opener.Index + opener.Length;
      var bodyEnd = sql.IndexOf(tag, bodyStart, StringComparison.Ordinal);
      if (bodyEnd < 0) {
        continue; // unterminated body: not a definition we can compare
      }

      var name = create.Groups["name"].Value.ToLowerInvariant();
      results.Add((create.Index, new MigrationFunctionBody(name, Normalize(sql.Substring(bodyStart, bodyEnd - bodyStart)))));
    }
    return results;
  }

  /// <summary>
  /// Resolves the last-word definition of every function across <paramref name="migrationsInOrder"/>: for
  /// each function name, the LAST file (in the given order) that defines it, and the body that file gives
  /// it. Later entries replace earlier ones; within one file the last definition wins.
  /// </summary>
  public static IReadOnlyDictionary<string, MigrationLastWord> LastWord(
      IEnumerable<(string Name, string Sql)> migrationsInOrder) {
    ArgumentNullException.ThrowIfNull(migrationsInOrder);
    var lastWord = new Dictionary<string, MigrationLastWord>(StringComparer.Ordinal);
    foreach (var (name, sql) in migrationsInOrder) {
      // Definitions and retirements are applied in the order they appear in the file, so a drop that
      // precedes a recreation is superseded and a drop with no later definition retires the function.
      var events = new List<(int Position, bool IsDrop, string FunctionName, string Body)>();
      foreach (var body in ExtractWithPositions(sql)) {
        events.Add((body.Position, false, body.Body.Name, body.Body.NormalizedBody));
      }
      foreach (Match drop in _dropFunction().Matches(sql)) {
        var dropped = drop.Groups["name"].Success ? drop.Groups["name"].Value : drop.Groups["name2"].Value;
        events.Add((drop.Index, true, dropped.ToLowerInvariant(), ""));
      }
      events.Sort((a, b) => a.Position.CompareTo(b.Position));
      foreach (var (_, isDrop, functionName, body) in events) {
        if (isDrop) {
          lastWord.Remove(functionName);
        } else {
          lastWord[functionName] = new MigrationLastWord(name, body);
        }
      }
    }
    return lastWord;
  }

  /// <summary>
  /// Compares deployed bodies against their last words and returns the migration files that must re-run:
  /// one per function whose deployed body differs from its last-word body, or that is missing from the
  /// database. Functions present more than once (duplicate overloads) are skipped here; the duplicate-overload
  /// sweep owns those. The result preserves the order of <paramref name="lastWords"/>' enumeration and has no
  /// duplicates.
  /// </summary>
  /// <param name="lastWords">The last-word definitions from <see cref="LastWord"/>.</param>
  /// <param name="deployed">Deployed bodies by function name: every <c>pg_proc.prosrc</c> row for the name (raw, un-normalized).</param>
  /// <param name="stale">Receives, per stale file, the function names that made it stale (for the log line).</param>
  public static IReadOnlyList<string> FilesToRerun(
      IReadOnlyDictionary<string, MigrationLastWord> lastWords,
      IReadOnlyDictionary<string, IReadOnlyList<string>> deployed,
      IDictionary<string, List<string>> stale) {
    ArgumentNullException.ThrowIfNull(lastWords);
    ArgumentNullException.ThrowIfNull(deployed);
    ArgumentNullException.ThrowIfNull(stale);
    var files = new List<string>();
    foreach (var (functionName, word) in lastWords) {
      if (!deployed.TryGetValue(functionName, out var bodies) || bodies.Count == 0) {
        _markStale(files, stale, word.FileName, functionName + " (missing)");
        continue;
      }
      if (bodies.Count > 1) {
        continue; // duplicate overloads: the overload sweep re-runs the defining files itself
      }
      if (!string.Equals(Normalize(bodies[0]), word.NormalizedBody, StringComparison.Ordinal)) {
        _markStale(files, stale, word.FileName, functionName);
      }
    }
    return files;
  }

  private static void _markStale(List<string> files, IDictionary<string, List<string>> stale, string fileName, string functionName) {
    if (!files.Contains(fileName)) {
      files.Add(fileName);
    }
    if (!stale.TryGetValue(fileName, out var names)) {
      names = new List<string>();
      stale[fileName] = names;
    }
    names.Add(functionName);
  }
}
