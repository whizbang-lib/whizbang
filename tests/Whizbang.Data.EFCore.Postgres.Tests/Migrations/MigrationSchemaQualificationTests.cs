// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Locks the multi-schema ownership contract: every framework table belongs to the SERVICE schema,
/// so two services sharing one database never share framework state.
/// </summary>
/// <remarks>
/// <para>
/// An unqualified <c>CREATE TABLE IF NOT EXISTS wh_x</c> resolves through the connection's
/// <c>search_path</c>, which defaults to <c>"$user", public</c> — so the table lands in
/// <c>public</c> and the SECOND service to migrate finds it already there and skips. The two
/// services then share one table. For <c>wh_settings</c>, whose <c>setting_key</c> is the primary
/// key, that makes divergence impossible: co-located services cannot hold different values for
/// <c>debug_mode</c> or any retention knob.
/// </para>
/// <para>
/// This is a corpus-wide structural check rather than a per-table one deliberately. The four
/// tables that regressed (<c>wh_log</c>, <c>wh_settings</c>, <c>wh_dead_letters</c>,
/// <c>wh_dead_letter_summary</c>) were written bare years after migration 000 established the
/// qualified form, and nothing caught the drift because every test runs on <c>public</c>, where
/// the qualified and bare forms are indistinguishable. A rule that only a human reads is a rule
/// that erodes; this makes the next omission a build failure.
/// </para>
/// </remarks>
/// <docs>contributors/data-engines/writing-migrations</docs>
[Category("Migrations")]
[Category("Shard4")]
public class MigrationSchemaQualificationTests {

