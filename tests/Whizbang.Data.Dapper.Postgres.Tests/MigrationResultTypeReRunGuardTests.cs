// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Every earlier migration runs again over a schema the later ones already migrated: an in-order replay starts each
/// function's first definer against the latest definition, and an upgrade re-runs any file whose hash changed.
/// PostgreSQL refuses <c>CREATE OR REPLACE FUNCTION</c> that changes an existing function's result type (42P13), so a
/// definer whose result differs from the latest one must drop the function before it creates it. The simpler way to
/// keep this is never to change a function's result under its name: a function that returns something new gets a new
/// name, and the old one keeps its result for callers that still use it (#2350, migration 449).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/449_RedeliveredCopiesKeepTheOriginsOrder.sql</code-under-test>
/// <docs>contributors/data-engines/writing-migrations#a-functions-result-never-changes-under-its-name</docs>
[Category("Unit")]
public partial class MigrationResultTypeReRunGuardTests {
  /// <summary>
  /// Definers that change a result without dropping first, each checked and found safe, with the reason.
  /// Keyed by migration number and function name.
  /// </summary>
  private static readonly Dictionary<string, string> _reviewed = new(StringComparer.Ordinal) {
    ["078 get_stream_events"] =
      "shipped and frozen, so an upgrade never re-runs it alone; in an in-order replay it runs right after 072, which "
      + "drops get_stream_events first and creates the same result 078 creates, and 139 then drops it again",
  };

  [GeneratedRegex(@"^(\d{3})_.+\.sql$")]
  private static partial Regex _numbered();

  [GeneratedRegex(@"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+(?:__SCHEMA__\.)?(\w+)\s*\((.*?)\)\s*RETURNS\s+(.*?)\s+(?:AS\s+\$|LANGUAGE\b)",
    RegexOptions.Singleline | RegexOptions.IgnoreCase)]
  private static partial Regex _definition();

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex _lineComment();

  [GeneratedRegex(@"\s+")]
  private static partial Regex _whitespace();

  [GeneratedRegex(@"\s*([(),])\s*")]
  private static partial Regex _punctuation();

  [GeneratedRegex(@"\s+DEFAULT\s+|\s*=\s*", RegexOptions.IgnoreCase)]
  private static partial Regex _default();

  private static string _migrationsDirectory() => Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Whizbang.Data.Postgres", "Migrations"));

  private sealed record Definer(string Number, string Function, string Arguments, string Result, bool DropsFirst);

  [Test]
  public async Task EveryDefinerWhoseResultDiffersFromTheLatest_DropsTheFunctionFirstAsync() {
    var definers = await _definersAsync();
    var unsafeDefiners = new List<string>();
    foreach (var signature in definers.GroupBy(d => (d.Function, d.Arguments))) {
      var latest = signature.Last().Result;
      foreach (var definer in signature.Where(d => d.Result != latest && !d.DropsFirst)) {
        if (!_reviewed.ContainsKey($"{definer.Number} {definer.Function}")) {
          unsafeDefiners.Add($"{definer.Number} {definer.Function}({definer.Arguments}): returns {definer.Result}, the latest returns {latest}");
        }
      }
    }

    await Assert.That(unsafeDefiners).IsEmpty()
      .Because("re-run over the latest definition, CREATE OR REPLACE with another result fails with 42P13; give the "
             + "new result a new name instead");
  }

  [Test]
  public async Task TheGuard_FindsResultsThatChanged_AndEveryReviewedEntryIsStillOneAsync() {
    var definers = await _definersAsync();
    var changed = definers.GroupBy(d => (d.Function, d.Arguments))
      .Where(s => s.Select(d => d.Result).Distinct(StringComparer.Ordinal).Count() > 1)
      .ToList();
    var stillUnsafe = changed.SelectMany(s => s.Where(d => d.Result != s.Last().Result && !d.DropsFirst))
      .Select(d => $"{d.Number} {d.Function}")
      .ToHashSet(StringComparer.Ordinal);

    await Assert.That(definers.Count).IsGreaterThan(500).Because("a guard over no definitions proves nothing");
    await Assert.That(changed.Count).IsGreaterThan(5)
      .Because("the history holds results that changed (each behind a drop), so the scan must find them");
    await Assert.That(_reviewed.Keys.Where(k => !stillUnsafe.Contains(k))).IsEmpty()
      .Because("a reviewed entry that no longer matches anything is stale");
  }

  private static async Task<List<Definer>> _definersAsync() {
    var definers = new List<Definer>();
    var files = Directory.GetFiles(_migrationsDirectory(), "*.sql")
      .Select(f => Path.GetFileName(f))
      .Where(f => !f.EndsWith(".down.sql", StringComparison.Ordinal) && _numbered().IsMatch(f))
      .Order(StringComparer.Ordinal);
    foreach (var file in files) {
      var sql = _lineComment().Replace(await File.ReadAllTextAsync(Path.Combine(_migrationsDirectory(), file)), "");
      foreach (Match m in _definition().Matches(sql)) {
        var function = m.Groups[1].Value.ToLowerInvariant();
        definers.Add(new Definer(
          file[..3], function, _arguments(m.Groups[2].Value), _normalized(m.Groups[3].Value),
          _dropsBefore(sql[..m.Index], function)));
      }
    }
    return definers;
  }

  private static string _normalized(string text) =>
    _punctuation().Replace(_whitespace().Replace(text.Trim(), " "), "$1").ToUpperInvariant();

  /// <summary>The argument types only: names, modes and defaults do not make a function another one.</summary>
  private static string _arguments(string arguments) => string.Join(",", arguments
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(a => {
      var tokens = _default().Split(a)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
      if (tokens.Count > 0 && tokens[0].ToUpperInvariant() is "IN" or "OUT" or "INOUT") {
        tokens.RemoveAt(0);
      }
      return _normalized(string.Join(' ', tokens.Count > 1 ? tokens.Skip(1) : tokens));
    }));

  private static bool _dropsBefore(string before, string function) =>
    Regex.IsMatch(before, $@"DROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?(?:__SCHEMA__\.)?{function}\b", RegexOptions.IgnoreCase)
    || before.Contains($"drop_all_overloads('{function}')", StringComparison.OrdinalIgnoreCase);
}
