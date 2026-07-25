using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for the new <c>claim_work</c> SQL function — the focused replacement for the
/// claim portion of the deprecated <c>process_work_batch</c> monolith.
/// Foundational contract tests for Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public class ClaimWorkSqlTests : EFCoreTestBase {

  /// <summary>
  /// The function must exist in the public schema after migrations apply.
  /// Other claim_work tests depend on this; if this fails, the function isn't being created.
  /// </summary>
  [Test]
  public async Task ClaimWork_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = ((NpgsqlConnection)connection).CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT 1 FROM pg_proc
        WHERE proname = 'claim_work'
          AND pronamespace = 'public'::regnamespace
      );";

    var exists = (bool)(await command.ExecuteScalarAsync())!;

    await Assert.That(exists).IsTrue();
  }

  /// <summary>
  /// The function signature must include the parameters the C# coordinator binds to.
  /// Asserting the signature catches both missing-function and wrong-shape regressions.
  /// </summary>
  [Test]
  public async Task ClaimWork_HasExpectedSignatureAsync() {
    await using var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = ((NpgsqlConnection)connection).CreateCommand();
    command.CommandText = @"
      SELECT pg_get_function_arguments(oid)::text
      FROM pg_proc
      WHERE proname = 'claim_work'
        AND pronamespace = 'public'::regnamespace
      LIMIT 1;";

    var args = (string?)await command.ExecuteScalarAsync();

    await Assert.That(args).IsNotNull();
    await Assert.That(args!).Contains("p_instance_id uuid");
    await Assert.That(args!).Contains("p_service_name text");
    await Assert.That(args!).Contains("p_max_streams integer");
    await Assert.That(args!).Contains("p_partition_count integer");
    await Assert.That(args!).Contains("p_lease_seconds integer");
  }

  /// <summary>
  /// When wh_outbox has an unprocessed message and the calling instance is heartbeating,
  /// claim_work must claim that message and return it as a row with source='outbox'.
  /// This is the basic happy-path opposite of the empty-queue short-circuit.
  /// </summary>
  [Test]
  public async Task ClaimWork_OutboxHasUnprocessedWork_ReturnsThatWorkAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // Heartbeat the test instance so calculate_instance_rank finds it.
    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Insert one unprocessed outbox row (instance_id NULL = unowned, ready to claim).
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    // Call claim_work.
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT source, work_id, work_stream_id FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => 100,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);

    var rows = new List<(string source, Guid workId, Guid streamId)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2)));
      }
    }

    await Assert.That(rows.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(rows.Any(r => r.source == "outbox" && r.workId == messageId)).IsTrue();
  }

  /// <summary>
  /// claim_work must respect the p_max_streams cap. Insert more rows than the cap
  /// allows; the function must return at most p_max_streams rows in the result set.
  /// Without the cap, drain-mode pulls become unbounded and starve other workloads.
  /// </summary>
  [Test]
  public async Task ClaimWork_RespectsMaxStreamsCapAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    const int cap = 3;
    const int insertCount = 10;

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Insert 10 unprocessed outbox rows, each on its own stream.
    for (var i = 0; i < insertCount; i++) {
      await using var ins = connection.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", Guid.NewGuid());
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => @cap,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("cap", cap);

    var returned = (long)(await cmd.ExecuteScalarAsync())!;

    await Assert.That(returned).IsLessThanOrEqualTo((long)cap);
    await Assert.That(returned).IsGreaterThan(0L);  // sanity: it claimed at least something
  }

  /// <summary>
  /// claim_work must atomically lock claimed rows to the calling instance:
  /// after the call, every row returned has instance_id set to the caller and
  /// lease_expiry > now. Without this, two instances could double-claim the same work.
  /// </summary>
  [Test]
  public async Task ClaimWork_LocksClaimedRowsToCallerAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    await using (var claim = connection.CreateCommand()) {
      claim.CommandText = @"
        SELECT count(*) FROM claim_work(
          p_instance_id => @id,
          p_service_name => 'test',
          p_host_name => 'test-host',
          p_process_id => 1,
          p_max_streams => 100,
          p_partition_count => 10000,
          p_lease_seconds => 300
        )";
      claim.Parameters.AddWithValue("id", instanceId);
      _ = await claim.ExecuteScalarAsync();
    }

    // Verify the row is now locked to the calling instance with a future lease.
    await using var verify = connection.CreateCommand();
    verify.CommandText = @"
      SELECT instance_id, lease_expiry > NOW() AS lease_in_future
      FROM wh_outbox WHERE message_id = @msg";
    verify.Parameters.AddWithValue("msg", messageId);

    await using var reader = await verify.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    var lockedTo = reader.GetGuid(0);
    var leaseInFuture = reader.GetBoolean(1);

    await Assert.That(lockedTo).IsEqualTo(instanceId);
    await Assert.That(leaseInFuture).IsTrue();
  }

  /// <summary>
  /// claim_work must also claim and return inbox work, mirroring outbox semantics.
  /// Inbox uses 'handler_name' (not 'destination') and 'received_at' (not 'created_at')
  /// — same shape, different column names. Source returned as 'inbox'.
  /// </summary>
  [Test]
  public async Task ClaimWork_InboxHasUnprocessedWork_ReturnsThatWorkAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 0, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT source, work_id, work_stream_id FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => 100,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);

    var rows = new List<(string source, Guid workId, Guid streamId)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2)));
      }
    }

    await Assert.That(rows.Any(r => r.source == "inbox" && r.workId == messageId)).IsTrue();
  }

  /// <summary>
  /// claim_work must claim and return perspective work as one row per distinct stream
  /// (source='perspective_stream'). The C# PerspectiveWorker fans out from there to fetch
  /// the actual events. Two-tier fairness ordering is a future optimization; this test
  /// just asserts the basic stream-row return.
  /// </summary>
  [Test]
  public async Task ClaimWork_PerspectiveHasUnprocessedWork_ReturnsThatStreamAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@stream, 'TestPerspective', @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("eid", eventId);
      await ins.ExecuteNonQueryAsync();
    }

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT source, work_stream_id FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => 100,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);

    var rows = new List<(string source, Guid streamId)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetString(0), reader.GetGuid(1)));
      }
    }

    await Assert.That(rows.Any(r => r.source == "perspective_stream" && r.streamId == streamId)).IsTrue();
  }

  /// <summary>
  /// Two-tier perspective fairness: streams with ≤ 100 pending events come BEFORE streams
  /// with > 100 pending events in the claim_work return. Without this, a single huge stream
  /// could starve many small streams behind it on every claim cycle. This test seeds one
  /// large stream (200 events) and one small stream (1 event), then asserts the small one
  /// is returned first.
  /// </summary>
  [Test]
  public async Task ClaimWork_PerspectiveTwoTierFairness_SmallStreamReturnsBeforeLargeAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var largeStreamId = Guid.NewGuid();
    var smallStreamId = Guid.NewGuid();

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Seed 200 events on the large stream + 1 event on the small stream, all owned by us.
    await using (var bulk = connection.CreateCommand()) {
      bulk.CommandText = @"
        INSERT INTO wh_perspective_events
          (event_work_id, event_id, stream_id, perspective_name, status, attempts, created_at,
           instance_id, lease_expiry)
        SELECT gen_random_uuid(), gen_random_uuid(), @largeStream, 'TestPerspective', 0, 0, NOW(),
               @inst, NOW() + INTERVAL '5 minutes'
        FROM generate_series(1, 200);

        INSERT INTO wh_perspective_events
          (event_work_id, event_id, stream_id, perspective_name, status, attempts, created_at,
           instance_id, lease_expiry)
        VALUES (gen_random_uuid(), gen_random_uuid(), @smallStream, 'TestPerspective', 0, 0, NOW(),
                @inst, NOW() + INTERVAL '5 minutes');";
      bulk.Parameters.AddWithValue("largeStream", largeStreamId);
      bulk.Parameters.AddWithValue("smallStream", smallStreamId);
      bulk.Parameters.AddWithValue("inst", instanceId);
      await bulk.ExecuteNonQueryAsync();
    }

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT source, work_stream_id FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => 100,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);

    var perspectiveStreams = new List<Guid>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        if (reader.GetString(0) == "perspective_stream") {
          perspectiveStreams.Add(reader.GetGuid(1));
        }
      }
    }

    var smallIdx = perspectiveStreams.IndexOf(smallStreamId);
    var largeIdx = perspectiveStreams.IndexOf(largeStreamId);

    await Assert.That(smallIdx).IsGreaterThanOrEqualTo(0)
      .Because("Small stream must appear in the result set");
    await Assert.That(smallIdx).IsLessThan(largeIdx == -1 ? int.MaxValue : largeIdx)
      .Because("Two-tier fairness — small stream must come BEFORE large stream");
  }

  /// <summary>
  /// claim_work must claim and return receptor work (wh_receptor_processing rows where
  /// the calling instance owns the lease + completed_at IS NULL). Source returned as 'receptor';
  /// work_id is the processing row's <c>id</c>, not a message_id.
  /// </summary>
  [Test]
  public async Task ClaimWork_ReceptorHasUnprocessedWork_ReturnsThatWorkAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var processingId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Receptor row has FK to wh_event_store(event_id), so seed an event row first.
    await using (var seedEvent = connection.CreateCommand()) {
      seedEvent.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
        VALUES (@eid, @stream, @stream, 'Test', 'TestEvent', 1, NOW())";
      seedEvent.Parameters.AddWithValue("eid", eventId);
      seedEvent.Parameters.AddWithValue("stream", streamId);
      await seedEvent.ExecuteNonQueryAsync();
    }

    // Insert a receptor row already leased to this instance with completed_at NULL.
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_receptor_processing
          (id, event_id, receptor_name, stream_id, partition_number, status, attempts,
           instance_id, lease_expiry, started_at, claimed_at)
        VALUES (@pid, @eid, 'TestReceptor', @stream, 0, 0, 0,
                @inst, NOW() + INTERVAL '5 minutes', NOW(), NOW())";
      ins.Parameters.AddWithValue("pid", processingId);
      ins.Parameters.AddWithValue("eid", eventId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("inst", instanceId);
      await ins.ExecuteNonQueryAsync();
    }

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT source, work_id, work_stream_id FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => 100,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);

    var rows = new List<(string source, Guid workId, Guid streamId)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2)));
      }
    }

    await Assert.That(rows.Any(r => r.source == "receptor" && r.workId == processingId && r.streamId == streamId)).IsTrue()
      .Because("claim_work must return the receptor row owned by this instance");
  }

  /// <summary>
  /// When claim_work returns a full batch (rows == p_max_streams), it must RAISE NOTICE
  /// 'whizbang.has_more=true' so the C# claim worker can skip its wait and re-poll
  /// immediately. This is the in-band drain-mode signal — survives pgbouncer because
  /// it's a protocol message on the same connection that issued the query.
  /// </summary>
  [Test]
  public async Task ClaimWork_FullBatch_RaisesHasMoreNoticeAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    const int cap = 3;

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Insert 5 outbox rows so claim_work fills its 3-row cap with 2 left over.
    for (var i = 0; i < 5; i++) {
      await using var ins = connection.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", Guid.NewGuid());
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    var notices = new List<string>();
    void OnNotice(object? sender, NpgsqlNoticeEventArgs e) {
      notices.Add(e.Notice.MessageText);
    }
    connection.Notice += OnNotice;
    try {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = @"
        SELECT count(*) FROM claim_work(
          p_instance_id => @id,
          p_service_name => 'test',
          p_host_name => 'test-host',
          p_process_id => 1,
          p_max_streams => @cap,
          p_partition_count => 10000,
          p_lease_seconds => 300
        )";
      cmd.Parameters.AddWithValue("id", instanceId);
      cmd.Parameters.AddWithValue("cap", cap);
      _ = await cmd.ExecuteScalarAsync();
    } finally {
      connection.Notice -= OnNotice;
    }

    await Assert.That(notices.Any(n => n.Contains("whizbang.has_more=true"))).IsTrue();
  }

  /// <summary>
  /// On empty queues, claim_work must not invoke the orphan-claim sub-functions.
  /// This is the empty-call short-circuit that drops the structural ~17 ms floor to ≤ 1 ms.
  /// Verified via pg_stat_user_functions — claim sub-functions must show zero invocations.
  /// </summary>
  [Test]
  public async Task ClaimWork_EmptyQueues_DoesNotInvokeOrphanClaimSubfunctionsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var npgsql = (NpgsqlConnection)connection;

    // track_functions defaults to 'none' in Postgres — pg_stat_user_functions stays
    // empty without this SET, which would make the IsEqualTo(0L) assertions below
    // trivially pass even if the sub-functions DID run. Lock-in test value depends on
    // tracking actually being on. v0.683 — same pattern used in the per-inner-function
    // guard tests below.
    await using (var track = npgsql.CreateCommand()) {
      track.CommandText = "SET track_functions = 'all';";
      await track.ExecuteNonQueryAsync();
    }

    // Reset pg_stat_user_functions for this database
    await using (var reset = npgsql.CreateCommand()) {
      reset.CommandText = "SELECT pg_stat_reset();";
      await reset.ExecuteNonQueryAsync();
    }

    // Call claim_work against fully-empty queues (fresh test DB)
    await using (var call = npgsql.CreateCommand()) {
      call.CommandText = @"
        SELECT * FROM claim_work(
          p_instance_id => gen_random_uuid(),
          p_service_name => 'test-service',
          p_host_name => 'test-host',
          p_process_id => 1,
          p_max_streams => 100,
          p_partition_count => 10000,
          p_lease_seconds => 300
        );";
      await using var reader = await call.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // drain — empty queues should yield zero rows
      }
    }

    // Flush the async stats collector so call counts are visible in this session.
    await using (var flush = npgsql.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush();";
      await flush.ExecuteNonQueryAsync();
    }

    // Assert orphan-claim sub-functions never ran
    await using var check = npgsql.CreateCommand();
    check.CommandText = @"
      SELECT funcname, COALESCE(SUM(calls), 0) AS calls
      FROM pg_stat_user_functions
      WHERE funcname IN (
        'claim_orphaned_outbox',
        'claim_orphaned_inbox',
        'claim_orphaned_receptor_work',
        'claim_orphaned_perspective_events'
      )
      GROUP BY funcname
      ORDER BY funcname;";

    await using var reader2 = await check.ExecuteReaderAsync();
    while (await reader2.ReadAsync()) {
      _ = reader2.GetString(0);
      var calls = reader2.GetInt64(1);
      await Assert.That(calls).IsEqualTo(0L);
    }
  }

  /// <summary>
  /// v0.661 — negative invariant for the drain-mode hint. When claim_work returns
  /// FEWER rows than the requested cap (i.e., the instance has no more eligible
  /// work after this batch), it MUST NOT raise the <c>whizbang.has_more=true</c>
  /// NOTICE. Otherwise the C# claim worker would skip its NOTIFY-wait cycle and
  /// hot-loop the empty path, defeating zero-idle-polling.
  /// </summary>
  /// <remarks>
  /// Paired with <see cref="ClaimWork_FullBatch_RaisesHasMoreNoticeAsync"/>. Both
  /// must pass: NOTICE fires when there's more work, NOTICE does NOT fire when
  /// caught up. The implementation refactor that replaces the four
  /// <c>COUNT(*)</c> drain-mode-hint queries with <c>GET DIAGNOSTICS ROW_COUNT</c>
  /// after each <c>RETURN QUERY</c> must preserve both invariants.
  /// </remarks>
  [Test]
  public async Task ClaimWork_PartialBatch_DoesNotRaiseHasMoreNoticeAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    const int cap = 10;

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Insert only 3 outbox rows — well below the cap of 10 — so claim_work can
    // drain the queue completely. No more work after this batch.
    for (var i = 0; i < 3; i++) {
      await using var ins = connection.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", Guid.NewGuid());
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    var notices = new List<string>();
    void OnNotice(object? sender, NpgsqlNoticeEventArgs e) {
      notices.Add(e.Notice.MessageText);
    }
    connection.Notice += OnNotice;
    try {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = @"
        SELECT count(*) FROM claim_work(
          p_instance_id => @id,
          p_service_name => 'test',
          p_host_name => 'test-host',
          p_process_id => 1,
          p_max_streams => @cap,
          p_partition_count => 10000,
          p_lease_seconds => 300
        )";
      cmd.Parameters.AddWithValue("id", instanceId);
      cmd.Parameters.AddWithValue("cap", cap);
      _ = await cmd.ExecuteScalarAsync();
    } finally {
      connection.Notice -= OnNotice;
    }

    await Assert.That(notices.Any(n => n.Contains("whizbang.has_more=true"))).IsFalse()
      .Because("Only 3 outbox rows with cap=10 — the instance has fully drained. Raising whizbang.has_more=true here would make the C# claim worker hot-loop the empty path, defeating zero-idle-polling.");
  }

  // ============================================================================
  // v0.683 — per-inner-function guard lock-ins.
  //
  // The five IF EXISTS guards added to claim_work in v0.683 are an
  // architectural invariant — without them, a consumer paid 22.4% of its DB
  // time on always-runs-the-scan orphan-claim / emit_chain calls under steady
  // load. Future refactors of claim_work must preserve the "skip the inner
  // function when the corresponding queue has no eligible rows" behavior, or
  // the regression silently re-appears in production.
  //
  // Pattern: pg_stat_reset() before invoking claim_work, then verify call
  // counts in pg_stat_user_functions. Mirrors
  // ClaimWork_EmptyQueues_DoesNotInvokeOrphanClaimSubfunctionsAsync above.
  // ============================================================================

  /// <summary>
  /// v0.683 lock-in — when ONLY the outbox queue has work, claim_work must
  /// skip the other three orphan-claim sub-functions AND skip emit_chain
  /// (no inbox rows means no event-store backfill candidates). Only
  /// claim_orphaned_outbox runs.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop</docs>
  [Test]
  public async Task ClaimWork_OnlyOutboxHasWork_SkipsOtherInnerFunctionsAsync() {
    var counts = await _runClaimWorkAndCountInnerCallsAsync(seedOutbox: 1, seedInbox: 0, seedPerspective: 0, seedReceptor: 0);

    await Assert.That(counts.OutboxCalls).IsEqualTo(1L)
      .Because("v0.683 outbox guard MUST fire — there's an unprocessed orphan outbox row.");
    await Assert.That(counts.InboxCalls).IsEqualTo(0L)
      .Because("v0.683 inbox guard MUST skip — no unprocessed orphan inbox rows.");
    await Assert.That(counts.PerspectiveCalls).IsEqualTo(0L)
      .Because("v0.683 perspective_events guard MUST skip — no unprocessed orphan perspective_event rows.");
    await Assert.That(counts.ReceptorCalls).IsEqualTo(0L)
      .Because("v0.683 receptor_work guard MUST skip — no uncompleted receptor_processing rows.");
    await Assert.That(counts.EmitChainCalls).IsEqualTo(0L)
      .Because("v0.683 emit_chain guard MUST skip — no unprocessed inbox event row with stream_id whose event_id is missing from wh_event_store.");
  }

  /// <summary>
  /// v0.683 lock-in — when ONLY the inbox queue has work AND the inbox row's
  /// event_id is NOT in wh_event_store, both claim_orphaned_inbox AND
  /// emit_chain MUST fire; the other three orphan-claim guards skip.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop</docs>
  [Test]
  public async Task ClaimWork_OnlyInboxHasWork_FiresInboxClaimAndEmitChainAsync() {
    var counts = await _runClaimWorkAndCountInnerCallsAsync(seedOutbox: 0, seedInbox: 1, seedPerspective: 0, seedReceptor: 0);

    await Assert.That(counts.InboxCalls).IsEqualTo(1L)
      .Because("v0.683 inbox guard MUST fire — there's an unprocessed orphan inbox row.");
    await Assert.That(counts.EmitChainCalls).IsEqualTo(1L)
      .Because("v0.683 emit_chain guard MUST fire — the inbox event row's message_id is not yet in wh_event_store.");
    await Assert.That(counts.OutboxCalls).IsEqualTo(0L)
      .Because("v0.683 outbox guard MUST skip — no unprocessed orphan outbox rows.");
    await Assert.That(counts.PerspectiveCalls).IsEqualTo(0L)
      .Because("v0.683 perspective_events guard MUST skip — no unprocessed orphan perspective_event rows.");
    await Assert.That(counts.ReceptorCalls).IsEqualTo(0L)
      .Because("v0.683 receptor_work guard MUST skip — no uncompleted receptor_processing rows.");
  }

  /// <summary>
  /// v0.683 lock-in — when ONLY the perspective_events queue has work, only
  /// claim_orphaned_perspective_events runs. The other three orphan-claim
  /// guards AND the emit_chain guard skip.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop</docs>
  [Test]
  public async Task ClaimWork_OnlyPerspectiveEventsHasWork_SkipsOtherInnerFunctionsAsync() {
    var counts = await _runClaimWorkAndCountInnerCallsAsync(seedOutbox: 0, seedInbox: 0, seedPerspective: 1, seedReceptor: 0);

    await Assert.That(counts.PerspectiveCalls).IsEqualTo(1L)
      .Because("v0.683 perspective_events guard MUST fire — there's an unprocessed orphan perspective_event row.");
    await Assert.That(counts.OutboxCalls).IsEqualTo(0L);
    await Assert.That(counts.InboxCalls).IsEqualTo(0L);
    await Assert.That(counts.ReceptorCalls).IsEqualTo(0L);
    await Assert.That(counts.EmitChainCalls).IsEqualTo(0L);
  }

  /// <summary>
  /// v0.684 lock-in — emit_chain MUST be idempotent when called against
  /// an inbox event row whose message_id is already in wh_event_store.
  /// The v0.683 guard intentionally does NOT pre-check wh_event_store
  /// (a production PM measurement showed the wrapping NOT EXISTS
  /// at 42 ms mean — overwhelming the savings from skipping the inner call).
  /// Instead, emit_chain's own internal NOT EXISTS check filters out
  /// already-emitted rows, and ON CONFLICT DO NOTHING swallows any race-
  /// driven duplicate inserts. This test exercises that idempotency on
  /// the hot path. Renamed from the original v2-guard-skip test now that
  /// the v0.684 guard is back to the simpler v1 form.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop</docs>
  [Test]
  public async Task ClaimWork_EmitChain_IsIdempotentWhenEventsAlreadyInStoreAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    // Heartbeat.
    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    // Pre-emit the event into wh_event_store so the inbox row's NOT EXISTS
    // predicate returns false.
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type,
           scope, version, created_at)
        VALUES (@eid, @stream, @stream, 'Test', 'Test', NULL, 1, NOW())";
      ins.Parameters.AddWithValue("eid", eventId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    // Insert the matching inbox event row — owned by this instance, eligible
    // by all four inbox predicates, but its event_id is already in wh_event_store.
    await using (var inbox = connection.CreateCommand()) {
      inbox.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, scope,
           stream_id, instance_id, lease_expiry, processed_at, is_event,
           status, attempts, received_at, partition_number)
        VALUES (@mid, 'TestHandler', 'Test', '{}'::jsonb, '{}'::jsonb, NULL,
                @stream, @inst, NOW() + INTERVAL '5 minutes', NULL, true,
                0, 0, NOW(), 1)";
      inbox.Parameters.AddWithValue("mid", eventId);
      inbox.Parameters.AddWithValue("stream", streamId);
      inbox.Parameters.AddWithValue("inst", instanceId);
      await inbox.ExecuteNonQueryAsync();
    }

    await using (var track = connection.CreateCommand()) {
      track.CommandText = "SET track_functions = 'all';";
      await track.ExecuteNonQueryAsync();
    }

    await using (var reset = connection.CreateCommand()) {
      reset.CommandText = "SELECT pg_stat_reset();";
      await reset.ExecuteNonQueryAsync();
    }

    await using (var call = connection.CreateCommand()) {
      call.CommandText = @"
        SELECT * FROM claim_work(
          p_instance_id => @id,
          p_service_name => 'test',
          p_host_name => 'test-host',
          p_process_id => 1,
          p_max_streams => 100,
          p_partition_count => 10000,
          p_lease_seconds => 300
        )";
      call.Parameters.AddWithValue("id", instanceId);
      await using var reader = await call.ExecuteReaderAsync();
      while (await reader.ReadAsync()) { }
    }

    await using (var flush = connection.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush();";
      await flush.ExecuteNonQueryAsync();
    }

    var emitCalls = await _scalarLongAsync(connection,
      "SELECT COALESCE(SUM(calls), 0) FROM pg_stat_user_functions WHERE funcname = '_emit_event_store_chain_for_inbox'");

    var afterCount = await _scalarLongAsync(connection, "SELECT count(*) FROM wh_event_store");

    // v0.684: emit_chain is INVOKED (the simpler v1-style guard only checks that the
    // instance owns at least one unprocessed inbox event row with stream_id — true
    // here) and must idempotently no-op when every row's event_id is already present.
    await Assert.That(emitCalls).IsGreaterThanOrEqualTo(1L)
      .Because("v0.684 reverted the guard to the simpler v1 form, so emit_chain runs when this instance owns any unprocessed inbox event row. A production PM measurement showed the v2 NOT EXISTS predicate at 42 ms mean (~5% of its DB time) — net loss under heavy load.");
    await Assert.That(afterCount).IsEqualTo(1L)
      .Because("emit_chain MUST be idempotent against pre-emitted events — its internal NOT EXISTS check + ON CONFLICT DO NOTHING guarantee no duplicate wh_event_store rows. This is the lock-in invariant that lets v0.684 ship the cheaper guard safely.");
  }

  private async Task<_InnerCallCounts> _runClaimWorkAndCountInnerCallsAsync(
      int seedOutbox, int seedInbox, int seedPerspective, int seedReceptor) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();

    await using (var hb = connection.CreateCommand()) {
      hb.CommandText = @"
        INSERT INTO wh_service_instances
          (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
      hb.Parameters.AddWithValue("id", instanceId);
      await hb.ExecuteNonQueryAsync();
    }

    for (var i = 0; i < seedOutbox; i++) {
      await using var ins = connection.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 0, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", Guid.NewGuid());
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    for (var i = 0; i < seedInbox; i++) {
      await using var ins = connection.CreateCommand();
      // event_data carries a 'p' payload key — _emit_event_store_chain_for_inbox
      // COALESCE-extracts that into wh_event_store.event_data, which is NOT NULL.
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, scope,
           stream_id, instance_id, lease_expiry, processed_at, is_event,
           status, attempts, received_at, partition_number)
        VALUES (@msg, 'TestHandler', 'Test', '{""p"": {}}'::jsonb, '{}'::jsonb, NULL,
                @stream, NULL, NULL, NULL, true,
                0, 0, NOW(), 1)";
      ins.Parameters.AddWithValue("msg", Guid.NewGuid());
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    for (var i = 0; i < seedPerspective; i++) {
      await using var ins = connection.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_perspective_events
          (stream_id, perspective_name, event_id, status, attempts, created_at)
        VALUES (@stream, 'TestPerspective', @eid, 0, 0, NOW())";
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      ins.Parameters.AddWithValue("eid", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    // wh_receptor_processing seed is omitted — receptor work is fed via a
    // separate pipeline not exercised here. seedReceptor stays at 0 in current
    // tests; left as a parameter for future symmetry.
    _ = seedReceptor;

    // track_functions defaults to 'none' in Postgres — pg_stat_user_functions is empty
    // unless tracking is enabled per session. SET on the same connection that runs
    // claim_work so the inner-function calls are recorded.
    await using (var track = connection.CreateCommand()) {
      track.CommandText = "SET track_functions = 'all';";
      await track.ExecuteNonQueryAsync();
    }

    await using (var reset = connection.CreateCommand()) {
      reset.CommandText = "SELECT pg_stat_reset();";
      await reset.ExecuteNonQueryAsync();
    }

    await using (var call = connection.CreateCommand()) {
      call.CommandText = @"
        SELECT * FROM claim_work(
          p_instance_id => @id,
          p_service_name => 'test',
          p_host_name => 'test-host',
          p_process_id => 1,
          p_max_streams => 100,
          p_partition_count => 10000,
          p_lease_seconds => 300
        )";
      call.Parameters.AddWithValue("id", instanceId);
      await using var reader = await call.ExecuteReaderAsync();
      while (await reader.ReadAsync()) { }
    }

    // pg_stat_user_functions is populated by the async stats collector — without an
    // explicit flush the counts aren't visible to the same session that ran the calls.
    await using (var flush = connection.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush();";
      await flush.ExecuteNonQueryAsync();
    }

    return new _InnerCallCounts(
      OutboxCalls: await _scalarLongAsync(connection, "SELECT COALESCE(SUM(calls), 0) FROM pg_stat_user_functions WHERE funcname = 'claim_orphaned_outbox'"),
      InboxCalls: await _scalarLongAsync(connection, "SELECT COALESCE(SUM(calls), 0) FROM pg_stat_user_functions WHERE funcname = 'claim_orphaned_inbox'"),
      PerspectiveCalls: await _scalarLongAsync(connection, "SELECT COALESCE(SUM(calls), 0) FROM pg_stat_user_functions WHERE funcname = 'claim_orphaned_perspective_events'"),
      ReceptorCalls: await _scalarLongAsync(connection, "SELECT COALESCE(SUM(calls), 0) FROM pg_stat_user_functions WHERE funcname = 'claim_orphaned_receptor_work'"),
      EmitChainCalls: await _scalarLongAsync(connection, "SELECT COALESCE(SUM(calls), 0) FROM pg_stat_user_functions WHERE funcname = '_emit_event_store_chain_for_inbox'"));
  }

  private static async Task<long> _scalarLongAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var result = await cmd.ExecuteScalarAsync();
    return result is null or DBNull ? 0L : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  private readonly record struct _InnerCallCounts(
    long OutboxCalls,
    long InboxCalls,
    long PerspectiveCalls,
    long ReceptorCalls,
    long EmitChainCalls);
}
