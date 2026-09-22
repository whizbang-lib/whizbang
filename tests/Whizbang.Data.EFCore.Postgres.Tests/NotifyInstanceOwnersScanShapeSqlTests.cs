using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That ringing a doorbell costs what the doorbell carries, never what the streams behind it have
/// accumulated.
/// </summary>
/// <remarks>
/// <para>
/// <c>notify_instance_owners</c> picks the deterministic target for an unclaimed stream from the
/// partition number, and it used to recover that number by reading the queue table: a
/// <c>WHERE stream_id = ANY(...)</c> with no status predicate, which no partial index can serve, so
/// every ring read every row those streams had ever written. Measured on a deployed fleet during a
/// bulk import, the three branches together turned a few tens of thousands of inserted rows into
/// hundreds of millions of sequentially read tuples, and the doorbell, not the insert, was where the
/// store spent its time.
/// </para>
/// <para>
/// The number never had to be read. <c>compute_partition</c> is declared <c>IMMUTABLE</c> and is a
/// total function of the stream id, and it is the only thing that ever writes
/// <c>partition_number</c>, so the target is computable from the argument the caller already passed.
/// These tests assert the cost, not the wall clock: the tuples a ring reads sequentially from each
/// queue table, which is zero when the number is computed and the whole table when it is read.
/// </para>
/// <para>
/// The counters live in <c>pg_stat_user_tables</c>, which is per database, and every test here gets
/// a database of its own, so nothing else in the run lands inside a measured window. They are read
/// over a connection of their own because a backend caches the statistics view for the length of its
/// transaction, so a before and an after read on the emitting connection return the same number.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notify-instance-owners</docs>
[Category("Shard1")]
[Category("Integration")]
public class NotifyInstanceOwnersScanShapeSqlTests : EFCoreTestBase {
  /// <summary>Rows the stream has already settled: the history a ring must not read.</summary>
  private const int SETTLED_ROWS = 20_000;

  /// <summary>
  /// Tuples one ring may read sequentially from one queue table. A ring that computes its target
  /// reads none at all; one that reads the stored number reads every row of every stream named, so
  /// any ceiling well below <see cref="SETTLED_ROWS"/> separates the two. The allowance is for the
  /// small bookkeeping tables the function legitimately reads, never for the queue table itself.
  /// </summary>
  private const long TUPLE_CEILING = 1_000;

  private const string OUTBOX = "wh_outbox";
  private const string INBOX = "wh_inbox";
  private const string PERSPECTIVE_EVENTS = "wh_perspective_events";

  /// <summary>
  /// A ring for a stream with a settled outbox history reads none of that history.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task NotifyInstanceOwners_OutboxStreamWithASettledHistory_DoesNotReadItAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    await _registerInstanceAsync(conn, new Guid("00000000-0000-0000-0000-00000000000a"));
    var streamId = Guid.NewGuid();
    await _fillOutboxAsync(conn, streamId);

    var read = await _tuplesReadWhileRingingAsync(conn, OUTBOX, "outbox", streamId);

