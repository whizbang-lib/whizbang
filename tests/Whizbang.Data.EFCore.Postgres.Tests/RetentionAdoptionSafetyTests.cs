using Npgsql;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the two adoption guards: a newly-enrolled perspective REPORTS what it would remove and
/// removes nothing until acknowledged, and every sweep drains in bounded chunks.
/// </summary>
/// <remarks>
/// Because retention is DERIVED rather than stamped, a declaration is retroactive the moment it
/// ships — adding a window to a perspective holding years of rows expires the whole backlog on the
/// next cycle. That is correct, and it is also not something a deploy should do unannounced. The
/// two risks are distinct: the surprise, and the load of one statement deleting a large population
/// on a shared database.
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
[NotInParallel("RetentionAdoptionSafety")]
public class RetentionAdoptionSafetyTests : EFCoreTestBase {
  private const string TABLE = "wh_per_adoption";
  private const string CLR_TYPE = "TestApp.AdoptionModel";

  private async Task _resetAsync(NpgsqlConnection conn, bool acknowledged, int rowCount, int idleDays) {
    await using (var ddl = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      DELETE FROM wh_perspective_registry WHERE clr_type_name = '{CLR_TYPE}';
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES ('{CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc');
      UPDATE wh_perspective_registry
         SET row_retention_enrolled = TRUE, row_ttl_seconds = {60 * 60 * 24 * 60},
             retention_enforcement_acknowledged = {(acknowledged ? "TRUE" : "FALSE")}
       WHERE clr_type_name = '{CLR_TYPE}';
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
      SELECT gen_random_uuid(), '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb,
             NOW() - make_interval(days => {idleDays}), NOW() - make_interval(days => {idleDays}), 1
      FROM generate_series(1, {rowCount});", conn)) {
      await ddl.ExecuteNonQueryAsync();
    }
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {TABLE}", conn);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  [Test]
  public async Task NewlyEnrolled_ReportsBacklogButRemovesNothingAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, acknowledged: false, rowCount: 10, idleDays: 200);

    await using (var preview = new NpgsqlCommand(
      "SELECT count_perspective_retention_backlog(@t)", conn)) {
      preview.Parameters.AddWithValue("t", CLR_TYPE);
      var backlog = Convert.ToInt64(
        await preview.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
      await Assert.That(backlog).IsEqualTo(10)
        .Because("the operator needs the size of the backlog BEFORE enforcement, and a preview that "
          + "disagrees with what enforcement does is worse than no preview");
    }

    await using (var reap = new NpgsqlCommand("SELECT reap_enrolled_perspective_rows()", conn)) {
      await reap.ExecuteNonQueryAsync();
    }

    await Assert.That(await _countAsync(conn)).IsEqualTo(10)
      .Because("adoption must be a decision, not a side effect of a deploy — an unacknowledged "
        + "perspective reports and removes nothing");
  }

  [Test]
  public async Task Acknowledged_EnforcesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, acknowledged: true, rowCount: 10, idleDays: 200);

    await using (var reap = new NpgsqlCommand("SELECT reap_enrolled_perspective_rows()", conn)) {
      await reap.ExecuteNonQueryAsync();
    }

    await Assert.That(await _countAsync(conn)).IsEqualTo(0)
      .Because("once acknowledged the ladder applies normally");
  }

  [Test]
  public async Task LargeBacklog_DrainsInBoundedChunksAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, acknowledged: true, rowCount: 25, idleDays: 200);

    // One cycle with a small bound must not clear the whole backlog: adoption should cost several
    // cheap cycles rather than one long statement on a shared database.
    await using (var reap = new NpgsqlCommand("SELECT reap_enrolled_perspective_rows(10)", conn)) {
      await reap.ExecuteNonQueryAsync();
    }
    await Assert.That(await _countAsync(conn)).IsEqualTo(15)
      .Because("the sweep is bounded per cycle, so a large backlog drains incrementally");

    await using (var reap2 = new NpgsqlCommand("SELECT reap_enrolled_perspective_rows(10)", conn)) {
      await reap2.ExecuteNonQueryAsync();
    }
    await Assert.That(await _countAsync(conn)).IsEqualTo(5);

    await using (var reap3 = new NpgsqlCommand("SELECT reap_enrolled_perspective_rows(10)", conn)) {
      await reap3.ExecuteNonQueryAsync();
    }
    await Assert.That(await _countAsync(conn)).IsEqualTo(0)
      .Because("it converges — the bound paces the drain, it does not cap what is ultimately removed");
  }
}
