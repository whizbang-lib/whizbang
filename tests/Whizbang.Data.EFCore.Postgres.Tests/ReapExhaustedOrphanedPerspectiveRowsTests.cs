using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the reactive orphan-disposal function (issue #679, migration 136). When the
/// perspective drainer fetches a leased stream and the inner join returns nothing, the stream's
/// rows may be orphaned (source event absent from <c>wh_event_store</c>) — unprojectable and
/// re-claiming forever. This function disposes such rows ON CONTACT, keyed on attempts rather
/// than age: a row attempted <c>p_max_attempts</c>+ times with no surviving event is
/// unambiguously an orphan. It is scoped to the calling instance's leased rows in the given
/// streams, so it never touches another instance's work or rows still under the attempt bar.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/136_ReapExhaustedOrphanedPerspectiveRows.sql</code-under-test>
[Category("Shard2")]
public class ReapExhaustedOrphanedPerspectiveRowsTests : EFCoreTestBase {

  private static async Task _seedEventAsync(NpgsqlConnection c, Guid eventId, Guid streamId) {
    await using var cmd = c.CreateCommand();
    cmd.CommandText = @"INSERT INTO wh_event_store
      (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES (@eid, @s, @s, 'Test', 'TestEvent', 1, NOW())";
    cmd.Parameters.AddWithValue("eid", eventId);
    cmd.Parameters.AddWithValue("s", streamId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _seedRowAsync(
      NpgsqlConnection c, Guid eventId, Guid streamId, Guid instanceId, int attempts) {
    var workId = (Guid)TrackedGuid.NewMedo();
    await using var cmd = c.CreateCommand();
    cmd.CommandText = @"INSERT INTO wh_perspective_events
      (event_work_id, stream_id, perspective_name, event_id, instance_id, partition_number, status, attempts, created_at)
      VALUES (@work, @s, 'Test.Projection', @event, @inst, 0, 0, @att, NOW())";
    cmd.Parameters.AddWithValue("work", workId);
    cmd.Parameters.AddWithValue("s", streamId);
    cmd.Parameters.AddWithValue("event", eventId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("att", attempts);
    await cmd.ExecuteNonQueryAsync();
    return workId;
  }

  private static async Task<bool> _existsAsync(NpgsqlConnection c, Guid workId) {
    await using var q = c.CreateCommand();
    q.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_perspective_events WHERE event_work_id=@w)";
    q.Parameters.AddWithValue("w", workId);
    return (bool)(await q.ExecuteScalarAsync() ?? false);
  }

  private static async Task<int> _reapAsync(NpgsqlConnection c, Guid instanceId, Guid[] streams, int max) {
    await using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT reap_exhausted_orphaned_perspective_rows(@inst, @streams, @max)";
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("streams", streams);
    cmd.Parameters.AddWithValue("max", max);
    return (int)(await cmd.ExecuteScalarAsync() ?? 0);
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    return conn;
  }

  [Test]
  public async Task Reap_ExhaustedOrphan_IsDeleted_OthersKeptAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var inst = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();

    // Exhausted orphan: attempts past the bar, source event absent — the wedge case.
    var orphan = await _seedRowAsync(conn, (Guid)TrackedGuid.NewMedo(), stream, inst, attempts: 10);
    // Control 1 — orphan still under the attempt bar: could be an in-flight write, keep it.
    var young = await _seedRowAsync(conn, (Guid)TrackedGuid.NewMedo(), stream, inst, attempts: 2);
    // Control 2 — exhausted but its event EXISTS: real work that keeps failing for another
    // reason; the existing joined-row dead-letter cap owns it, not this orphan reaper.
    var goodEvent = (Guid)TrackedGuid.NewMedo();
    await _seedEventAsync(conn, goodEvent, stream);
    var healthy = await _seedRowAsync(conn, goodEvent, stream, inst, attempts: 10);

    var reaped = await _reapAsync(conn, inst, [stream], 10);

    await Assert.That(reaped).IsEqualTo(1);
    await Assert.That(await _existsAsync(conn, orphan)).IsFalse()
      .Because("an exhausted row whose event is gone is unprojectable — dispose it on contact "
             + "so it stops re-claiming and livelocking the drainer");
    await Assert.That(await _existsAsync(conn, young)).IsTrue()
      .Because("under the attempt bar the event write may simply not have committed yet");
    await Assert.That(await _existsAsync(conn, healthy)).IsTrue()
      .Because("a row whose source event exists is real work — the joined-row cap owns its fate, "
             + "not the orphan reaper");
  }

  [Test]
  public async Task Reap_ScopesToInstanceAndStreamsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var mine = (Guid)TrackedGuid.NewMedo();
    var other = (Guid)TrackedGuid.NewMedo();
    var stream = (Guid)TrackedGuid.NewMedo();
    var otherStream = (Guid)TrackedGuid.NewMedo();

    var otherInstance = await _seedRowAsync(conn, (Guid)TrackedGuid.NewMedo(), stream, other, attempts: 20);
    var otherStreamRow = await _seedRowAsync(conn, (Guid)TrackedGuid.NewMedo(), otherStream, mine, attempts: 20);
    var target = await _seedRowAsync(conn, (Guid)TrackedGuid.NewMedo(), stream, mine, attempts: 20);

    var reaped = await _reapAsync(conn, mine, [stream], 10);

    await Assert.That(reaped).IsEqualTo(1);
    await Assert.That(await _existsAsync(conn, target)).IsFalse();
    await Assert.That(await _existsAsync(conn, otherInstance)).IsTrue()
      .Because("another instance's leased rows are its own to dispose — never reap across instances");
    await Assert.That(await _existsAsync(conn, otherStreamRow)).IsTrue()
      .Because("only the streams the drainer actually fetched-empty are in scope");
  }
}
