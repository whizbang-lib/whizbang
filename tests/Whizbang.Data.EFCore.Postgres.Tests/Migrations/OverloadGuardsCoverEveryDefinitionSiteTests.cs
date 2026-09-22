using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Locks the precondition the duplicate-overload sweep is built on: for a function name that the
/// corpus defines at more than one parameter count, EVERY file defining it clears the old overloads
/// first.
/// </summary>
/// <remarks>
/// <para>
/// <c>CREATE OR REPLACE FUNCTION</c> replaces a function only when the parameter list matches. At a
/// different count it creates a SECOND function beside the first, and every unqualified reference to
/// the name is then ambiguous (42725) — including the defining file's own bare
/// <c>COMMENT ON FUNCTION</c>, which is how this failed: a redefinition-closure replay re-ran an
/// unguarded file, the file added an overload beside the current one, and its own comment threw. A
/// startup pass is all-or-nothing, so the migration that triggered the closure never applied, and
/// the service came up against a schema missing the table its code queries.
/// </para>
/// <para>
/// The sweep in <see cref="DuplicateOverloadSweepTests"/> repairs duplicates by forcing the defining
/// files back into the run, which only works because "the fixed <c>drop_all_overloads</c> at the top
/// of each clears every overload". That claim is a property of the corpus, not of the sweep, and
/// nothing checked it: the sweep's own test exercises <c>cleanup_stale_instances</c>, a name that
/// happens to be guarded everywhere, while the corpus carried 31 unguarded sites across 13 names.
/// The repair path therefore re-ran the very files that create the duplicate it was repairing.
/// </para>
/// <para>
/// Scoped to multi-arity names on purpose. A name defined at one arity everywhere cannot gain a
/// duplicate, so requiring a drop there would be ceremony; the moment a migration introduces a
/// second arity, every prior site for that name falls under the rule and this test starts failing
/// until they are guarded.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations</code-under-test>
/// <docs>contributors/data-engines/writing-migrations</docs>
[Category("Migrations")]
[Category("Shard4")]
public class OverloadGuardsCoverEveryDefinitionSiteTests {

  // The arities 025/138 and 145-onward give claim_orphaned_inbox; see the scanner meta-test.
  // 167 adds the tenth parameter, p_include_idle: acquisition has to agree with the re-offer about whether the
  // idle band may be taken, or the claim leases idle rows it will then withhold.
  private static readonly int[] _claimOrphanedInboxArities = [8, 9, 10];

  private static readonly Regex _definition = new(
    @"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+__SCHEMA__\.([a-z_0-9]+)\s*\(",
    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

  // Substituting the placeholder with itself is an identity replace, so this yields the migration
  // corpus verbatim — the form the rule is written against.
  private static IReadOnlyList<MigrationScript> _rawCorpus() =>
    new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "__SCHEMA__")
      .GetMigrations();

