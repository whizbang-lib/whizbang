using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 150 (priority step 3): the claim schedules by bucket. A stream is claimed by the bucket its most
/// urgent pending row falls in (the fold is over every pending row of the stream, not its head), so an
/// interactive row queued behind bulk rows on its own stream pulls the whole stream forward while the stream's
/// rows are still taken in order. Background streams keep a floor of every batch so they never starve, a
/// background stream that has waited past its wait target is promoted into the standard lane, and commands
/// keep their place ahead of events inside a bucket. The perspective claim and the re-emission in claim_work
/// order streams the same way.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#the-claim</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/150_BucketAwareClaim.sql</code-under-test>
[Category("Shard1")]
public class BucketAwareClaimSqlTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES (@inst, 'test', 'test-host', 1, NOW(), NOW())
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Seeds pending, unassigned inbox rows on one stream, one second apart, each with its own priority; returns the stream and the rows in order.</summary>
  private static async Task<(Guid Stream, List<Guid> Rows)> _seedStreamAsync(NpgsqlConnection conn, TimeSpan age, bool isEvent, params int[] priorities) {
    var streamId = Guid.CreateVersion7();
    var rows = new List<Guid>();
    for (var i = 0; i < priorities.Length; i++) {
      var id = Guid.CreateVersion7();
      rows.Add(id);
      await using var ins = conn.CreateCommand();
      ins.CommandText = """
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           stream_id, partition_number, is_event, instance_id, lease_expiry, error, failure_reason, priority)
        VALUES (@id, 'TestHandler', 'TestEvent', '{"p": {}}', '{}', 1, 0,
                NOW() - @age + (@seq * INTERVAL '1 second'),
                @sid, 0, @isEvent, NULL, NULL, NULL, 99, @priority)
        """;
      ins.Parameters.AddWithValue("id", id);
      ins.Parameters.AddWithValue("age", age);
      ins.Parameters.AddWithValue("seq", i);
      ins.Parameters.AddWithValue("sid", streamId);
      ins.Parameters.AddWithValue("isEvent", isEvent);
      ins.Parameters.AddWithValue("priority", priorities[i]);
      await ins.ExecuteNonQueryAsync();
    }
    return (streamId, rows);
  }

  /// <summary>Claims through claim_orphaned_inbox and returns the claimed message ids in the order the function returned them.</summary>
  private static async Task<List<Guid>> _claimAsync(NpgsqlConnection conn, Guid instance, int limit) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT message_id FROM claim_orphaned_inbox(
        @inst, 0, 1, NOW() + INTERVAL '5 minutes', NOW(), 10000, NOW() - INTERVAL '10 minutes', @lim, FALSE)";
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("lim", limit);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<int> _priorityOfAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT priority FROM wh_inbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task ClaimOrphanedInbox_AnInteractiveStream_IsClaimedAheadOfOlderStandardStreamsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    for (var i = 0; i < 3; i++) {
      _ = await _seedStreamAsync(conn, TimeSpan.FromMinutes(5), isEvent: true, 150);
    }
    var (_, urgent) = await _seedStreamAsync(conn, TimeSpan.FromSeconds(1), isEvent: true, 50);

    var claimed = await _claimAsync(conn, instance, limit: 2);

    await Assert.That(claimed.Count).IsEqualTo(2);
    await Assert.That(claimed[0]).IsEqualTo(urgent[0])
      .Because("an interactive row is what a person is waiting on; arriving last must not put it behind three standard streams");
  }

  [Test]
  public async Task ClaimOrphanedInbox_AStreamWithAnInteractiveRowBehindStandardRows_IsPulledForward_InOrderAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    for (var i = 0; i < 3; i++) {
      _ = await _seedStreamAsync(conn, TimeSpan.FromMinutes(5), isEvent: true, 150);
    }
    // The stream's head is standard; the interactive row is behind it. The stream folds to interactive and
    // is taken first, and per-stream order means its head is what gets claimed first, not the urgent row.
    var (_, folded) = await _seedStreamAsync(conn, TimeSpan.FromMinutes(1), isEvent: true, 150, 50);

    var claimed = await _claimAsync(conn, instance, limit: 2);

    await Assert.That(claimed[0]).IsEqualTo(folded[0])
      .Because("the fold is over every pending row of the stream: an interactive row deep in a stream pulls the stream forward, and its predecessors are prerequisites");
    await Assert.That(claimed[1]).IsEqualTo(folded[1])
      .Because("the stream's rows are still taken in order; priority reorders streams, never rows within a stream");
  }

  [Test]
  public async Task ClaimOrphanedInbox_BackgroundStreams_KeepAFloorOfTheBatchAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    for (var i = 0; i < 20; i++) {
      _ = await _seedStreamAsync(conn, TimeSpan.FromMinutes(5), isEvent: true, 150);
    }
    // Background streams inside the wait target (a stream past it competes as standard, which is the promotion
    // rule the next test proves); the standard streams are older, so FIFO alone would take all of them first.
    var background = new List<Guid>();
    for (var i = 0; i < 5; i++) {
      var (_, rows) = await _seedStreamAsync(conn, TimeSpan.FromSeconds(30), isEvent: true, 250);
      background.AddRange(rows);
    }

    var claimed = await _claimAsync(conn, instance, limit: 10);

    await Assert.That(claimed.Count).IsEqualTo(10);
    await Assert.That(claimed.Count(id => background.Contains(id))).IsGreaterThanOrEqualTo(1)
      .Because("a bucket with pending streams always receives at least its floor of the batch, so background never starves behind a steady standard flow");
    await Assert.That(claimed.Count(id => background.Contains(id))).IsLessThan(5)
      .Because("the floor is a floor: standard work takes the rest of the batch ahead of background");
  }

  [Test]
  public async Task ClaimOrphanedInbox_ABackgroundStreamPastItsWaitTarget_IsPromotedIntoTheStandardLaneAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var (_, aged) = await _seedStreamAsync(conn, TimeSpan.FromMinutes(20), isEvent: true, 250);
    for (var i = 0; i < 15; i++) {
      _ = await _seedStreamAsync(conn, TimeSpan.FromSeconds(5), isEvent: true, 150);
    }

    var claimed = await _claimAsync(conn, instance, limit: 5);

    await Assert.That(claimed[0]).IsEqualTo(aged[0])
      .Because("a background stream that has waited past the background wait target competes as standard, and as the oldest standard row it goes first");
  }

  [Test]
  public async Task ClaimOrphanedInbox_CommandsStayAheadOfEvents_InsideABucketAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var (_, olderEvent) = await _seedStreamAsync(conn, TimeSpan.FromMinutes(5), isEvent: true, 150);
    var (_, newerCommand) = await _seedStreamAsync(conn, TimeSpan.FromSeconds(1), isEvent: false, 150);
    var (_, urgentEvent) = await _seedStreamAsync(conn, TimeSpan.FromSeconds(2), isEvent: true, 50);

    var claimed = await _claimAsync(conn, instance, limit: 3);

    await Assert.That(claimed[0]).IsEqualTo(urgentEvent[0])
      .Because("the bucket decides first: an interactive event precedes a standard command");
    await Assert.That(claimed[1]).IsEqualTo(newerCommand[0])
      .Because("inside a bucket a command keeps its place ahead of events (the command lane)");
    await Assert.That(claimed[2]).IsEqualTo(olderEvent[0]);
  }

  [Test]
  public async Task ClaimWork_ReemitsHeldInboxStreams_MostUrgentBucketFirstAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var (_, standard) = await _seedStreamAsync(conn, TimeSpan.FromMinutes(5), isEvent: true, 150);
    var (_, urgent) = await _seedStreamAsync(conn, TimeSpan.FromSeconds(1), isEvent: true, 50);
    _ = await _claimAsync(conn, instance, limit: 10);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT work_id FROM claim_work(@inst, 'test', 'test-host', 1, 10, 10000, 300, 0.5, 10, FALSE, NULL) WHERE source = 'inbox'";
    cmd.Parameters.AddWithValue("inst", instance);
    var order = new List<Guid>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        order.Add(reader.GetGuid(0));
      }
    }

    await Assert.That(order.Count).IsEqualTo(2);
    await Assert.That(order[0]).IsEqualTo(urgent[0])
      .Because("the rows an instance already holds are re-offered in bucket order too, or the drain would dispatch the standard stream first");
    await Assert.That(order[1]).IsEqualTo(standard[0]);
  }

  [Test]
  public async Task ClaimWork_ReturnsThePriorityAndArrivalOfHeldInboxRows_ForTheBatchHooksAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var (stream, rows) = await _seedStreamAsync(conn, TimeSpan.FromMinutes(5), isEvent: true, 150, 50);
    _ = await _claimAsync(conn, instance, limit: 10);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT work_id, priority, received_at FROM claim_work(@inst, 'test', 'test-host', 1, 10, 10000, 300, 0.5, 10, FALSE, NULL) WHERE source = 'inbox'";
    cmd.Parameters.AddWithValue("inst", instance);
    var seen = new Dictionary<Guid, (int Priority, DateTimeOffset ReceivedAt)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        seen[reader.GetGuid(0)] = (reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2));
      }
    }

    await Assert.That(seen.Count).IsEqualTo(2);
    await Assert.That(seen[rows[0]].Priority).IsEqualTo(150);
    await Assert.That(seen[rows[1]].Priority).IsEqualTo(50)
      .Because("the batch hooks fold a stream over the rows the claim returned, so each row carries its number back");
    await Assert.That(DateTimeOffset.UtcNow - seen[rows[0]].ReceivedAt).IsGreaterThan(TimeSpan.FromMinutes(4))
      .Because("and its arrival, so the fold knows how long the stream's oldest row has waited");
  }

  [Test]
  public async Task ClaimOrphanedPerspectiveEvents_TakesTheMostUrgentStreamsFirstAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.CreateVersion7();
    await _registerInstanceAsync(conn, instance);
    var streams = new List<(Guid Stream, int Priority)>();
    for (var i = 0; i < 3; i++) {
      streams.Add((await _seedPerspectiveEventAsync(conn, 150, TimeSpan.FromMinutes(5)), 150));
    }
    var urgentStream = await _seedPerspectiveEventAsync(conn, 50, TimeSpan.FromSeconds(1));

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stream_id FROM claim_orphaned_perspective_events(@inst, NOW() + INTERVAL '5 minutes', NOW(), 1, 0, 1)";
    cmd.Parameters.AddWithValue("inst", instance);
    var claimedStreams = new List<Guid>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        claimedStreams.Add(reader.GetGuid(0));
      }
    }

    await Assert.That(claimedStreams.Distinct().Single()).IsEqualTo(urgentStream)
      .Because("with room for one stream the perspective claim takes the most urgent one, so an interactive event's projection is not queued behind three standard streams");
  }

  private static async Task<Guid> _seedPerspectiveEventAsync(NpgsqlConnection conn, int priority, TimeSpan age) {
    var streamId = Guid.CreateVersion7();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, instance_id, lease_expiry, priority)
      VALUES (gen_random_uuid(), @sid, 'BucketTestPerspective', gen_random_uuid(), 0, 1, 0, NOW() - @age, NULL, NULL, @priority)
      """;
    cmd.Parameters.AddWithValue("sid", streamId);
    cmd.Parameters.AddWithValue("age", age);
    cmd.Parameters.AddWithValue("priority", priority);
    await cmd.ExecuteNonQueryAsync();
    return streamId;
  }
}
