using Npgsql;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration tests for the bitemporal perspective-row columns: <c>sys_created_at</c> and
/// <c>sys_updated_at</c> are added to tables that ALREADY EXIST, and are backfilled by copying
/// the current <c>created_at</c> / <c>updated_at</c> values.
/// </summary>
/// <remarks>
/// <para>
/// The generated schema script opens with <c>CREATE TABLE IF NOT EXISTS</c>, which SKIPS a table
/// that already exists — so new columns reach existing deployments only through the trailing
/// <c>ALTER TABLE ... ADD COLUMN IF NOT EXISTS</c>. A schema test that creates a fresh table
/// would pass whether or not that ALTER is present, which is precisely the bug these tests exist
/// to catch: the column would appear for new consumers and be silently missing for every
/// existing one.
/// </para>
/// <para>
/// The backfill has the same asymmetry. Copying the sibling values is what preserves the meaning
/// those columns always had (wall-clock write times); leaving them NULL, or defaulting them to
/// <c>NOW()</c>, would silently claim every historical row was written at upgrade time.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
[NotInParallel("BitemporalColumnMigration")]
public class BitemporalColumnMigrationTests : EFCoreTestBase {
  private const string LEGACY_TABLE = "wh_per_bitemporal_legacy";

  /// <summary>Creates a table in the PRE-migration shape, with no sys_ columns.</summary>
  private async Task _createLegacyTableAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {LEGACY_TABLE};
      CREATE TABLE {LEGACY_TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        expires_at TIMESTAMPTZ,
        version INTEGER NOT NULL);", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// The additive half of the generated schema script — the statements an existing deployment
  /// receives. Kept verbatim in shape so the test exercises the same SQL the generator emits.
  /// </summary>
  private static async Task _applyAdditiveMigrationAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand($@"
      ALTER TABLE {LEGACY_TABLE} ADD COLUMN IF NOT EXISTS sys_created_at TIMESTAMPTZ;
      ALTER TABLE {LEGACY_TABLE} ADD COLUMN IF NOT EXISTS sys_updated_at TIMESTAMPTZ;
      UPDATE {LEGACY_TABLE}
         SET sys_created_at = created_at, sys_updated_at = updated_at
       WHERE sys_created_at IS NULL OR sys_updated_at IS NULL;
      CREATE INDEX IF NOT EXISTS idx_bitemporal_legacy_updated_at ON {LEGACY_TABLE} (updated_at);", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _seedRowAsync(NpgsqlConnection conn, Guid id, DateTime createdAt, DateTime updatedAt) {
    await using var cmd = new NpgsqlCommand($@"
      INSERT INTO {LEGACY_TABLE} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb, @c, @u, 1);", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("c", createdAt);
    cmd.Parameters.AddWithValue("u", updatedAt);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<(DateTime? SysCreated, DateTime? SysUpdated)> _readSysColumnsAsync(
      NpgsqlConnection conn, Guid id) {
    await using var cmd = new NpgsqlCommand(
      $"SELECT sys_created_at, sys_updated_at FROM {LEGACY_TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return (null, null);
    }
    return (
      reader.IsDBNull(0) ? null : reader.GetDateTime(0),
      reader.IsDBNull(1) ? null : reader.GetDateTime(1));
  }

  [Test]
  public async Task Migration_OnPreExistingTable_AddsSysColumnsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createLegacyTableAsync(conn);

    await _applyAdditiveMigrationAsync(conn);

    await using var cmd = new NpgsqlCommand(@"
      SELECT column_name, data_type, is_nullable FROM information_schema.columns
      WHERE table_name = @t AND column_name LIKE 'sys\_%' ORDER BY column_name", conn);
    cmd.Parameters.AddWithValue("t", LEGACY_TABLE);
    var found = new Dictionary<string, (string Type, string Nullable)>(StringComparer.Ordinal);
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        found[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
      }
    }

    await Assert.That(found).ContainsKey("sys_created_at")
      .Because("CREATE TABLE IF NOT EXISTS skips an existing table, so only the trailing ALTER reaches "
        + "an existing deployment — without it the column is silently missing for every current consumer");
    await Assert.That(found).ContainsKey("sys_updated_at");
    await Assert.That(found["sys_created_at"].Type).IsEqualTo("timestamp with time zone");
    await Assert.That(found["sys_updated_at"].Type).IsEqualTo("timestamp with time zone");
  }

  [Test]
  public async Task Migration_BackfillsSysColumns_ByCopyingTheSiblingValuesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createLegacyTableAsync(conn);

    var id = Guid.CreateVersion7();
    // Distinct, deliberately historical values — a NOW() default or a missed backfill would
    // claim this row was written at upgrade time instead.
    var createdAt = new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var updatedAt = new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc);
    await _seedRowAsync(conn, id, createdAt, updatedAt);

    await _applyAdditiveMigrationAsync(conn);

    var (sysCreated, sysUpdated) = await _readSysColumnsAsync(conn, id);
    await Assert.That(sysCreated).IsEqualTo(createdAt)
      .Because("sys_created_at must carry the row's existing created_at — that wall-clock write time is "
        + "exactly what the operational axis always meant");
    await Assert.That(sysUpdated).IsEqualTo(updatedAt)
      .Because("sys_updated_at must carry the row's existing updated_at, not NOW()");
  }

  [Test]
  public async Task Migration_IsIdempotent_SecondRunDoesNotClobberAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createLegacyTableAsync(conn);

    var id = Guid.CreateVersion7();
    var createdAt = new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var updatedAt = new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc);
    await _seedRowAsync(conn, id, createdAt, updatedAt);

    await _applyAdditiveMigrationAsync(conn);

    // Simulate live traffic diverging the two axes after the migration: a later write advances the
    // operational stamp while business time stays put.
    var laterWrite = new DateTime(2024, 9, 9, 9, 9, 9, DateTimeKind.Utc);
    await using (var advance = new NpgsqlCommand(
        $"UPDATE {LEGACY_TABLE} SET sys_updated_at = @w WHERE id = @id", conn)) {
      advance.Parameters.AddWithValue("w", laterWrite);
      advance.Parameters.AddWithValue("id", id);
      await advance.ExecuteNonQueryAsync();
    }

    // Schema init runs on every startup, so the migration must be safe to re-apply.
    await _applyAdditiveMigrationAsync(conn);

    var (_, sysUpdated) = await _readSysColumnsAsync(conn, id);
    await Assert.That(sysUpdated).IsEqualTo(laterWrite)
      .Because("the backfill is guarded on IS NULL, so re-running schema init must not overwrite a "
        + "row whose operational stamp has since moved on");
  }

  [Test]
  public async Task Migration_AddsUpdatedAtIndex_ForTheSlidingReapPredicateAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createLegacyTableAsync(conn);

    await _applyAdditiveMigrationAsync(conn);

    await using var cmd = new NpgsqlCommand(
      "SELECT indexdef FROM pg_indexes WHERE tablename = @t", conn);
    cmd.Parameters.AddWithValue("t", LEGACY_TABLE);
    var indexDefs = new List<string>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        indexDefs.Add(reader.GetString(0));
      }
    }

    await Assert.That(indexDefs.Any(d => d.Contains("(updated_at)", StringComparison.Ordinal))).IsTrue()
      .Because("the sliding reap predicate is updated_at < NOW() - interval; without an index on "
        + "updated_at it degrades to a sequential scan of every enrolled table on every cycle");
  }
}
