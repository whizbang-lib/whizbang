using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Every redefinition of the claim must keep <c>SET plan_cache_mode = force_custom_plan</c>.
/// </summary>
/// <remarks>
/// <para>
/// Migration 157 added that attribute for a measured reason: the claim's tables are empty between
/// bursts, a session that polled while they were empty kept generic plans made for empty tables,
/// and once the tables filled those plans scanned them whole on every poll until the next analyze.
/// A poll that takes well under a second with fresh plans did not finish inside the command timeout
/// with the cached ones.
/// </para>
/// <para>
/// The attribute sits after <c>LANGUAGE plpgsql</c>, which is exactly where a redefinition drops it:
/// copy the body, terminate at the language clause, and the function is still valid SQL and still
/// passes every behavioural test while quietly losing the guarantee. That happened in this branch --
/// a redefinition ended at <c>$$ LANGUAGE plpgsql</c> with neither the attribute nor a semicolon,
/// applied anyway as the file's last statement, and no test noticed.
/// </para>
/// <para>
/// So the rule is on the text, because the cost only shows up on a service that has been idle and
/// then gets busy, which no unit test reproduces.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Migrations")]
[Category("Shard4")]
public class ClaimKeepsItsPlanCacheModeTests {

  private static IReadOnlyList<(string Migration, string Sql)> _definitionsOf(string function) =>
    new Whizbang.Data.Postgres.PostgresMigrationProvider(
        typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly, "__SCHEMA__")
      .GetMigrations()
      .Where(m => m.Sql.Contains($"FUNCTION __SCHEMA__.{function}(", StringComparison.Ordinal))
      .Select(m => (m.Name, m.Sql))
      .ToList();

  [Test]
  public async Task OnceTheClaimGainedForceCustomPlan_NoLaterDefinitionDropsItAsync() {
    // Monotonic rather than universal: the attribute arrived partway through the corpus, so the
    // definitions that predate it are history and not defects. What must hold is that no
    // redefinition AFTER it appeared quietly loses it.
    var definitions = _definitionsOf("claim_work")
      .OrderBy(d => d.Migration, StringComparer.Ordinal)
      .ToList();

    await Assert.That(definitions).IsNotEmpty()
      .Because("the claim has to be findable, or this rule asserts nothing.");

    var first = definitions.FindIndex(
      d => d.Sql.Contains("SET plan_cache_mode = force_custom_plan", StringComparison.Ordinal));
    await Assert.That(first).IsGreaterThan(-1)
      .Because("some definition must carry the attribute, or the property this rule protects is "
        + "already gone and the rule is vacuous.");

    var dropped = definitions.Skip(first)
      .Where(d => !d.Sql.Contains("SET plan_cache_mode = force_custom_plan", StringComparison.Ordinal))
      .Select(d => d.Migration)
      .ToList();

    await Assert.That(dropped).IsEmpty()
      .Because("the claim runs under force_custom_plan because generic plans made while its tables "
        + "were empty scanned them whole once they filled -- a poll that runs in well under a "
        + "second with fresh plans did not finish inside the command timeout with cached ones. A "
        + "redefinition that ends at the language clause drops the attribute, stays valid SQL, and "
        + "passes every behavioural test. Dropped in: " + string.Join(", ", dropped));
  }

  [Test]
  public async Task NoFunctionBodyIsLeftUnterminatedAsync() {
    // The same slip that drops the attribute also drops the semicolon. A trailing statement still
    // applies as the file's last one, so this is invisible until a later statement is appended
    // after it -- and then the failure is a syntax error pointing at the NEXT statement.
    var provider = new Whizbang.Data.Postgres.PostgresMigrationProvider(
      typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly, "__SCHEMA__");

    var unterminated = provider.GetMigrations()
      .Where(m => System.Text.RegularExpressions.Regex.IsMatch(
        m.Sql, @"\$\$ LANGUAGE plpgsql[ \t]*(\r?\n|$)"))
      .Select(m => m.Name)
      .ToList();

    await Assert.That(unterminated).IsEmpty()
      .Because("a function body must end at a terminator -- a semicolon, or an attribute clause "
        + "and then a semicolon. Ending bare leaves the statement open, which only fails once "
        + "something follows it. Found in: " + string.Join(", ", unterminated));
  }
}