    await Assert.That(read).IsLessThan(TUPLE_CEILING)
      .Because($"one doorbell ring read {read} tuples of {OUTBOX} sequentially for a single stream carrying "
        + $"{SETTLED_ROWS} settled rows; the ring needs the stream's partition number, which compute_partition "
        + "yields from the stream id alone, and reading it out of the queue table instead makes every ring cost "
        + "what the stream has ever written, on a table no partial index can serve because the lookup carries no "
        + "status predicate");
  }

  /// <summary>
  /// A ring for a stream with a settled inbox history reads none of that history.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task NotifyInstanceOwners_InboxStreamWithASettledHistory_DoesNotReadItAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    await _registerInstanceAsync(conn, new Guid("00000000-0000-0000-0000-00000000000a"));
    var streamId = Guid.NewGuid();
    await _fillInboxAsync(conn, streamId);

    var read = await _tuplesReadWhileRingingAsync(conn, INBOX, "inbox", streamId);

    await Assert.That(read).IsLessThan(TUPLE_CEILING)
      .Because($"one doorbell ring read {read} tuples of {INBOX} sequentially for a single stream carrying "
        + $"{SETTLED_ROWS} settled rows; the inbox branch carried the same defect as the outbox one, because the "
        + "three branches differed only in which table they read the partition number out of");
  }

  /// <summary>
  /// A ring for a stream with a settled perspective-event history reads none of that history.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task NotifyInstanceOwners_PerspectiveStreamWithASettledHistory_DoesNotReadItAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    await _registerInstanceAsync(conn, new Guid("00000000-0000-0000-0000-00000000000a"));
    var streamId = Guid.NewGuid();
    await _fillPerspectiveEventsAsync(conn, streamId);

    var read = await _tuplesReadWhileRingingAsync(conn, PERSPECTIVE_EVENTS, "perspective", streamId);

    await Assert.That(read).IsLessThan(TUPLE_CEILING)
      .Because($"one doorbell ring read {read} tuples of {PERSPECTIVE_EVENTS} sequentially for a single stream "
        + $"carrying {SETTLED_ROWS} settled rows; the perspective branch carried the same defect as the other two");
  }

  /// <summary>
  /// A stream the caller names that has no row in the queue table still wakes its deterministic
  /// owner.
  /// </summary>
  /// <remarks>
  /// This is a deliberate change of behavior, pinned here so it is a decision and not an accident.
  /// Recovering the partition number from the table made "no row for this stream" mean "notify
  /// nobody", which is the opposite of what the branch is for: the branch exists to wake the
  /// instance that would claim a stream nothing has claimed yet. Computing the number from the
  /// stream id gives every stream the caller names its deterministic owner, whatever the table
  /// happens to hold at that instant.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task NotifyInstanceOwners_StreamWithNoQueueRow_StillWakesItsDeterministicOwnerAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    // Sequential ids so the ROW_NUMBER OVER (ORDER BY instance_id) ranks are predictable.
    var instanceA = new Guid("00000000-0000-0000-0000-00000000000a");
    var instanceB = new Guid("00000000-0000-0000-0000-00000000000b");
    var instanceC = new Guid("00000000-0000-0000-0000-00000000000c");
    await _registerInstanceAsync(conn, instanceA);
    await _registerInstanceAsync(conn, instanceB);
    await _registerInstanceAsync(conn, instanceC);

    // A stream with nothing stored under it anywhere: the state a producer's doorbell reaches when
    // the rows it announces are still uncommitted to the reader, or already drained.
    var streamId = Guid.NewGuid();
    var expected = await _deterministicOwnerAsync(conn, streamId, [instanceA, instanceB, instanceC]);

    var received = await _captureNotificationsAsync(conn, [instanceA, instanceB, instanceC], async () => {
      await using var emit = conn.CreateCommand();
      emit.CommandText = "SELECT notify_instance_owners('outbox', ARRAY[@sid]::uuid[])";
      emit.Parameters.AddWithValue("sid", streamId);
      await emit.ExecuteNonQueryAsync(cancellationToken);
    });

    await Assert.That(received.Count).IsEqualTo(1)
      .Because("the deterministic branch exists to wake the instance that would claim a stream nobody has "
        + "claimed, and a stream with no stored row is exactly that case; deriving the target from rows in the "
        + "queue table made the branch fall silent precisely when it was needed");
    await Assert.That(received[0].Channel).IsEqualTo($"wh_work_i_{expected}")
      .Because("the target is the instance whose rank equals compute_partition(stream_id) modulo the live count, "
        + "the same assignment the claim uses, so the instance woken is the one that would take the work");
  }

  // --- helpers ---

  private static async Task<NpgsqlConnection> _openAsync(
      WorkCoordinationDbContext dbContext, CancellationToken cancellationToken) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    return conn;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, @hb, @hb, '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = EXCLUDED.last_heartbeat_at";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.Add(new NpgsqlParameter("hb", NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.UtcNow });
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// The instance the partition assignment names for a stream: the one whose rank equals the
  /// stream's partition number modulo the live instance count. Asked of the database so the test
  /// states the rule rather than a hard-coded answer for one stream id.
  /// </summary>
  private static async Task<Guid> _deterministicOwnerAsync(
      NpgsqlConnection conn, Guid streamId, IReadOnlyList<Guid> live) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      WITH ranked AS (
        SELECT instance_id, (ROW_NUMBER() OVER (ORDER BY instance_id) - 1)::INTEGER AS rank
        FROM wh_service_instances
        WHERE last_heartbeat_at > NOW() - INTERVAL '30 seconds'
      )
      SELECT instance_id FROM ranked
      WHERE rank = compute_partition(@sid) % (SELECT count(*)::INTEGER FROM ranked)";
    cmd.Parameters.AddWithValue("sid", streamId);
    var owner = (Guid?)await cmd.ExecuteScalarAsync();
    return owner ?? live[0];
  }

  /// <summary>
  /// A stream whose rows are settled, with the partition number the system's own writers stamp, and
  /// no entry in the stream ledger, so the ring takes the deterministic-target branch.
  /// </summary>
  private static async Task _fillOutboxAsync(NpgsqlConnection conn, Guid streamId) {
    await using var fill = conn.CreateCommand();
    fill.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
         stream_id, partition_number, processed_at)
      SELECT gen_random_uuid(), 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(),
             @sid, compute_partition(@sid), NOW()
      FROM generate_series(1, @n);
      DELETE FROM wh_active_streams WHERE stream_id = @sid;
      ANALYZE wh_outbox;";
    fill.Parameters.AddWithValue("sid", streamId);
    fill.Parameters.AddWithValue("n", SETTLED_ROWS);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }

  private static async Task _fillInboxAsync(NpgsqlConnection conn, Guid streamId) {
    await using var fill = conn.CreateCommand();
    fill.CommandText = @"
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at,
           stream_id, is_event)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{}', '{}', NOW(),
               @sid, TRUE
        FROM generate_series(1, @n)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event,
         status, attempts, partition_number, processed_at)
      SELECT message_id, stream_id, received_at, priority, is_event,
             0, 0, compute_partition(@sid), NOW() FROM m;
      DELETE FROM wh_active_streams WHERE stream_id = @sid;
      ANALYZE wh_inbox;";
    fill.Parameters.AddWithValue("sid", streamId);
    fill.Parameters.AddWithValue("n", SETTLED_ROWS);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }

  private static async Task _fillPerspectiveEventsAsync(NpgsqlConnection conn, Guid streamId) {
    await using var fill = conn.CreateCommand();
    fill.CommandText = @"
      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number, processed_at)
      SELECT @sid, 'TestPerspective', gen_random_uuid(), 0, 0, NOW(), compute_partition(@sid), NOW()
      FROM generate_series(1, @n);
      DELETE FROM wh_active_streams WHERE stream_id = @sid;
      ANALYZE wh_perspective_events;";
    fill.Parameters.AddWithValue("sid", streamId);
    fill.Parameters.AddWithValue("n", SETTLED_ROWS);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Tuples the named table gives up to sequential scans while one doorbell is rung for one stream.
  /// </summary>
  private async Task<long> _tuplesReadWhileRingingAsync(
      NpgsqlConnection conn, string table, string kind, Guid streamId) {
    var before = await _sequentialTuplesReadAsync(conn, table);
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT notify_instance_owners(@kind, ARRAY[@sid]::uuid[])";
      emit.Parameters.AddWithValue(nameof(kind), kind);
      emit.Parameters.AddWithValue("sid", streamId);
      await emit.ExecuteNonQueryAsync();
    }
    return await _sequentialTuplesReadAsync(conn, table) - before;
  }

  /// <summary>
  /// Tuples read by sequential scans of one table, after the working backend has flushed its
  /// counters, over a connection of its own: a backend caches this view for the length of its
  /// transaction, so reading it twice from the emitting connection returns one number twice.
  /// </summary>
  private async Task<long> _sequentialTuplesReadAsync(NpgsqlConnection reporter, string table) {
    await using (var flush = reporter.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush()";
      await flush.ExecuteNonQueryAsync();
    }
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync();
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = "SELECT seq_tup_read FROM pg_stat_user_tables WHERE relname = @t";
    cmd.Parameters.AddWithValue("t", table);
    return (long?)await cmd.ExecuteScalarAsync() ?? 0L;
  }

  private static async Task<List<(string Channel, string Payload)>> _captureNotificationsAsync(
      NpgsqlConnection conn, IReadOnlyList<Guid> instancesToListen, Func<Task> emit) {
    var received = new List<(string Channel, string Payload)>();
    void handler(object? _, NpgsqlNotificationEventArgs args) => received.Add((args.Channel, args.Payload));
    conn.Notification += handler;
    try {
      foreach (var instance in instancesToListen) {
        await using var listen = conn.CreateCommand();
        listen.CommandText = $"LISTEN \"wh_work_i_{instance}\"";
        await listen.ExecuteNonQueryAsync();
      }
      await emit();
      // 146 (#720): the functions under test queue their doorbells and the caller rings after the
      // commit, which is what the driver's DoorbellRinger does.
      await using (var ring = conn.CreateCommand()) {
        ring.CommandText = "SELECT ring_doorbells()";
        _ = await ring.ExecuteScalarAsync();
      }
      await using var ping = conn.CreateCommand();
      ping.CommandText = "SELECT 1";
      _ = await ping.ExecuteScalarAsync();
    } finally {
      conn.Notification -= handler;
      foreach (var instance in instancesToListen) {
        await using var unlisten = conn.CreateCommand();
        unlisten.CommandText = $"UNLISTEN \"wh_work_i_{instance}\"";
        await unlisten.ExecuteNonQueryAsync();
      }
    }
    return received;
  }
}