  // Prose about a definition ("145 replaced claim_work(...)") is not a definition.
  private static string _stripLineComments(string sql) =>
    Regex.Replace(sql, "--[^\n]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));

  private sealed record Definition(string Migration, string Function, int Arity, int Offset);

  /// <summary>
  /// The parameter count of the list opening at <paramref name="openParen"/>: top-level commas plus
  /// one, and zero for an empty list. Nested parentheses (a DEFAULT expression, a type modifier)
  /// hold no top-level comma, so they are skipped rather than counted.
  /// </summary>
  private static int _arity(string sql, int openParen) {
    var depth = 0;
    var commas = 0;
    var sawContent = false;
    for (var i = openParen; i < sql.Length; i++) {
      var ch = sql[i];
      if (ch == '(') {
        depth++;
      } else if (ch == ')') {
        depth--;
        if (depth == 0) {
          return sawContent ? commas + 1 : 0;
        }
      } else if (ch == ',' && depth == 1) {
        commas++;
      } else if (depth == 1 && !char.IsWhiteSpace(ch)) {
        sawContent = true;
      }
    }
    return sawContent ? commas + 1 : 0;
  }

  private static IReadOnlyList<Definition> _definitions() {
    var found = new List<Definition>();
    foreach (var migration in _rawCorpus()) {
      var sql = _stripLineComments(migration.Sql);
      foreach (Match m in _definition.Matches(sql)) {
        var open = sql.IndexOf('(', m.Index);
        found.Add(new Definition(migration.Name, m.Groups[1].Value, _arity(sql, open), m.Index));
      }
    }
    return found;
  }

  private static IReadOnlySet<string> _multiArityNames() =>
    _definitions()
      .GroupBy(d => d.Function, StringComparer.Ordinal)
      .Where(g => g.Select(d => d.Arity).Distinct().Count() > 1)
      .Select(g => g.Key)
      .ToHashSet(StringComparer.Ordinal);

  [Test]
  public async Task EveryMultiArityFunction_ClearsItsOverloadsAtEveryDefinitionSiteAsync() {
    var multi = _multiArityNames();
    var byMigration = _rawCorpus().ToDictionary(m => m.Name, m => m.Sql, StringComparer.Ordinal);

    var unguarded = _definitions()
      .Where(d => multi.Contains(d.Function)
        && !byMigration[d.Migration].Contains(
          $"drop_all_overloads('{d.Function}')", StringComparison.Ordinal))
      .Select(d => $"{d.Migration}: {d.Function}({d.Arity} args)")
      .Distinct(StringComparer.Ordinal)
      .Order(StringComparer.Ordinal)
      .ToList();

    await Assert.That(unguarded).IsEmpty()
      .Because("CREATE OR REPLACE at a different parameter count ADDS an overload instead of "
        + "replacing one, so a replay of an unguarded file leaves two functions of the same name "
        + "and every unqualified reference — this file's own COMMENT ON FUNCTION included — fails "
        + "with 42725, aborting the whole startup pass. Add "
        + "SELECT __SCHEMA__.drop_all_overloads('<name>'); above the definition.");
  }

  [Test]
  public async Task TheGuard_PrecedesTheDefinitionItProtectsAsync() {
    var multi = _multiArityNames();
    var byMigration = _rawCorpus().ToDictionary(m => m.Name, m => m.Sql, StringComparer.Ordinal);
    var late = new List<string>();

    foreach (var d in _definitions().Where(d => multi.Contains(d.Function))) {
      var sql = byMigration[d.Migration];
      var guard = sql.IndexOf($"drop_all_overloads('{d.Function}')", StringComparison.Ordinal);
      var create = sql.IndexOf(
        $"CREATE OR REPLACE FUNCTION __SCHEMA__.{d.Function}(", StringComparison.Ordinal);
      if (guard >= 0 && create >= 0 && guard > create) {
        late.Add($"{d.Migration}: {d.Function}");
      }
    }

    await Assert.That(late.Distinct(StringComparer.Ordinal)).IsEmpty()
      .Because("a drop that runs after the create removes the function the file just defined, so "
        + "the guard has to come first.");
  }

  // ============================================================================
  // meta-tests — a corpus-derived rule passes vacuously the moment the derivation
  // stops finding anything, and that is exactly the failure this class exists to
  // prevent. These fail if the machinery stops seeing the corpus.
  // ============================================================================

  [Test]
  public async Task TheCorpus_DefinesFunctionsAtMoreThanOneArity_SoTheRuleIsNotVacuousAsync() {
    // If this ever legitimately reaches zero, the rule above is satisfiable by an empty corpus and
    // this class must be deleted rather than left passing.
    await Assert.That(_multiArityNames().Count).IsGreaterThanOrEqualTo(10)
      .Because("the rule is scoped to multi-arity names; with none found it asserts nothing. "
        + "13 names carried more than one arity when this was written.");
  }

  [Test]
  public async Task TheDefinitionScanner_FindsTheKnownDefinitionsAsync() {
    var definitions = _definitions();
    await Assert.That(definitions.Count).IsGreaterThan(250)
      .Because("the corpus held 284 function definitions when this was written; a scanner "
        + "returning far fewer has stopped matching the corpus rather than proving it clean.");

    // claim_orphaned_inbox is the name that actually failed in a deployed service: 025/138 define
    // it at eight parameters, 145 onward at nine.
    var arities = definitions
      .Where(d => d.Function == "claim_orphaned_inbox")
      .Select(d => d.Arity)
      .Distinct()
      .Order()
      .ToList();
    await Assert.That(arities).IsEquivalentTo(_claimOrphanedInboxArities)
      .Because("the arity parser has to distinguish signatures that differ only by parameter "
        + "count — that difference is the whole hazard.");
  }

  [Test]
  public async Task TheArityParser_DoesNotCountCommasInsideNestedParenthesesAsync() {
    // A DEFAULT expression or a type modifier carries commas that belong to no parameter.
    const string SQL = "CREATE OR REPLACE FUNCTION __SCHEMA__.f(a NUMERIC(10,2), "
      + "b TEXT DEFAULT concat('x', 'y')) RETURNS VOID AS $$ BEGIN END; $$ LANGUAGE plpgsql;";
    await Assert.That(_arity(SQL, SQL.IndexOf('(', SQL.IndexOf(".f", StringComparison.Ordinal))))
      .IsEqualTo(2)
      .Because("two parameters, four commas — counting them all would report a different arity "
        + "for identical signatures and silently split a name into false 'multi-arity'.");
  }

  [Test]
  public async Task TheArityParser_ReportsZeroForAnEmptyParameterListAsync() {
    // reap_perspective_row_caps() gained a parameter in 113; the no-argument form has to read as 0
    // rather than 1, or the arity difference that makes it a hazard disappears.
    const string SQL = "CREATE OR REPLACE FUNCTION __SCHEMA__.f() RETURNS VOID AS $$ BEGIN END; $$;";
    await Assert.That(_arity(SQL, SQL.IndexOf('(', SQL.IndexOf(".f", StringComparison.Ordinal))))
      .IsEqualTo(0);
  }
}
