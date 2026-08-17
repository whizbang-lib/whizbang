using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <c>wh_instance_evictions</c> tombstones only need to outlive a genuine pause-and-resume
/// window — a GC pause, a brief partition, a throttled node — not the fleet's lifetime. Left
/// unbounded, the table only grows. <c>perform_maintenance</c> purges tombstones older than
/// <c>instance_eviction_retention_hours</c> (default 24), the same pattern already used for
/// abandoned active-stream rows.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/032_PerformMaintenance.sql</code-under-test>
public class PerformMaintenanceInstanceEvictionPurgeTests : EFCoreTestBase {

  [Test]
  public async Task PerformMaintenance_TombstoneOlderThanRetention_IsPurgedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var oldInstance = (Guid)TrackedGuid.NewMedo();
    await _insertTombstoneAsync(conn, oldInstance, evictedAtOffset: TimeSpan.FromHours(-25));

    var affected = await _runMaintenanceTaskAsync(conn, "purge_instance_evictions");

    await Assert.That(affected).IsGreaterThanOrEqualTo(1L);
    await Assert.That(await _tombstoneExistsAsync(conn, oldInstance)).IsFalse()
      .Because("a tombstone past the default 24-hour retention must not survive maintenance");
  }

  [Test]
  public async Task PerformMaintenance_TombstoneWithinRetention_SurvivesAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var recentInstance = (Guid)TrackedGuid.NewMedo();
    await _insertTombstoneAsync(conn, recentInstance, evictedAtOffset: TimeSpan.FromHours(-1));
    // Control: an old tombstone alongside the recent one. If the purge task were a no-op, BOTH
    // would survive and this test would pass for the wrong reason — the control rules that out.
    var oldInstance = (Guid)TrackedGuid.NewMedo();
    await _insertTombstoneAsync(conn, oldInstance, evictedAtOffset: TimeSpan.FromHours(-25));

    await _runMaintenanceTaskAsync(conn, "purge_instance_evictions");

    await Assert.That(await _tombstoneExistsAsync(conn, recentInstance)).IsTrue()
      .Because("a fresh tombstone is exactly what a resuming, wrongly-reaped instance needs to be "
               + "correctly refused by — purging it early would silently let a zombie back in");
    await Assert.That(await _tombstoneExistsAsync(conn, oldInstance)).IsFalse()
      .Because("control: the old tombstone must actually be gone, proving the purge ran rather than no-op'd");
  }

  [Test]
  public async Task PerformMaintenance_ReportsPurgeInstanceEvictionsAsATaskAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT task_name FROM perform_maintenance()";
    var names = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }

    await Assert.That(names).Contains("purge_instance_evictions");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _insertTombstoneAsync(
      NpgsqlConnection conn, Guid instanceId, TimeSpan evictedAtOffset) {
    var evictedAt = DateTimeOffset.UtcNow + evictedAtOffset;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_instance_evictions (instance_id, evicted_at, reason)
      VALUES (@id, @evictedAt, 'test')";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("evictedAt", NpgsqlDbType.TimestampTz) { Value = evictedAt });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _tombstoneExistsAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_instance_evictions WHERE instance_id = @id)";
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _runMaintenanceTaskAsync(NpgsqlConnection conn, string taskName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rows_affected FROM perform_maintenance() WHERE task_name = @task";
    cmd.Parameters.AddWithValue("task", taskName);
    var result = await cmd.ExecuteScalarAsync();
    return result is long l ? l : 0L;
  }
}
