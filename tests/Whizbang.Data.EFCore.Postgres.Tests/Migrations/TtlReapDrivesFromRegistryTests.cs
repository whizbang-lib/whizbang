using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The maintenance sweep's row-expiry reap must choose its tables from the perspective registry,
/// like every other retention reaper, and not by asking the catalog which tables happen to have an
/// <c>expires_at</c> column.
/// </summary>
/// <remarks>
/// <para>
/// Every perspective table carries <c>expires_at</c>, because the column comes from the shared table
/// template rather than from the perspective declaring a row lifetime. Enumerating the catalog for
/// the column therefore selects EVERY perspective table, and the reap issues an unindexed delete
/// against each one. The rows never match -- a perspective that declares no lifetime never stamps
/// the column -- but not matching still costs a full scan of every table.
/// </para>
/// <para>
/// Measured on a deployed slot: 120 tables scanned for the 3 that declare a lifetime, 14,758 ms to
/// delete zero rows, which was 98 percent of the entire maintenance cycle. One service scanned 81
/// tables for a single declaring perspective.
/// </para>
/// <para>
/// The registry already records the declaration (<c>row_ttl_seconds</c>), and the three sibling
/// reapers -- enrolled rows, row caps, and reap-target collection -- all drive from it. This one was
/// left on the older pattern, so the rule is written as consistency with its siblings rather than as
/// a performance assertion: a catalog sweep is the defect, whatever it currently costs.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Migrations")]
[Category("Shard4")]
public class TtlReapDrivesFromRegistryTests {

  private static string _maintenanceBody() {
    var sql = new Whizbang.Data.Postgres.PostgresMigrationProvider(
        typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly, "__SCHEMA__")
      .GetMigrations()
      .Where(m => m.Sql.Contains("FUNCTION __SCHEMA__.perform_maintenance", StringComparison.Ordinal))
      .OrderBy(m => m.Name, StringComparer.Ordinal)
      .Last().Sql;

    var start = sql.IndexOf("FUNCTION __SCHEMA__.perform_maintenance", StringComparison.Ordinal);
    var end = sql.IndexOf("$$ LANGUAGE plpgsql", start, StringComparison.Ordinal);
    return sql[start..(end > start ? end : sql.Length)];
  }

  [Test]
  public async Task TheRowExpiryReap_SelectsItsTablesFromTheRegistry_NotTheCatalogAsync() {
    var body = _maintenanceBody();

    // Sliced between task markers, not by a character count: the block's length is prose, and a
    // fixed window silently stops covering the statement the moment a comment grows.
    var task9Start = body.IndexOf("Task 9", StringComparison.Ordinal);
    await Assert.That(task9Start).IsGreaterThan(-1)
      .Because("the reap this rule is about has to be findable, or the rule asserts nothing.");
    var task10Start = body.IndexOf("Task 10", task9Start, StringComparison.Ordinal);
    await Assert.That(task10Start).IsGreaterThan(task9Start)
      .Because("the slice needs a far edge, or it runs into tasks this rule says nothing about.");
    var task9 = body[task9Start..task10Start];

    await Assert.That(task9).DoesNotContain("information_schema.columns")
      .Because("selecting by the presence of an expires_at column selects every perspective table, "
        + "because the column comes from the shared template rather than from a declaration. Each "
        + "one then takes an unindexed delete that matches nothing. Measured: 120 tables scanned for "
        + "3 that declare a lifetime, 14.8 seconds to delete nothing.");

    await Assert.That(task9).Contains("wh_perspective_registry")
      .Because("the registry records which perspectives declare a row lifetime, and the enrolled-row, "
        + "row-cap and reap-target functions all drive from it. This reap must agree with them.");
  }

  [Test]
  public async Task TheSiblingReapers_StillDriveFromTheRegistry_SoTheRuleIsNotVacuousAsync() {
    // If the siblings ever stop using the registry this rule's premise is gone, and the test above
    // would be enforcing a convention nothing else follows.
    var all = new Whizbang.Data.Postgres.PostgresMigrationProvider(
        typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly, "__SCHEMA__")
      .GetMigrations();

    foreach (var fn in new[] { "reap_enrolled_perspective_rows", "reap_perspective_row_caps" }) {
      var sql = all.Where(m => m.Sql.Contains($"FUNCTION __SCHEMA__.{fn}", StringComparison.Ordinal))
        .OrderBy(m => m.Name, StringComparer.Ordinal).Last().Sql;
      var at = sql.IndexOf($"FUNCTION __SCHEMA__.{fn}", StringComparison.Ordinal);
      var slice = sql[at..Math.Min(at + 2500, sql.Length)];
      await Assert.That(slice).Contains("wh_perspective_registry")
        .Because($"{fn} is one of the siblings this rule says the expiry reap should match.");
    }
  }
}
