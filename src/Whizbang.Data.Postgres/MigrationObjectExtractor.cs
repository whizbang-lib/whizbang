using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The SQL objects a migration file defines or redefines, extracted for the ledger's
/// redefinition-closure expansion (see <see cref="MigrationRedefinitionClosure"/>). Either parsed
/// from the file's DDL or taken verbatim from an explicit <c>-- Objects:</c> header override
/// (required for files whose definitions hide inside dynamic SQL).
/// </summary>
/// <docs>operations/migrations</docs>
public sealed record MigrationObjectSet(IReadOnlyList<string> Objects, bool ExplicitOverride);

/// <summary>
/// Extracts the SQL object names a migration file defines. Several migrations may redefine the
/// same function over time; a hash-driven ledger replay that re-runs an EARLIER definition leaves
/// the database on that earlier definition unless every LATER same-object migration re-runs after
/// it. The extractor supplies the per-file object lists the closure expansion needs to make that
/// re-run automatic. Over-capture (e.g. a CREATE inside a function body's string) merely widens
/// the closure; under-capture is the dangerous direction, so recognition is deliberately broad.
/// </summary>
/// <docs>operations/migrations</docs>
public static partial class MigrationObjectExtractor {
  [GeneratedRegex(@"^\s*--\s*Objects:\s*(?<list>.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
  private static partial Regex _objectsHeader();

  [GeneratedRegex(
    @"CREATE\s+(?:OR\s+REPLACE\s+)?(?:UNIQUE\s+)?(?:FUNCTION|TABLE|VIEW|INDEX|TRIGGER)\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?(?:__SCHEMA__\s*\.\s*|""?[A-Za-z_]\w*""?\s*\.\s*)?""?(?<name>[A-Za-z_]\w*)""?",
    RegexOptions.IgnoreCase)]
  private static partial Regex _createStatement();

  [GeneratedRegex(
    @"DROP\s+(?:FUNCTION|TABLE|VIEW|INDEX|TRIGGER)\s+(?:IF\s+EXISTS\s+)?(?:__SCHEMA__\s*\.\s*|""?[A-Za-z_]\w*""?\s*\.\s*)?""?(?<name>[A-Za-z_]\w*)""?",
    RegexOptions.IgnoreCase)]
  private static partial Regex _dropStatement();

  [GeneratedRegex(
    @"ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?(?:__SCHEMA__\s*\.\s*|""?[A-Za-z_]\w*""?\s*\.\s*)?""?(?<name>[A-Za-z_]\w*)""?",
    RegexOptions.IgnoreCase)]
  private static partial Regex _alterStatement();

  /// <summary>
  /// Extracts the object names <paramref name="sql"/> defines. An explicit
  /// <c>-- Objects: name1, name2</c> (or <c>-- Objects: none</c>) header replaces parsing
  /// entirely; otherwise <c>CREATE [OR REPLACE] FUNCTION | TABLE | VIEW | INDEX | TRIGGER</c>, <c>ALTER TABLE</c>,
  /// and <c>DROP …</c> statements are recognized. Names are normalized: any schema qualifier
  /// (including the <c>__SCHEMA__.</c> placeholder) is stripped and the bare name lower-cased.
  /// </summary>
  public static MigrationObjectSet Extract(string sql) {
    ArgumentNullException.ThrowIfNull(sql);

    var header = _objectsHeader().Match(sql);
    if (header.Success) {
      var list = header.Groups["list"].Value.Trim();
      if (string.Equals(list, "none", StringComparison.OrdinalIgnoreCase)) {
        return new MigrationObjectSet([], ExplicitOverride: true);
      }
      var declared = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(n => n.ToLowerInvariant())
        .Distinct()
        .ToList();
      return new MigrationObjectSet(declared, ExplicitOverride: true);
    }

    var names = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (Match m in _createStatement().Matches(sql).Concat(_dropStatement().Matches(sql)).Concat(_alterStatement().Matches(sql))) {
      var name = m.Groups["name"].Value.ToLowerInvariant();
      if (seen.Add(name)) {
        names.Add(name);
      }
    }
    return new MigrationObjectSet(names, ExplicitOverride: false);
  }
}
