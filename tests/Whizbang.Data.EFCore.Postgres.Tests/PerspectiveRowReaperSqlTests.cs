using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the E2-4d d-4 perspective-row reaper — Task 9 of <c>perform_maintenance</c>
/// (migration 082). A <c>TransientStorage.TtlRow</c> perspective row carries an <c>expires_at</c> (stamped on
/// upsert); once past, the row is logically expired (already hidden from lens reads by d-3) and physically
/// deleted here. The reaper dynamically enumerates every <c>wh_per_*</c> table that has an <c>expires_at</c>
/// column, so it needs no per-app table list. Skipped under <c>debug_mode</c>. Verified against a real Postgres.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
[Category("Shard4")]
public class PerspectiveRowReaperSqlTests : EFCoreTestBase {
  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection connection, string sql) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _seedRowAsync(NpgsqlConnection connection, string table, Guid id, string expiresExpr) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $@"
      INSERT INTO {table} (id, data, metadata, scope, created_at, updated_at, expires_at, version)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb, NOW(), NOW(), {expiresExpr}, 1)";
    cmd.Parameters.AddWithValue(nameof(id), id);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _runMaintenanceAsync(NpgsqlConnection connection) {
    await using var m = connection.CreateCommand();
    m.CommandText = "SELECT * FROM perform_maintenance()";
    await using var r = await m.ExecuteReaderAsync();
    while (await r.ReadAsync()) { }
  }

  private static async Task<long> _existsAsync(NpgsqlConnection connection, string table, Guid id) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"SELECT count(*) FROM {table} WHERE id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }




  /// <summary>
  /// Registers the perspective the way the schema pass does, declaring a row lifetime.
  /// </summary>
  /// <remarks>
  /// Migration 165 drives the expiry reap from the registry rather than from "every table that has
  /// an expires_at column", because the column comes from the shared template and sweeping the
  /// catalog meant an unindexed delete against every perspective table on every cycle. These cases
  /// used to rely on that sweep, creating a bare table and no registry row -- a shape the schema
  /// pass never produces, since it writes the table and the registry entry together. Registering it
  /// here keeps the case testing the reap rather than the old discovery mechanism.
  /// </remarks>
  private static async Task _registerTtlPerspectiveAsync(NpgsqlConnection connection, string table) {
    await _execAsync(connection, $@"
      INSERT INTO wh_perspective_registry
        (id, clr_type_name, table_name, schema_json, schema_hash, service_name,
         created_at, updated_at, row_retention_enrolled, row_ttl_seconds)
      VALUES (gen_random_uuid(), 'Test.{table}', '{table}', '{{}}', 'test', 'tests',
              NOW(), NOW(), TRUE, 300)
      ON CONFLICT DO NOTHING");
  }

  [Test]
  public async Task Task9_ReapsExpiredRows_KeepsUnexpiredAndNullAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string table = "wh_per_ttlreap";
    // A perspective table with the generated shape (incl. the E2-4d expires_at column).
    await _execAsync(connection, $@"CREATE TABLE IF NOT EXISTS {table} (
      id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
      created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, expires_at TIMESTAMPTZ, version INTEGER NOT NULL)");
    await _registerTtlPerspectiveAsync(connection, table);

    var expired = Guid.NewGuid();
    var future = Guid.NewGuid();
    var never = Guid.NewGuid();
    await _seedRowAsync(connection, table, expired, "NOW() - INTERVAL '5 minutes'");
    await _seedRowAsync(connection, table, future, "NOW() + INTERVAL '1 hour'");
    await _seedRowAsync(connection, table, never, "NULL");

    await _runMaintenanceAsync(connection);

    await Assert.That(await _existsAsync(connection, table, expired)).IsEqualTo(0L)
      .Because("A TtlRow perspective row past its expires_at is physically reaped.");
    await Assert.That(await _existsAsync(connection, table, future)).IsEqualTo(1L)
      .Because("A not-yet-expired row survives the reap.");
    await Assert.That(await _existsAsync(connection, table, never)).IsEqualTo(1L)
      .Because("A row with no expiry (NULL — a non-TtlRow row) is never reaped.");
  }

  [Test]
  public async Task Task9_RowReap_RetainsLatestSnapshot_TheResurrectionAnchorAsync() {
    // Perspective-row-retention increment 3: reaping an expired row must leave the stream's
    // latest snapshot intact — it is the resurrection anchor. When a reaped Sourced stream
    // wakes up (a new event arrives), the writer path re-folds from snapshot + tail instead
    // of replaying from zero; deleting snapshots here would silently make every resurrection
    // a full replay. Task 9 touches ONLY wh_per_* rows by construction — this locks that.
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string table = "wh_per_ttlreapsnap";
    const string perspective = "SnapshotAnchorPerspective";
    await _execAsync(connection, $@"CREATE TABLE IF NOT EXISTS {table} (
      id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
      created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, expires_at TIMESTAMPTZ, version INTEGER NOT NULL)");
    await _registerTtlPerspectiveAsync(connection, table);

    var streamId = Guid.NewGuid();
    var snapshotEventId = Guid.NewGuid();
    await _seedRowAsync(connection, table, streamId, "NOW() - INTERVAL '5 minutes'");
    await using (var cmd = connection.CreateCommand()) {
      cmd.CommandText =
        "INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_commit_sequence) " +
        "VALUES (@sid, @p, @eid, '{}'::jsonb, 1, 5)";
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("p", perspective);
      cmd.Parameters.AddWithValue("eid", snapshotEventId);
      await cmd.ExecuteNonQueryAsync();
    }

    await _runMaintenanceAsync(connection);

    await Assert.That(await _existsAsync(connection, table, streamId)).IsEqualTo(0L)
      .Because("the expired row itself is reaped");
    await using var check = connection.CreateCommand();
    check.CommandText = "SELECT count(*) FROM wh_perspective_snapshots WHERE stream_id = @sid AND perspective_name = @p";
    check.Parameters.AddWithValue("sid", streamId);
    check.Parameters.AddWithValue("p", perspective);
    await Assert.That((long)(await check.ExecuteScalarAsync())!).IsEqualTo(1L)
      .Because("the stream's snapshot survives the row reap — resurrection re-folds from it plus the tail.");
  }

  [Test]
  public async Task Task9_DebugMode_RetainsExpiredRowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string table = "wh_per_ttlreapdbg";
    await _execAsync(connection, $@"CREATE TABLE IF NOT EXISTS {table} (
      id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
      created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, expires_at TIMESTAMPTZ, version INTEGER NOT NULL)");
    await _registerTtlPerspectiveAsync(connection, table);

    var expired = Guid.NewGuid();
    await _seedRowAsync(connection, table, expired, "NOW() - INTERVAL '5 minutes'");
    await _execAsync(connection, "UPDATE wh_settings SET setting_value = 'true' WHERE setting_key = 'debug_mode'");

    await _runMaintenanceAsync(connection);

    await Assert.That(await _existsAsync(connection, table, expired)).IsEqualTo(1L)
      .Because("Under debug_mode the reaper is skipped, so even an expired perspective row is retained for forensics.");
  }

  /// <summary>
  /// An expired row in a table the registry does not know about is NOT reaped, and that is the
  /// deliberate trade migration 165 makes.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The reap used to find its tables by sweeping the catalog for an <c>expires_at</c> column, which
  /// selected every perspective table because the column comes from the shared template. It now
  /// reads the registry, which records the row-lifetime declaration. The cost went from an
  /// unindexed delete against 120 tables -- 14.8 seconds to delete nothing, 98 percent of the
  /// maintenance cycle on a deployed service -- to a delete against only the perspectives that
  /// declare a lifetime.
  /// </para>
  /// <para>
  /// The trade: reaping now depends on the registry being current. In production it is, because the
  /// schema pass writes the table and the registry row together and the reconciler refreshes it on
  /// every boot, and the three sibling retention reapers have depended on it since 112/113. But a
  /// table created outside that path keeps its expired rows forever, so this is asserted rather
  /// than left to be discovered: if the reap should ever fall back to a catalog sweep, this is the
  /// case that has to change, and its failure names the reason.
  /// </para>
  /// </remarks>
  [Test]
  public async Task Task9_SkipsATableTheRegistryDoesNotKnow_TheDeliberateTradeAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string table = "wh_per_ttlreap_unregistered";
    await _execAsync(connection, $@"CREATE TABLE IF NOT EXISTS {table} (
      id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
      created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, expires_at TIMESTAMPTZ, version INTEGER NOT NULL)");
    // deliberately NOT registered
    var expired = Guid.NewGuid();
    await _seedRowAsync(connection, table, expired, "NOW() - INTERVAL '5 minutes'");

    await _runMaintenanceAsync(connection);

    await Assert.That(await _existsAsync(connection, table, expired)).IsEqualTo(1L)
      .Because("the reap reads the registry, so a perspective that never declared a row lifetime "
        + "is not swept. This is the cost trade 165 makes deliberately: the previous catalog sweep "
        + "touched every perspective table on every cycle to find the few that declare one.");
  }
}
