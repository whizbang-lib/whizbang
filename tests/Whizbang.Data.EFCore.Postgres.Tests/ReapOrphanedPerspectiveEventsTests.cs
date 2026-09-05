using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the orphaned-perspective-event reaper (issue #687, Task 12 of
/// <c>perform_maintenance</c>). A <c>wh_perspective_events</c> row whose source event is
/// absent from <c>wh_event_store</c> is unprojectable forever — the drainer's inner join
/// returns nothing, so the row re-claims every cycle with climbing attempts and no error,
/// livelocking the pipeline (root cause of #679). The reaper deletes such rows, but only
/// once they are older than the grace window, so a legitimately in-flight event write that
/// has not committed yet is never reaped out from under itself.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/135_ReapOrphanedPerspectiveEvents.sql</code-under-test>
[Category("Shard2")]
public class ReapOrphanedPerspectiveEventsTests : EFCoreTestBase {

  private static async Task _seedEventAsync(NpgsqlConnection c, Guid eventId, Guid streamId) {
    await using var cmd = c.CreateCommand();
    cmd.CommandText = @"INSERT INTO wh_event_store
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eid, @s, @s, 'Test', 'TestEvent', 1, NOW())";
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("s", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _seedPerspectiveRowAsync(
      NpgsqlConnection c, Guid eventId, Guid streamId, string ageInterval, int attempts = 200) {
    var workId = (Guid)TrackedGuid.NewMedo();
    await using var cmd = c.CreateCommand();
    cmd.CommandText = @"INSERT INTO wh_perspective_events
      (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (@work, @s, 'Test.Projection', @event, 0, 0, @att, NOW() + @age::interval)";
    cmd.Parameters.AddWithValue("work", workId);
    cmd.Parameters.AddWithValue("s", streamId);
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue("att", attempts);
    cmd.Parameters.AddWithValue("age", ageInterval);
    await cmd.ExecuteNonQueryAsync();
    return workId;
  }

  private static async Task<bool> _existsAsync(NpgsqlConnection c, Guid workId) {
    await using var q = c.CreateCommand();
    q.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_perspective_events WHERE event_work_id=@w)";
    q.Parameters.AddWithValue("w", workId);
    return (bool)(await q.ExecuteScalarAsync() ?? false);
  }

  private static async Task<long> _reapAsync(NpgsqlConnection c) {
    await using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT rows_affected FROM perform_maintenance() WHERE task_name = 'reap_orphaned_perspective_events'";
    return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    return conn;
  }

  [Test]
  public async Task Reap_OldOrphan_IsDeleted_HealthyAndYoungAreKeptAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = (Guid)TrackedGuid.NewMedo();

    // Orphan, older than the 1-hour grace window: its event was never/no-longer stored.
    var orphan = await _seedPerspectiveRowAsync(conn, (Guid)TrackedGuid.NewMedo(), stream, "-2 hours");

    // Control 1 — healthy row: its source event exists, so it must never be reaped.
    var goodEvent = (Guid)TrackedGuid.NewMedo();
    var goodStream = (Guid)TrackedGuid.NewMedo();
    await _seedEventAsync(conn, goodEvent, goodStream);
    var healthy = await _seedPerspectiveRowAsync(conn, goodEvent, goodStream, "-2 hours");

    // Control 2 — young orphan inside the grace window: could be an in-flight write, keep it.
    var young = await _seedPerspectiveRowAsync(conn, (Guid)TrackedGuid.NewMedo(), stream, "-1 minute");

    var reaped = await _reapAsync(conn);

    await Assert.That(reaped).IsGreaterThanOrEqualTo(1L);
    await Assert.That(await _existsAsync(conn, orphan)).IsFalse()
      .Because("an aged orphan (source event absent) is unprojectable forever — it must be reaped "
             + "so it stops livelocking the drainer");
    await Assert.That(await _existsAsync(conn, healthy)).IsTrue()
      .Because("a row whose source event exists is real work and must survive");
    await Assert.That(await _existsAsync(conn, young)).IsTrue()
      .Because("inside the grace window the event write may simply not have committed yet — "
             + "reaping it would race a legitimate in-flight write");
  }

  [Test]
  public async Task Reap_ReportsAsAMaintenanceTaskAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM perform_maintenance() WHERE task_name = 'reap_orphaned_perspective_events'";
    await Assert.That((long)(await cmd.ExecuteScalarAsync() ?? 0L)).IsEqualTo(1L)
      .Because("the reaper must surface as its own perform_maintenance task so its volume rolls "
             + "up under the Maintenance housekeeping activity like every other sweep");
  }
}
