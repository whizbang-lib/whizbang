using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks the fresh-work share of the claim batch. The production failure this encodes: a
/// service accumulated a 28,000-row inbox backlog of retried control-plane messages, and
/// <c>claim_work</c>'s strict oldest-first ordering meant a brand-new single-row stream — a user
/// clicking "new chat" — was guaranteed the last slot in line, hours out. Real-time work must get
/// a reserved share of every batch while the backlog still drains behind it.</para>
/// <para>A stream is classified by its HEAD row (earliest unprocessed): stream-FIFO means rows
/// behind a retried head cannot dispatch anyway, so a fresh row on a poisoned stream buys nothing.
/// The share is work-conserving — when either class is empty the other fills the whole batch.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/126_FreshWorkClaimFairness.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class FreshWorkClaimFairnessTests : EFCoreTestBase {

  private static async Task _seedOwnedInboxAsync(
      NpgsqlConnection conn, Guid instanceId, int streams, int attempts, string ageOffset) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata,
           received_at, stream_id)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}',
               NOW() + @age::interval + (s * INTERVAL '1 millisecond'), gen_random_uuid()
        FROM generate_series(1, @n) AS s
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event,
         status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event,
             1, @attempts, 0, @inst, NOW() + INTERVAL '5 minutes'
      FROM m";
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.AddWithValue("n", streams);
    ins.Parameters.AddWithValue("attempts", attempts);
    ins.Parameters.AddWithValue("age", ageOffset);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task<(int Fresh, int Retry)> _classifyClaimedAsync(
      NpgsqlConnection conn, WorkBatch batch) {
    var fresh = 0;
    var retry = 0;
    foreach (var streamId in batch.InboxStreamIds) {
      await using var q = conn.CreateCommand();
      // Head row's attempts — the class the stream claims under.
      q.CommandText = "SELECT attempts FROM wh_inbox_state WHERE stream_id = @id ORDER BY received_at LIMIT 1";
      q.Parameters.AddWithValue("id", streamId);
      var attempts = (int)(await q.ExecuteScalarAsync() ?? 0);
      if (attempts == 0) { fresh++; } else { retry++; }
    }
    return (fresh, retry);
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task FreshStreams_GetTheirShare_DespiteAnOlderRetryBacklogAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    // 20 retried streams, hours older — the backlog. 10 fresh streams, arriving now.
    await _seedOwnedInboxAsync(conn, instanceId, streams: 20, attempts: 2, ageOffset: "-2 hours");
    await _seedOwnedInboxAsync(conn, instanceId, streams: 10, attempts: 0, ageOffset: "0");

    var batch = await _coordinator(ctx).ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc", "host", 1, MaxStreams: 10));

    var (fresh, _) = await _classifyClaimedAsync(conn, batch);
    await Assert.That(fresh).IsGreaterThanOrEqualTo(4)
      .Because("with the default 0.5 share, roughly half of every batch belongs to fresh-head "
             + "streams; oldest-first alone hands all ten slots to the backlog and a new chat "
             + "waits behind 28,000 rows");
  }

  [Test]
  public async Task OnlyBacklog_FillsTheWholeBatch_WorkConservingAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _seedOwnedInboxAsync(conn, instanceId, streams: 12, attempts: 3, ageOffset: "-1 hour");

    var batch = await _coordinator(ctx).ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc", "host", 1, MaxStreams: 10));

    await Assert.That(batch.InboxStreamIds.Count).IsEqualTo(10)
      .Because("a reserved share must never hold slots empty — no fresh work means the "
             + "backlog takes the entire batch");
  }

  [Test]
  public async Task OnlyFreshWork_FillsTheWholeBatch_WorkConservingAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _seedOwnedInboxAsync(conn, instanceId, streams: 12, attempts: 0, ageOffset: "0");

    var batch = await _coordinator(ctx).ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc", "host", 1, MaxStreams: 10));

    await Assert.That(batch.InboxStreamIds.Count).IsEqualTo(10);
  }

  [Test]
  public async Task ShareOfOne_PutsEveryFreshStreamAheadOfTheBacklogAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _seedOwnedInboxAsync(conn, instanceId, streams: 20, attempts: 2, ageOffset: "-2 hours");
    await _seedOwnedInboxAsync(conn, instanceId, streams: 6, attempts: 0, ageOffset: "0");

    var batch = await _coordinator(ctx).ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc", "host", 1, MaxStreams: 10, FreshWorkShare: 1.0));

    var (fresh, retry) = await _classifyClaimedAsync(conn, batch);
    await Assert.That(fresh).IsEqualTo(6)
      .Because("share 1.0 is the real-time-first posture: every fresh stream claims before "
             + "any retry does");
    await Assert.That(retry).IsEqualTo(4)
      .Because("and the remainder still goes to the backlog — priority, not starvation "
             + "in the other direction");
  }

  [Test]
  public async Task StreamFifo_HoldsInsideAStream_RegardlessOfShareAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    // One stream whose HEAD is a retry and whose second row is fresh: the stream classifies
    // as retry, and the fresh row must not be claimable ahead of its own head.
    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        WITH src (message_id, received_at, attempts) AS (
          VALUES
            (gen_random_uuid(), NOW() - INTERVAL '1 hour', 2),
            (gen_random_uuid(), NOW(), 0)
        ),
        m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata,
             received_at, stream_id)
          SELECT message_id, 'TestHandler', 'TestEvent', '{}', '{}', received_at, @sid
          FROM src
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event,
           status, attempts, partition_number, instance_id, lease_expiry)
        SELECT m.message_id, m.stream_id, m.received_at, m.priority, m.is_event,
               1, src.attempts, 0, @inst, NOW() + INTERVAL '5 minutes'
        FROM m JOIN src USING (message_id)";
      ins.Parameters.AddWithValue("sid", streamId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    var batch = await _coordinator(ctx).ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc", "host", 1, MaxStreams: 10, FreshWorkShare: 1.0));

    await Assert.That(batch.InboxStreamIds.Contains(streamId)).IsTrue()
      .Because("the stream is claimable as a unit — classification chooses ordering "
             + "between streams, never visibility of rows behind a retried head");
  }

  private static async Task<Guid> _seedBulkStreamAsync(
      NpgsqlConnection conn, Guid? instanceId, int rows, string ageOffset) {
    var streamId = Guid.NewGuid();
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata,
           received_at, stream_id)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}',
               NOW() + @age::interval + (s * INTERVAL '1 millisecond'), @stream
        FROM generate_series(1, @n) AS s
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event,
         status, attempts, partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event,
             1, 0, 0, @inst, CASE WHEN @inst IS NULL THEN NULL ELSE NOW() + INTERVAL '5 minutes' END
      FROM m";
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.Add(new NpgsqlParameter("inst", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)instanceId ?? DBNull.Value });
    ins.Parameters.AddWithValue("n", rows);
    ins.Parameters.AddWithValue("age", ageOffset);
    await ins.ExecuteNonQueryAsync();
    return streamId;
  }

  [Test]
  public async Task InteractiveStream_IsNotStarvedByABulkFloodOfFreshRowsAsync() {
    // #568: fresh-vs-retry fairness cannot help when BOTH sides are fresh — a bulk flood's
    // thousands of attempts=0 rows and an interactive request's one row share a class, and
    // strict arrival order put the interactive row 300 deep. Breadth-first ranking competes
    // stream heads against stream heads.
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _seedBulkStreamAsync(conn, instanceId, rows: 300, ageOffset: "-10 minutes");
    var interactive = await _seedBulkStreamAsync(conn, instanceId, rows: 1, ageOffset: "0");

    var batch = await _coordinator(ctx).ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, "svc", "host", 1, MaxStreams: 50));

    await Assert.That(batch.InboxStreamIds).Contains(interactive)
      .Because("the interactive stream's FIRST row competes with the bulk stream's FIRST "
             + "row, not its three-hundredth — one flood must never own the whole batch");
  }

  [Test]
  public async Task OrphanAcquisition_InteractiveStream_EntersTheWindowDespiteABulkFloodAsync() {
    // #568 acquisition half: the orphan claim's window was oldest-first over ROWS, so a
    // bulk stream's flood filled p_max_rows end to end and the interactive row never even
    // became a candidate — starved upstream of every downstream fairness mechanism.
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await using (var hb = conn.CreateCommand()) {
      hb.CommandText = "SELECT record_heartbeat(@id, 'svc', 'host', 1, '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }
    await _seedBulkStreamAsync(conn, instanceId: null, rows: 300, ageOffset: "-10 minutes");
    var interactive = await _seedBulkStreamAsync(conn, instanceId: null, rows: 1, ageOffset: "0");

    await using var claim = conn.CreateCommand();
    claim.CommandText = @"
      SELECT count(*) FROM claim_orphaned_inbox(
        @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '30 seconds', 50)
      WHERE stream_id = @interactive";
    claim.Parameters.AddWithValue("inst", instanceId);
    claim.Parameters.AddWithValue("interactive", interactive);
    var claimedInteractive = (long)(await claim.ExecuteScalarAsync() ?? 0L);

    await Assert.That(claimedInteractive).IsEqualTo(1L)
      .Because("acquisition is where starvation begins: a row that never enters the claim "
             + "window is invisible to every fairness mechanism behind it");
  }

}
