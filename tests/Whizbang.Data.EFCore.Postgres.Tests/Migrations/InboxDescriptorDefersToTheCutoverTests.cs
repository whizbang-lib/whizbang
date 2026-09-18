using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Data.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The table descriptor must not re-create what the cutover migration removes.
/// </summary>
/// <remarks>
/// <para>
/// This schema has THREE declaration sites for the same table, and this test covers one of them.
/// </para>
/// <para>
/// The third was found only after the cutover reached CI, which is why it is named here rather
/// than left to be rediscovered: <c>CoreInfrastructureSchema.sql</c> in the EFCore generator
/// resources also creates the table and its indexes, and also runs on every startup. Its hazard
/// is the opposite of the descriptor's and louder: <c>CREATE INDEX IF NOT EXISTS</c> tests the
/// index NAME, so once the cutover drops a column and takes its index with it, the name is free
/// and PostgreSQL builds the index against a column that is gone -- 42703, on the second boot,
/// behind the schema-ready gate. Every per-test database is a first boot, so no test saw it.
/// <see cref="PostgresSchemaBuilder.BuildInfrastructureSchema"/> renders the descriptors in
/// <c>Whizbang.Data.Schema.Schemas</c> into a CREATE-and-ensure script, and the numbered SQL
/// migrations evolve the table from there. Schema initialization runs them in that order on EVERY
/// startup: the ensure first, then the migrations under hash-based change detection.
/// </para>
/// <para>
/// That ordering is what makes a drift between the two sites dangerous rather than merely untidy.
/// The ensure emits <c>ALTER TABLE ... ADD COLUMN IF NOT EXISTS</c> for every column the descriptor
/// declares and <c>CREATE INDEX IF NOT EXISTS</c> for every index, so a column a forward migration
/// dropped comes back on the next boot, and the indexes that name it come back with it. The
/// migration does not undo that, because its hash has not changed and it is skipped.
/// </para>
/// <para>
/// The failure is silent, and it is silent in the worst possible way. A re-added column that is
/// nullable, or that carries a default, is added by PostgreSQL without rewriting the table and
/// without an error. So the first boot after the migration is correct, the second boot quietly
/// restores the wide table, and nothing anywhere reports it. What is left is a table carrying
/// duplicate columns that stay NULL forever because the code now reads the other table, and every
/// index on them being maintained on every write for nothing. That is the entire cost the split
/// exists to remove, back in place, looking like a regression that appeared from nowhere.
/// </para>
/// <para>
/// <see cref="ColumnDefinition.BackfillExempt"/> exists for exactly this, and the event store's
/// offloaded payload columns are the precedent: declared so a fresh database gets them from CREATE
/// TABLE, exempt from the ensure so their lifecycle belongs to the migrations. Indexes have no
/// equivalent flag, so an index on a moved column has to be removed from the descriptor outright.
/// </para>
/// <para>
/// <strong>The dropped set is derived from the migration, never listed here.</strong> A hardcoded
/// copy of it is a gate that passes when the thing it guards has moved on, which is the failure this
/// suite keeps finding elsewhere. Deriving it means a later migration that moves one more column is
/// covered without anyone remembering this file exists, and
/// <see cref="TheCutoverActuallyDropsColumnsAsync"/> refuses to let an empty parse make the other
/// two pass vacuously.
/// </para>
/// </remarks>
[Category("Shard3")]
public partial class InboxDescriptorDefersToTheCutoverTests {
  private const string INBOX_TABLE = "wh_inbox";

  [GeneratedRegex(@"ALTER\s+TABLE\s+(?:[A-Za-z0-9_]+\.)?" + INBOX_TABLE + @"(?![_A-Za-z0-9])(.*?);",
    RegexOptions.IgnoreCase | RegexOptions.Singleline)]
  private static partial Regex AlterInbox();