  // Substituting the placeholder with itself is an identity replace, so this yields the migration
  // corpus verbatim — the form the rule is written against.
  private static IReadOnlyList<MigrationScript> _rawCorpus() =>
    new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "__SCHEMA__")
      .GetMigrations();

  // Line comments carry prose ABOUT the DDL ("CREATE TABLE IF NOT EXISTS is a no-op on re-run"),
  // which would otherwise read as a bare create.
  private static string _stripLineComments(string sql) =>
    Regex.Replace(sql, "--[^\n]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));

  [Test]
  public async Task EveryTableCreate_IsSchemaQualifiedAsync() {
    var bare = new List<string>();
    var rx = new Regex(
      @"CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+(?!__SCHEMA__\.)([a-zA-Z_][a-zA-Z0-9_]*)",
      RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    foreach (var migration in _rawCorpus()) {
      foreach (Match m in rx.Matches(_stripLineComments(migration.Sql))) {
        bare.Add($"{migration.Name}: {m.Groups[1].Value}");
      }
    }

    await Assert.That(bare).IsEmpty()
      .Because("a bare CREATE TABLE lands in public via search_path, so co-located services share "
        + "the table — and for wh_settings, whose setting_key is the PK, they cannot even hold "
        + "different values. Qualify it with __SCHEMA__.");
  }

  [Test]
  public async Task EveryIndexCreate_TargetsAQualifiedTableAsync() {
    var bare = new List<string>();
    var rx = new Regex(
      @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\s+\w+\s+ON\s+(?!__SCHEMA__\.)([a-zA-Z_][a-zA-Z0-9_]*)",
      RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    foreach (var migration in _rawCorpus()) {
      foreach (Match m in rx.Matches(_stripLineComments(migration.Sql))) {
        bare.Add($"{migration.Name}: {m.Groups[1].Value}");
      }
    }

    await Assert.That(bare).IsEmpty()
      .Because("an index on a bare table name binds to whichever copy search_path resolves to, so "
        + "it can silently index another service's table");
  }
  // ==========================================================================================
  // Catalog lookups ask about the migration's own schema
  // ==========================================================================================
  //
  // A guard that asks the catalog whether a column or table already exists has to ask about the
  // schema its DDL alters, and so does a function that finds tables at run time. current_schema()
  // answers for the CALLER's search_path, which on a pooled runtime connection is public whatever
  // schema the service owns; a literal '__SCHEMA__' becomes the QUOTED identifier on the EF Core
  // driver, which no catalog row equals. The form that answers on both drivers is
  // replace('__SCHEMA__', '"', '') (or a regclass built from __SCHEMA__.wh_x).
  //
  // Shipped migrations are never edited (migrations README), so these rules hold the files written
  // after the last shipped one, and the latest definition of every function, which is what runs.
  // The shipped guards that ask current_schema() are made correct by the runners instead: the EF Core
  // runner sets search_path to the schema it migrates for each migration, and the Dapper driver takes
  // its schema from current_schema() to begin with.

  /// <summary>The highest-numbered migration that shipped before these rules; it and every earlier file are frozen.</summary>
  private const int LAST_SHIPPED_MIGRATION = 212;

  private static IEnumerable<MigrationScript> _newMigrations() =>
    _rawCorpus().Where(m => int.Parse(m.Name.AsSpan(0, 3), System.Globalization.CultureInfo.InvariantCulture) > LAST_SHIPPED_MIGRATION);

  [Test]
  public async Task NoNewMigration_AsksForTheCallersSchemaAsync() {
    var offenders = new List<string>();
    var rx = new Regex(@"\bcurrent_schema\s*\(", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    foreach (var migration in _newMigrations()) {
      var code = _stripComments(migration.Sql);
      foreach (Match m in rx.Matches(code)) {
        offenders.Add($"{migration.Name}: line {_lineOf(code, m.Index)}");
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("current_schema() is the caller's search_path, which is public on a pooled connection "
        + "whatever schema the service owns; use replace('__SCHEMA__', '\"', '')");
  }

  [Test]
  public async Task NoNewMigration_ComparesTheSchemaPlaceholderAsTextAsync() {
    var offenders = new List<string>();
    var rx = new Regex(@"(?<!replace\(\s*)'__SCHEMA__'", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    foreach (var migration in _newMigrations()) {
      var code = _stripComments(migration.Sql);
      foreach (Match m in rx.Matches(code)) {
        offenders.Add($"{migration.Name}: line {_lineOf(code, m.Index)}");
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("the EF Core driver substitutes the QUOTED schema, so '__SCHEMA__' compared as text never "
        + "equals a catalog row there; wrap it as replace('__SCHEMA__', '\"', '')");
  }

  [Test]
  public async Task EveryNewInformationSchemaLookup_NamesItsSchemaAsync() {
    var offenders = _lookupsMissing(@"\binformation_schema\.\w+", "table_schema");

    await Assert.That(offenders).IsEmpty()
      .Because("a lookup by table name alone answers for whichever schema holds a table of that name; "
        + "another service's, in a shared database");
  }

  [Test]
  public async Task EveryNewPgIndexesLookup_NamesItsSchemaAsync() {
    var offenders = _lookupsMissing(@"\bpg_indexes\b", "schemaname");

    await Assert.That(offenders).IsEmpty()
      .Because("an index name is unique per schema, not per database");
  }

  /// <summary>
  /// The definition of each function that a database ends on (the last file that defines it) names its
  /// own schema, whichever file that is.
  /// </summary>
  /// <remarks>
  /// A function runs long after its migration, on a pooled connection the runner's search_path never
  /// reaches, so a shipped definition that asks current_schema() stays wrong until a later file
  /// redefines it. Keyed by name: every overload a later definition leaves behind was dropped where
  /// the signature changed (rule 5), so the last definition of a name is the one that runs.
  /// </remarks>
  [Test]
  public async Task TheLatestDefinitionOfEveryFunction_NamesItsOwnSchemaAsync() {
    var latest = new Dictionary<string, (string Migration, string Body)>(StringComparer.OrdinalIgnoreCase);
    var definition = new Regex(
      @"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:__SCHEMA__\.)?(\w+)\s*\(", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
    var bodyStart = new Regex(@"\bAS\s+(\$\w*\$)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    foreach (var migration in _rawCorpus()) {
      var sql = migration.Sql;
      foreach (Match m in definition.Matches(sql)) {
        var open = bodyStart.Match(sql, m.Index + m.Length);
        var close = open.Success ? sql.IndexOf(open.Groups[1].Value, open.Index + open.Length, StringComparison.Ordinal) : -1;
        if (close < 0) {
          // A definition spelled inside dynamic SQL, whose body is not delimited here; its DO block is the code.
          continue;
        }
        latest[m.Groups[1].Value] = (migration.Name, sql[(open.Index + open.Length)..close]);
      }
    }

    var callersSchema = new Regex(@"\bcurrent_schema\s*\(|(?<!replace\(\s*)'__SCHEMA__'", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
    var offenders = latest
      .Where(f => callersSchema.IsMatch(_stripComments(f.Value.Body)))
      .Select(f => $"{f.Key} (last defined in {f.Value.Migration})")
      .Order(StringComparer.Ordinal)
      .ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("a run-time function asking current_schema() looks for its tables in the caller's "
        + "search_path, public on a pooled connection; redefine it whole in a new migration with "
        + "replace('__SCHEMA__', '\"', '')");
  }

  // Each lookup's own clause: from the view to the end of the subquery or statement it sits in.
  private static List<string> _lookupsMissing(string lookupPattern, string requiredColumn) {
    var offenders = new List<string>();
    var rx = new Regex(lookupPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

    foreach (var migration in _newMigrations()) {
      var code = _stripComments(migration.Sql);
      foreach (Match m in rx.Matches(code)) {
        var clause = _enclosingClause(code, m.Index);
        if (!clause.Contains(requiredColumn, StringComparison.OrdinalIgnoreCase)) {
          offenders.Add($"{migration.Name}: line {_lineOf(code, m.Index)}");
        }
      }
    }

    return offenders;
  }

  private static string _enclosingClause(string code, int start) {
    var depth = 0;
    var end = start;
    for (; end < code.Length; end++) {
      var c = code[end];
      if (c == ';') {
        break;
      }
      if (c == '(') {
        depth++;
      } else if (c == ')' && --depth < 0) {
        break;
      }
    }
    return code[start..end];
  }

  // Block comments keep their line breaks, so a reported line is the line in the file.
  private static string _stripComments(string sql) =>
    Regex.Replace(_stripLineComments(sql), @"/\*.*?\*/", m => new string('\n', m.Value.AsSpan().Count('\n')),
      RegexOptions.Singleline, TimeSpan.FromSeconds(5));

  private static int _lineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
