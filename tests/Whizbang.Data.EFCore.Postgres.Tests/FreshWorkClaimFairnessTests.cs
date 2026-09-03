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
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts,
         received_at, stream_id, partition_number, instance_id, lease_expiry)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', 1, @attempts,
             NOW() + @age::interval + (s * INTERVAL '1 millisecond'), gen_random_uuid(), 0,
             @inst, NOW() + INTERVAL '5 minutes'
      FROM generate_series(1, @n) AS s";
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
      q.CommandText = "SELECT attempts FROM wh_inbox WHERE stream_id = @id ORDER BY received_at LIMIT 1";
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
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts,
           received_at, stream_id, partition_number, instance_id, lease_expiry)
        VALUES
          (gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', 1, 2,
           NOW() - INTERVAL '1 hour', @sid, 0, @inst, NOW() + INTERVAL '5 minutes'),
          (gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', 1, 0,
           NOW(), @sid, 0, @inst, NOW() + INTERVAL '5 minutes')";
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
}