  [GeneratedRegex(@"DROP\s+COLUMN\s+(?:IF\s+EXISTS\s+)?([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
  private static partial Regex DropColumnClause();

  [GeneratedRegex(@"ALTER\s+TABLE\s+(?:[A-Za-z0-9_""]+\.)?" + INBOX_TABLE
    + @"(?![_A-Za-z0-9])\s+ADD\s+COLUMN\s+IF\s+NOT\s+EXISTS\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
  private static partial Regex EnsureAddsInboxColumn();

  [GeneratedRegex(@"CREATE\s+(?:UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\s+([A-Za-z0-9_]+)\s+ON\s+"
    + @"(?:[A-Za-z0-9_""]+\.)?" + INBOX_TABLE + @"(?![_A-Za-z0-9])\s*\(([^)]*)\)([^;]*);", RegexOptions.IgnoreCase)]
  private static partial Regex EnsureCreatesInboxIndex();

  /// <summary>
  /// Every column any migration drops from <c>wh_inbox</c>, read out of the migration text.
  /// </summary>
  /// <remarks>
  /// Line comments are stripped first: the cutover discusses <c>DROP COLUMN</c> in prose above the
  /// statement that performs it, and a parse that counted the prose would name columns nothing drops.
  /// </remarks>
  private static IReadOnlyList<string> _columnsDroppedFromInbox() {
    var dropped = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var migration in new PostgresMigrationProvider().GetMigrations()) {
      var sql = _stripLineComments(migration.Sql);
      // An ALTER TABLE may carry several comma-separated DROP COLUMN clauses, so match each clause
      // inside a statement that targets this table rather than assuming one per statement.
      foreach (Match statement in AlterInbox().Matches(sql)) {
        foreach (Match clause in DropColumnClause().Matches(statement.Groups[1].Value)) {
          dropped.Add(clause.Groups[1].Value.ToLowerInvariant());
        }
      }
    }
    return [.. dropped];
  }

  private static string _stripLineComments(string sql) =>
    string.Join("\n", sql.Split('\n').Select(static line => {
      var at = line.IndexOf("--", StringComparison.Ordinal);
      return at < 0 ? line : line[..at];
    }));

  private static string _ensureScript() =>
    PostgresSchemaBuilder.Instance.BuildInfrastructureSchema(new SchemaConfiguration());

  /// <summary>
  /// The parse found real drops, so the two guards below are testing something.
  /// </summary>
  [Test]
  public async Task TheCutoverActuallyDropsColumnsAsync() {
    var dropped = _columnsDroppedFromInbox();

    await Assert.That(dropped).IsNotEmpty()
      .Because("the two guards below are both of the form \"no dropped column is re-asserted\", "
        + "which an empty dropped set satisfies perfectly while checking nothing. If the migration "
        + "that moves these columns was renamed or its statement reshaped, this fails first and "
        + "says so, rather than letting the suite report green over an unguarded schema");
  }

  /// <summary>
  /// The ensure re-asserts no column a migration has dropped from <c>wh_inbox</c>.
  /// </summary>
  [Test]
  public async Task TheEnsureDoesNotReAssertADroppedColumnAsync() {
    var dropped = _columnsDroppedFromInbox();
    var ensure = _ensureScript();

    // Table-scoped on purpose: wh_outbox declares columns of the same names, and a script-wide
    // search for "status" would report the outbox's ensure as an inbox violation.
    var reAsserted = EnsureAddsInboxColumn().Matches(ensure)
      .Select(m => m.Groups[1].Value.ToLowerInvariant())
      .Where(dropped.Contains)
      .Distinct(StringComparer.Ordinal)
      .OrderBy(static c => c, StringComparer.Ordinal)
      .ToList();

    await Assert.That(reAsserted).IsEmpty()
      .Because("the ensure runs before the migrations on every startup, so each of these columns is "
        + "added back to the table the migration removed it from, and the migration is then skipped "
        + "because its hash has not changed. Mark them BackfillExempt so a fresh database still gets "
        + $"them from CREATE TABLE while their lifecycle belongs to the migrations: {string.Join(", ", reAsserted)}");
  }

  /// <summary>
  /// The descriptor declares no index on a column a migration has dropped.
  /// </summary>
  /// <remarks>
  /// Checked separately from the columns because the consequences differ. A re-added column is dead
  /// weight; a re-created index is maintained on every insert and update of the table, which is the
  /// write amplification the split was measured to remove.
  /// </remarks>
  [Test]
  public async Task TheEnsureDeclaresNoIndexOnADroppedColumnAsync() {
    var dropped = _columnsDroppedFromInbox();
    var ensure = _ensureScript();

    var offenders = new List<string>();
    foreach (Match index in EnsureCreatesInboxIndex().Matches(ensure)) {
      var name = index.Groups[1].Value;
      // The key list and the partial index's predicate both pin the index to the column: an index
      // keyed on a surviving column but filtered on a dropped one is just as broken.
      var referenced = index.Groups[2].Value + " " + index.Groups[3].Value;
      var named = dropped
        .Where(c => Regex.IsMatch(referenced, $"(?<![_A-Za-z0-9]){Regex.Escape(c)}(?![_A-Za-z0-9])"))
        .ToList();
      if (named.Count > 0) {
        offenders.Add($"{name} ({string.Join(", ", named)})");
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("CREATE INDEX IF NOT EXISTS succeeds once the ensure has re-added the columns, so "
        + "these indexes return on the second boot and are maintained on every write for rows whose "
        + "state now lives in another table. There is no BackfillExempt for an index, so an index on "
        + $"a moved column has to leave the descriptor: {string.Join("; ", offenders)}");
  }
}
