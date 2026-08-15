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
}
