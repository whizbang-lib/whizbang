using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Migrations whose correctness depends on being one transaction must not carry a commit boundary.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SchemaCommandBoundary"/> applies a marker-free script as one command on one
/// connection, which PostgreSQL runs as a single implicit transaction. A marker splits the script
/// into separately committed pieces, which is exactly right for a data rewrite an index depends on,
/// and exactly wrong for a migration that moves data between tables and then drops the columns it
/// came from.
/// </para>
/// <para>
/// The hazard is not theoretical and it is not loud. Split at the wrong place, a migration can
/// commit a column drop while the functions that read those columns have not been replaced yet, or
/// commit a backfill that a later piece then fails to build on, leaving a half-migrated schema that
/// the next attempt starts from rather than retries cleanly. Neither shows up as a test failure
/// anywhere else, because every individual statement is valid.
/// </para>
/// <para>
/// The rule checked here is general, and general is what makes it useful: a script that takes an
/// explicit table lock or drops a column is relying on everything around it committing together,
/// whatever else it does. It catches the hazard by shape rather than by name, so a migration written
/// next year is covered without anyone remembering to add it.
/// </para>
/// <para>
/// A per-migration named guard is deliberately NOT added here. A list of names that a migration can
/// simply be absent from is a gate that passes when the thing it guards has gone, which is the
/// failure mode this suite keeps finding in other people's tests and should not introduce into its
/// own. The shape rule covers the same hazard without that hole.
/// </para>
/// </remarks>
public class MigrationTransactionBoundaryGuardTests {
  /// <summary>
  /// Statements whose presence means the script is relying on being one transaction.
  /// </summary>
  private static readonly string[] ATOMIC_ONLY_STATEMENTS = ["LOCK TABLE", "DROP COLUMN"];

  [Test]
  public async Task AScriptThatLocksOrDropsAColumnCarriesNoCommitBoundaryAsync() {
    var offenders = new List<string>();
    foreach (var migration in new PostgresMigrationProvider().GetMigrations()) {
      var relies = ATOMIC_ONLY_STATEMENTS.Any(
        s => migration.Sql.Contains(s, StringComparison.OrdinalIgnoreCase));
      if (relies && migration.Sql.Contains(SchemaCommandBoundary.MARKER, StringComparison.Ordinal)) {
        offenders.Add(migration.Name);
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("a script that takes an explicit table lock or drops a column depends on every "
        + "statement around it committing together. A commit boundary splits it into pieces that "
        + "commit separately, so a lock is released before the work it was protecting finishes and "
        + "a dropped column can be committed while the functions still reading it have not been "
        + "replaced. Move the dependent statements into one script instead of separating them");
  }

}
