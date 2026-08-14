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
    cmd.Parameters.AddWithValue("id", id);
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
    cmd.Parameters.AddWithValue("id", id);
    return (long)(await cmd.ExecuteScalarAsync())!;
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

    var expired = Guid.NewGuid();
    await _seedRowAsync(connection, table, expired, "NOW() - INTERVAL '5 minutes'");
    await _execAsync(connection, "UPDATE wh_settings SET setting_value = 'true' WHERE setting_key = 'debug_mode'");

    await _runMaintenanceAsync(connection);

    await Assert.That(await _existsAsync(connection, table, expired)).IsEqualTo(1L)
      .Because("Under debug_mode the reaper is skipped, so even an expired perspective row is retained for forensics.");
  }
}
